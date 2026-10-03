using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AiComparison.Models;
using AiComparison.Services.Maf;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AiComparison.Services;

/// <summary>
/// Hybrid AI service built as a Microsoft Agent Framework pipeline:
/// <c>[ChunkMapExecutor: local agent summarizes chunks] --> [Cloud agent synthesizes] --> final summary</c>.
///
/// The local stage runs a <see cref="ChatClientAgent"/> over the document chunks with bounded,
/// order-preserving concurrency; the cloud stage is a second <see cref="ChatClientAgent"/> that
/// streams the synthesized summary. Small documents take a single-pass fast path (no local map),
/// removing an entire model round trip.
/// </summary>
public class HybridAiService : IAiService
{
    private readonly IChatClient _localClient;
    private readonly IChatClient _cloudClient;
    private readonly bool _localIsCloud;

    // Chunk size tuned for Apple Intelligence context limits (~500 words per chunk).
    private const int WordsPerChunk = 500;

    // Bound concurrent local calls. Apple Intelligence serializes on-device, so a small cap keeps
    // ordering simple without throttling; when "local" falls back to the cloud client we cap lower
    // to stay within Azure rate limits while still overlapping a few requests.
    private const int AppleLocalConcurrency = 2;
    private const int CloudFallbackConcurrency = 3;

    // Cap cloud synthesis length to bound total time.
    private const int MaxCloudOutputTokens = 800;

    private const string ChunkInstructions =
        "Summarize this section of a larger document concisely in 2-3 sentences, capturing the key points.";

    private const string CloudSynthesisInstructions =
        """
        You are a summarization assistant. You receive content from a single document, provided either
        as raw text or as an ordered set of section summaries. Produce one coherent, well-structured
        summary that flows naturally, eliminates redundancy, and organizes the information logically.
        """;

    public string Name => "Hybrid AI";
    public string Description => "Local summarizes chunks → Cloud synthesizes (handles large docs)";

