using System.Runtime.CompilerServices;
using AiComparison.Models;

namespace AiComparison.Services;

public interface IAiService
{
    string Name { get; }
    string Description { get; }
    
    Task<bool> IsAvailableAsync();
    
    Task<SummarizationResult> SummarizeAsync(string text, CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<string> SummarizeStreamingAsync(
        string text,
        Action<BenchmarkResult>? onBenchmarkUpdate = null,
        CancellationToken cancellationToken = default);
}
