using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AiComparison.Models;
using Microsoft.Extensions.AI;

namespace AiComparison.Services;

public class LocalAiService : IAiService
{
    private readonly IChatClient _chatClient;

    public string Name => "Local AI";
    public string Description => "On-device Apple Intelligence - Fast, private, no network required";

    public LocalAiService(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var metadata = _chatClient.GetService<ChatClientMetadata>();
            return metadata != null;
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
            var prompt = CreateSummarizationPrompt(text);
            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            
            stopwatch.Stop();
            var memoryAfter = GC.GetTotalMemory(false);
            var outputText = response.Text ?? string.Empty;

            return new SummarizationResult
            {
                Text = outputText,
                Benchmark = new BenchmarkResult
                {
                    TotalTimeMs = stopwatch.ElapsedMilliseconds,
                    FirstTokenLatencyMs = stopwatch.ElapsedMilliseconds, // Non-streaming, same as total
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
            return SummarizationResult.Error($"Local AI error: {ex.Message}");
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

        var prompt = CreateSummarizationPrompt(text);

        await foreach (var update in _chatClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
        {
            if (!firstTokenReceived && !string.IsNullOrEmpty(update.Text))
            {
                firstTokenLatency = stopwatch.ElapsedMilliseconds;
                firstTokenReceived = true;
            }

            if (update.Text != null)
            {
                outputBuilder.Append(update.Text);
                tokenCount++;
                yield return update.Text;

                // Update benchmark periodically
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

        // Final benchmark update
        stopwatch.Stop();
        onBenchmarkUpdate?.Invoke(CreateBenchmark(
            stopwatch.ElapsedMilliseconds,
            firstTokenLatency,
            outputBuilder.ToString(),
            memoryBefore,
            inputWordCount));
    }

    private static string CreateSummarizationPrompt(string text) =>
        $"""
        Summarize the following text concisely, capturing the main points and key information.
        Keep the summary clear and well-organized.

        Text to summarize:
        {text}

        Summary:
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
        (int)(CountWords(text) * 1.3); // Rough estimate: ~1.3 tokens per word

    private static double EstimateTokensPerSecond(string text, long milliseconds) =>
        milliseconds > 0 ? EstimateTokenCount(text) / (milliseconds / 1000.0) : 0;
}
