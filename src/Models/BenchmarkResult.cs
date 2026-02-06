namespace AiComparison.Models;

public record BenchmarkResult
{
    public long TotalTimeMs { get; init; }
    public long FirstTokenLatencyMs { get; init; }
    public double TokensPerSecond { get; init; }
    public long MemoryDeltaBytes { get; init; }
    public int InputWordCount { get; init; }
    public int OutputWordCount { get; init; }
    public int OutputTokenCount { get; init; }

    public double MemoryDeltaMB => MemoryDeltaBytes / (1024.0 * 1024.0);

    public static BenchmarkResult Empty => new()
    {
        TotalTimeMs = 0,
        FirstTokenLatencyMs = 0,
        TokensPerSecond = 0,
        MemoryDeltaBytes = 0,
        InputWordCount = 0,
        OutputWordCount = 0,
        OutputTokenCount = 0
    };
}
