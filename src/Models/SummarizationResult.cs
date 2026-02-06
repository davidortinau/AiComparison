namespace AiComparison.Models;

public record SummarizationResult
{
    public required string Text { get; init; }
    public required BenchmarkResult Benchmark { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorMessage { get; init; }

    public static SummarizationResult Error(string message) => new()
    {
        Text = string.Empty,
        Benchmark = BenchmarkResult.Empty,
        IsSuccess = false,
        ErrorMessage = message
    };
}
