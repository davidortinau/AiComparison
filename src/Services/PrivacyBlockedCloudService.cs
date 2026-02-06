using System.Runtime.CompilerServices;
using AiComparison.Models;

namespace AiComparison.Services;

/// <summary>
/// A service that refuses to process text containing PII.
/// Used in Privacy scenario to demonstrate that cloud services shouldn't receive sensitive data.
/// </summary>
public class PrivacyBlockedCloudService : IAiService
{
    public string Name => "Cloud AI (Privacy Mode)";
    public string Description => "🚫 Blocked - Cannot send PII to cloud services";

    public Task<bool> IsAvailableAsync() => Task.FromResult(true);

    public Task<SummarizationResult> SummarizeAsync(string text, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SummarizationResult.Error(GetBlockedMessage()));
    }

    public async IAsyncEnumerable<string> SummarizeStreamingAsync(
        string text,
        Action<BenchmarkResult>? onBenchmarkUpdate = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "🚫 **BLOCKED: Privacy Protection Active**\n\n";
        yield return "This text contains personally identifiable information (PII) that cannot be sent to cloud services.\n\n";
        yield return "**Detected PII categories:**\n";
        yield return "• Social Security Numbers\n";
        yield return "• Medical Record Numbers\n";
        yield return "• Home Addresses\n";
        yield return "• Phone Numbers\n";
        yield return "• Email Addresses\n";
        yield return "• Insurance Policy Numbers\n";
        yield return "• Family Member Information\n\n";
        yield return "**Why this matters:**\n";
        yield return "Sending health records to cloud AI services could violate HIPAA regulations ";
        yield return "and expose sensitive patient data to third parties.\n\n";
        yield return "**Solution:** Use the **Hybrid** mode, which anonymizes PII locally before ";
        yield return "sending to the cloud, then restores the original identifiers in the final summary.";
        
        onBenchmarkUpdate?.Invoke(BenchmarkResult.Empty);
    }

    private static string GetBlockedMessage() => """
        🚫 BLOCKED: Privacy Protection Active
        
        This text contains personally identifiable information (PII) that cannot be sent to cloud services.
        
        Use Hybrid mode to safely summarize PII-containing documents.
        """;
}