    public HybridAiService(IChatClient localClient, IChatClient cloudClient)
    {
        _localClient = localClient;
        _cloudClient = cloudClient;
        _localIsCloud = ReferenceEquals(localClient, cloudClient);
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var localMeta = _localClient.GetService<ChatClientMetadata>();
            var cloudMeta = _cloudClient.GetService<ChatClientMetadata>();
            return localMeta != null && cloudMeta != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SummarizationResult> SummarizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        var inputWordCount = CountWords(text);

        try
        {
            var output = new StringBuilder();

            // Non-streaming path: suppress progress markers so the result is the clean summary only.
            await foreach (var piece in RunPipelineAsync(text, emitProgress: false, _ => { }, cancellationToken))
            {
                output.Append(piece);
            }

            stopwatch.Stop();
            var outputText = output.ToString().Trim();

            return new SummarizationResult
            {
                Text = outputText,
                Benchmark = CreateBenchmark(stopwatch.ElapsedMilliseconds, stopwatch.ElapsedMilliseconds,
                    outputText, memoryBefore, inputWordCount)
            };
        }
        catch (Exception ex)
        {
            return SummarizationResult.Error($"Hybrid AI error: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<string> SummarizeStreamingAsync(
        string text,
        Action<BenchmarkResult>? onBenchmarkUpdate = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        var inputWordCount = CountWords(text);
        var outputBuilder = new StringBuilder();
        long firstTokenLatency = 0;
        var firstTokenSeen = false;
        var tokenCount = 0;

        void OnCloudToken(string token)
        {
            if (!firstTokenSeen)
            {
                firstTokenLatency = stopwatch.ElapsedMilliseconds;
                firstTokenSeen = true;
            }

            outputBuilder.Append(token);
            tokenCount++;

            if (tokenCount % 5 == 0)
            {
                onBenchmarkUpdate?.Invoke(CreateBenchmark(
                    stopwatch.ElapsedMilliseconds, firstTokenLatency, outputBuilder.ToString(),
                    memoryBefore, inputWordCount));
            }
        }

        await foreach (var piece in RunPipelineAsync(text, emitProgress: true, OnCloudToken, cancellationToken))
        {
            yield return piece;
        }

        stopwatch.Stop();
        onBenchmarkUpdate?.Invoke(CreateBenchmark(
            stopwatch.ElapsedMilliseconds, firstTokenSeen ? firstTokenLatency : stopwatch.ElapsedMilliseconds,
            outputBuilder.ToString(), memoryBefore, inputWordCount));
    }

    private async IAsyncEnumerable<string> RunPipelineAsync(
        string text,
        bool emitProgress,
        Action<string> onCloudToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        var localAgent = new ChatClientAgent(_localClient, instructions: ChunkInstructions, name: "LocalChunkSummarizer");
        var cloudAgent = CreateCloudSynthesizer();
        var concurrency = _localIsCloud ? CloudFallbackConcurrency : AppleLocalConcurrency;
        var mapExecutor = new ChunkMapExecutor(localAgent, channel.Writer, concurrency, emitProgress);

        // Own a linked CTS so that if the consumer stops enumerating early (or the caller's token
        // fires), we tear the workflow down instead of leaking an orphaned task writing to a dead channel.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = MafPipeline.RunAsync(mapExecutor, cloudAgent, text, channel.Writer, onCloudToken, cts.Token);

        try
        {
            await foreach (var piece in channel.Reader.ReadAllAsync(cts.Token))
            {
                yield return piece;
            }
        }
        finally
        {
            cts.Cancel();
            await runTask; // RunAsync never throws (errors surface via the channel); observe completion.
        }
    }

    private ChatClientAgent CreateCloudSynthesizer() =>
        new(_cloudClient, new ChatClientAgentOptions
        {
            Name = "CloudSynthesizer",
            ChatOptions = new ChatOptions
            {
                Instructions = CloudSynthesisInstructions,
                MaxOutputTokens = MaxCloudOutputTokens
            }
        });

    /// <summary>
    /// Local map stage: splits the document into chunks, summarizes each chunk with the local agent
    /// (bounded, order-preserving concurrency), then forwards the synthesis prompt to the cloud agent.
    /// Progress markers are written directly to the output channel; raw chunk tokens are never streamed
    /// (they would interleave across concurrent chunks), only ordered completion markers are surfaced.
    /// </summary>
    [SendsMessage(typeof(ChatMessage))]
    [SendsMessage(typeof(TurnToken))]
    private sealed class ChunkMapExecutor : Executor<string>
    {
        private readonly AIAgent _localAgent;
        private readonly ChannelWriter<string> _progress;
        private readonly int _concurrency;
        private readonly bool _emitProgress;

        public ChunkMapExecutor(AIAgent localAgent, ChannelWriter<string> progress, int concurrency, bool emitProgress)
            : base("ChunkMapExecutor")
        {
            _localAgent = localAgent;
            _progress = progress;
            _concurrency = Math.Max(1, concurrency);
            _emitProgress = emitProgress;
        }

        public override async ValueTask HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var chunks = SplitIntoChunks(message, WordsPerChunk);
            string cloudUserMessage;

            if (chunks.Count <= 1)
            {
                // Small-doc fast path: no local map, summarize the text directly in the cloud.
                await EmitAsync("📍 Small document — summarizing directly in the cloud...\n\n", cancellationToken);
                cloudUserMessage = $"Text:\n{message}";
            }
            else
            {
                await EmitAsync($"📍 Phase 1: Summarizing {chunks.Count} chunks locally...\n\n", cancellationToken);

                var summaries = new string?[chunks.Count];
                using var gate = new SemaphoreSlim(_concurrency);

                var tasks = chunks.Select(async (chunk, index) =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var response = await _localAgent.RunAsync(chunk, cancellationToken: cancellationToken);
                        summaries[index] = response.Text?.Trim();
                        await EmitAsync($"── Chunk {index + 1}/{chunks.Count} summarized ({CountWords(chunk)} words)\n", cancellationToken);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks);

                await EmitAsync("\n📍 Phase 2: Synthesizing final summary in the cloud...\n\n", cancellationToken);

                var ordered = summaries
                    .Select((summary, index) => (summary, index))
                    .Where(x => !string.IsNullOrWhiteSpace(x.summary))
                    .Select(x => $"[Section {x.index + 1}]: {x.summary}");

                cloudUserMessage = $"Section summaries:\n{string.Join("\n\n", ordered)}";
            }

            await context.SendMessageAsync(new ChatMessage(ChatRole.User, cloudUserMessage), cancellationToken: cancellationToken);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken: cancellationToken);
        }

        private ValueTask EmitAsync(string text, CancellationToken cancellationToken) =>
            _emitProgress ? _progress.WriteAsync(text, cancellationToken) : ValueTask.CompletedTask;
    }

    private static List<string> SplitIntoChunks(string text, int wordsPerChunk)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();

        for (int i = 0; i < words.Length; i += wordsPerChunk)
        {
            chunks.Add(string.Join(" ", words.Skip(i).Take(wordsPerChunk)));
        }

        return chunks.Count == 0 ? [text] : chunks;
    }

    private BenchmarkResult CreateBenchmark(long totalMs, long firstTokenMs, string output, long memoryBefore, int inputWords)
    {
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: false);
        return new BenchmarkResult
        {
            TotalTimeMs = totalMs,
            FirstTokenLatencyMs = firstTokenMs,
            TokensPerSecond = EstimateTokensPerSecond(output, totalMs),
            MemoryDeltaBytes = memoryAfter - memoryBefore,
            InputWordCount = inputWords,
            OutputWordCount = CountWords(output),
            OutputTokenCount = EstimateTokenCount(output)
        };
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static int EstimateTokenCount(string text) =>
        (int)(CountWords(text) * 1.3);

    private static double EstimateTokensPerSecond(string text, long milliseconds) =>
        milliseconds > 0 ? EstimateTokenCount(text) / (milliseconds / 1000.0) : 0;
}
