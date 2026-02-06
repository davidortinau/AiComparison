using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AiComparison.Models;
using Microsoft.Extensions.AI;

namespace AiComparison.Services;

/// <summary>
/// Hybrid AI service that combines local and cloud AI using chunked summarization:
/// - Local AI: Summarizes each chunk independently (handles context window limits)
/// - Cloud AI: Synthesizes chunk summaries into a coherent final summary
/// 
/// This approach enables processing documents that exceed local AI's context window
/// by breaking them into manageable pieces.
/// </summary>
public class HybridAiService : IAiService
{
    private readonly IChatClient _localClient;
    private readonly IChatClient _cloudClient;
    
    // Chunk size tuned for Apple Intelligence context limits (~500 words per chunk)
    private const int WordsPerChunk = 500;

    public string Name => "Hybrid AI";
    public string Description => "Local summarizes chunks → Cloud synthesizes (handles large docs)";

    public HybridAiService(IChatClient localClient, IChatClient cloudClient)
    {
        _localClient = localClient;
        _cloudClient = cloudClient;
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
        var memoryBefore = GC.GetTotalMemory(true);
        var inputWordCount = CountWords(text);

        try
        {
            var chunks = SplitIntoChunks(text, WordsPerChunk);
            var chunkSummaries = new List<string>();

            // Phase 1: Local AI summarizes each chunk
            foreach (var chunk in chunks)
            {
                var prompt = CreateChunkSummaryPrompt(chunk, chunks.Count);
                var response = await _localClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(response.Text))
                    chunkSummaries.Add(response.Text);
            }

            // Phase 2: Cloud AI synthesizes chunk summaries
            var synthesisPrompt = CreateSynthesisPrompt(chunkSummaries);
            var cloudResponse = await _cloudClient.GetResponseAsync(synthesisPrompt, cancellationToken: cancellationToken);
            
            stopwatch.Stop();
            var memoryAfter = GC.GetTotalMemory(false);
            var outputText = cloudResponse.Text ?? string.Empty;

            return new SummarizationResult
            {
                Text = outputText,
                Benchmark = new BenchmarkResult
                {
                    TotalTimeMs = stopwatch.ElapsedMilliseconds,
                    FirstTokenLatencyMs = stopwatch.ElapsedMilliseconds,
                    TokensPerSecond = EstimateTokensPerSecond(outputText, stopwatch.ElapsedMilliseconds),
                    MemoryDeltaBytes = memoryAfter - memoryBefore,
                    InputWordCount = inputWordCount,
                    OutputWordCount = CountWords(outputText),
                    OutputTokenCount = EstimateTokenCount(outputText)
                }
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
        var memoryBefore = GC.GetTotalMemory(true);
        var inputWordCount = CountWords(text);
        var outputBuilder = new StringBuilder();
        var firstTokenReceived = false;
        long firstTokenLatency = 0;
        var tokenCount = 0;

        var chunks = SplitIntoChunks(text, WordsPerChunk);
        var chunkSummaries = new List<string>();

        yield return $"📍 Phase 1: Summarizing {chunks.Count} chunks locally...\n\n";

        // Phase 1: Local AI summarizes each chunk
        for (int i = 0; i < chunks.Count; i++)
        {
            yield return $"── Chunk {i + 1}/{chunks.Count} ({CountWords(chunks[i])} words) ──\n";
            
            var chunkBuilder = new StringBuilder();
            var prompt = CreateChunkSummaryPrompt(chunks[i], chunks.Count);
            
            await foreach (var update in _localClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
            {
                if (!firstTokenReceived && !string.IsNullOrEmpty(update.Text))
                {
                    firstTokenLatency = stopwatch.ElapsedMilliseconds;
                    firstTokenReceived = true;
                }

                if (update.Text != null)
                {
                    chunkBuilder.Append(update.Text);
                    tokenCount++;
                    yield return update.Text;
                }
            }

            var chunkSummary = chunkBuilder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunkSummary))
                chunkSummaries.Add(chunkSummary);
            
            yield return "\n\n";
        }

        // Phase 2: Cloud AI synthesizes all chunk summaries
        yield return "📍 Phase 2: Synthesizing final summary in cloud...\n\n";
        outputBuilder.Clear();
        
        var synthesisPrompt = CreateSynthesisPrompt(chunkSummaries);
        
        await foreach (var update in _cloudClient.GetStreamingResponseAsync(synthesisPrompt, cancellationToken: cancellationToken))
        {
            if (update.Text != null)
            {
                outputBuilder.Append(update.Text);
                tokenCount++;
                yield return update.Text;

                if (tokenCount % 5 == 0)
                {
                    onBenchmarkUpdate?.Invoke(CreateBenchmark(
                        stopwatch.ElapsedMilliseconds,
                        firstTokenLatency,
                        outputBuilder.ToString(),
                        memoryBefore,
                        inputWordCount));
                }
            }
        }

        stopwatch.Stop();
        onBenchmarkUpdate?.Invoke(CreateBenchmark(
            stopwatch.ElapsedMilliseconds,
            firstTokenLatency,
            outputBuilder.ToString(),
            memoryBefore,
            inputWordCount));
    }

    private static List<string> SplitIntoChunks(string text, int wordsPerChunk)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        
        for (int i = 0; i < words.Length; i += wordsPerChunk)
        {
            var chunkWords = words.Skip(i).Take(wordsPerChunk);
            chunks.Add(string.Join(" ", chunkWords));
        }
        
        return chunks;
    }

    private static string CreateChunkSummaryPrompt(string chunk, int totalChunks) =>
        $"""
        Summarize this text section concisely in 2-3 sentences. Capture the key points.
        {(totalChunks > 1 ? "This is one part of a larger document." : "")}

        Text:
        {chunk}

        Summary:
        """;

    private static string CreateSynthesisPrompt(List<string> chunkSummaries) =>
        $"""
        The following are summaries of consecutive sections from a single document.
        Synthesize them into one coherent, well-structured summary that flows naturally.
        Eliminate redundancy and organize the information logically.

        Section summaries:
        {string.Join("\n\n", chunkSummaries.Select((s, i) => $"[Section {i + 1}]: {s}"))}

        Write a unified summary:
        """;

    private BenchmarkResult CreateBenchmark(long totalMs, long firstTokenMs, string output, long memoryBefore, int inputWords)
    {
        var memoryAfter = GC.GetTotalMemory(false);
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
