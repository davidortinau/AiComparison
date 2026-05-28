using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AiComparison.Models;
using AiComparison.Services.Maf;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AiComparison.Services;

/// <summary>
/// Privacy-preserving hybrid AI service built as a Microsoft Agent Framework pipeline:
/// <c>[AnonymizeExecutor: regex PII anonymization] --> [Cloud agent] --> service restores PII</c>.
///
/// PII never leaves the device: the anonymize stage replaces sensitive values with placeholders
/// before anything is sent to the cloud agent. The original values are kept in per-call executor
/// state (never in workflow shared state, events, logs, or streamed output). After the cloud agent
/// finishes, the service restores the originals on the fully-buffered response and validates that no
/// placeholders leaked through before surfacing the final result.
/// </summary>
public class PrivacyHybridAiService : IAiService
{
    private readonly IChatClient _cloudClient;

    private const int MaxCloudOutputTokens = 800;

    // Matches any residual [CATEGORY_n] placeholder so leaks can be detected after restoration.
    private static readonly Regex PlaceholderRegex = new(@"\[[A-Z_]+_\d+\]", RegexOptions.Compiled);

    private const string NetworkContext =
        """
        INSURANCE NETWORK CONTEXT (BlueCross BlueShield Portland Network):
        Available Specialists:
        - Endocrinology: Dr. Rachel Morrison, Pacific Diabetes Center (accepts new patients, 2-week wait)
        - Cardiology: Dr. James Chen, Providence Heart Institute (specializes in preventive cardiology)
        - Podiatry: Dr. Amanda Foster, Portland Foot & Ankle Clinic (diabetic foot care specialist)
        - Genetic Counseling: Sarah Williams, MS, CGC, OHSU Knight Cancer Institute (BRCA testing)
        - Neurology: Dr. Michael Park, Legacy Neuroscience Center (peripheral neuropathy specialist)
        - Geriatric Medicine: Dr. Linda Tran, Providence ElderCare (Alzheimer's family support)

        Nearby Facilities:
        - Quest Diagnostics Lab: 1520 SW Taylor St (patient's usual lab)
        - OHSU Imaging Center: Comprehensive cardiac and neurological imaging
        - Providence Wellness Center: Diabetes education and nutrition counseling
        """;

    private static readonly string QuestionInstructions =
        $"""
        You are a medical assistant with access to both a patient's health record AND their insurance
        network information. Patient identifying information has been replaced with placeholders like
        [PERSON_NAME_1]; keep these placeholders in your answer where relevant — they will be restored
        afterward. Use both the record data and the network resources below where helpful.

        {NetworkContext}
        """;

    private const string SummaryInstructions =
        """
        Summarize the following medical record. Focus on key medical conditions and their current
        management, important family medical history and risk factors, and recent concerns with
        recommended next steps. Some identifying information has been replaced with placeholders like
        [PERSON_NAME_1]; keep these placeholders in your summary where relevant.
        """;

    public string Name => "Hybrid AI (Privacy)";
    public string Description => "Local anonymizes → Cloud summarizes → Local restores PII";

    public PrivacyHybridAiService(IChatClient localClient, IChatClient cloudClient)
    {
        // The local client participates only as the on-device anonymization stage, which is pure
        // regex (no model call); the cloud client performs the summarization/answer.
        _cloudClient = cloudClient;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var cloudMeta = _cloudClient.GetService<ChatClientMetadata>();
            return cloudMeta != null;
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
        var (question, healthRecord) = ParseRequest(text);
        var inputWordCount = CountWords(healthRecord);

        try
        {
            var executor = new AnonymizeExecutor(healthRecord, question, progress: null);
            var cloudAgent = CreateCloudAgent(question != null);
            var buffer = new StringBuilder();

            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var runTask = MafPipeline.RunAsync(executor, cloudAgent, healthRecord, channel.Writer,
                token => buffer.Append(token), cancellationToken);

            await foreach (var _ in channel.Reader.ReadAllAsync(cancellationToken))
            {
                // Non-streaming path: drain cloud tokens; progress markers are suppressed.
            }

            await runTask;

            var (restored, _) = RestoreAndValidate(buffer.ToString(), executor.PiiMap);

            stopwatch.Stop();

            return new SummarizationResult
            {
                Text = restored,
                Benchmark = CreateBenchmark(stopwatch.ElapsedMilliseconds, stopwatch.ElapsedMilliseconds,
                    restored, memoryBefore, inputWordCount)
            };
        }
        catch (Exception ex)
        {
            return SummarizationResult.Error($"Privacy Hybrid AI error: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<string> SummarizeStreamingAsync(
        string text,
        Action<BenchmarkResult>? onBenchmarkUpdate = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        var (question, healthRecord) = ParseRequest(text);
        var inputWordCount = CountWords(healthRecord);

        var outputBuilder = new StringBuilder();
        long firstTokenLatency = 0;
        var firstTokenSeen = false;
        var tokenCount = 0;

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var executor = new AnonymizeExecutor(healthRecord, question, channel.Writer);
        var cloudAgent = CreateCloudAgent(question != null);

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

        // Own a linked CTS so early enumeration-stop or caller cancellation tears the workflow down.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = MafPipeline.RunAsync(executor, cloudAgent, healthRecord, channel.Writer, OnCloudToken, cts.Token);

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

        // Phase 3 (reached only after a successful drain): restore PII on the fully-buffered cloud
        // response. Placeholders may be split across streamed tokens, so restoration must happen on
        // the complete text. On an errored/cancelled stream the foreach above throws and we never get here.
        var (finalResponse, leftovers) = RestoreAndValidate(outputBuilder.ToString(), executor.PiiMap);

        var resultLabel = question != null ? "Final answer" : "Final summary";
        yield return $"\n\n📍 Phase 3: Restoring original PII values...\n\n";
        yield return $"✓ **{resultLabel} with restored PII:**\n\n";
        yield return "─────────────────────────────\n";
        yield return finalResponse;
        yield return "\n─────────────────────────────\n";

        if (leftovers > 0)
        {
            yield return $"\n⚠️ {leftovers} placeholder(s) could not be matched to original values.\n";
        }

        stopwatch.Stop();
        onBenchmarkUpdate?.Invoke(CreateBenchmark(
            stopwatch.ElapsedMilliseconds, firstTokenSeen ? firstTokenLatency : stopwatch.ElapsedMilliseconds,
            finalResponse, memoryBefore, inputWordCount));
    }

    private ChatClientAgent CreateCloudAgent(bool isQuestion) =>
        new(_cloudClient, new ChatClientAgentOptions
        {
            Name = "CloudPrivacyAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = isQuestion ? QuestionInstructions : SummaryInstructions,
                MaxOutputTokens = MaxCloudOutputTokens
            }
        });

    private static (string? question, string healthRecord) ParseRequest(string text)
    {
        if (text.Contains("QUESTION:") && text.Contains("---"))
        {
            var parts = text.Split("---", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var question = parts[0].Replace("QUESTION:", "").Trim();
                var record = parts[1].Replace("HEALTH_RECORD:", "").Trim();
                return (question, record);
            }
        }

        return (null, text);
    }

    /// <summary>
    /// On-device anonymization stage. Replaces PII with placeholders, keeps the original mapping in
    /// per-call state (never sent to the cloud, events, or logs), emits a privacy-safe progress
    /// preview, then forwards the anonymized prompt to the cloud agent.
    /// </summary>
    [SendsMessage(typeof(ChatMessage))]
    [SendsMessage(typeof(TurnToken))]
    private sealed class AnonymizeExecutor : Executor<string>
    {
        private readonly string _healthRecord;
        private readonly string? _question;
        private readonly ChannelWriter<string>? _progress;

        public Dictionary<string, string> PiiMap { get; } = new();

        public AnonymizeExecutor(string healthRecord, string? question, ChannelWriter<string>? progress)
            : base("AnonymizeExecutor")
        {
            _healthRecord = healthRecord;
            _question = question;
            _progress = progress;
        }

        public override async ValueTask HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            await EmitAsync("📍 Phase 1: Anonymizing PII locally...\n\n", cancellationToken);

            var anonymized = AnonymizePii(_healthRecord, PiiMap);

            // The question is also bound for the cloud, so it must be anonymized too — otherwise PII in
            // the question (e.g. a name) would bypass the privacy guarantee. It shares the same map so
            // entities common to the record reuse the same placeholder and restore consistently.
            var anonymizedQuestion = _question != null ? AnonymizeQuestion(_question, PiiMap) : null;

            await EmitAsync($"✓ Found and anonymized {PiiMap.Count} PII items:\n", cancellationToken);
            foreach (var category in PiiMap.GroupBy(p => GetPiiCategory(p.Key)))
            {
                await EmitAsync($"  • {category.Key}: {category.Count()} items\n", cancellationToken);
            }
            await EmitAsync("\n", cancellationToken);

            // The preview shows anonymized text only (placeholders), so it is safe to surface.
            await EmitAsync("📄 Anonymized text preview:\n─────────────────────────────\n", cancellationToken);
            var preview = anonymized.Length > 500 ? anonymized[..500] + "..." : anonymized;
            await EmitAsync(preview + "\n─────────────────────────────\n\n", cancellationToken);

            var phase = _question != null
                ? "📍 Phase 2: Cloud AI answering question (with network context)...\n\n"
                : "📍 Phase 2: Cloud AI summarizing anonymized text...\n\n";
            await EmitAsync(phase, cancellationToken);

            var cloudUserMessage = _question != null
                ? $"ANONYMIZED HEALTH RECORD:\n{anonymized}\n\nQUESTION:\n{anonymizedQuestion}"
                : $"ANONYMIZED MEDICAL RECORD:\n{anonymized}";

            await context.SendMessageAsync(new ChatMessage(ChatRole.User, cloudUserMessage), cancellationToken: cancellationToken);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken: cancellationToken);
        }

        private ValueTask EmitAsync(string text, CancellationToken cancellationToken) =>
            _progress?.WriteAsync(text, cancellationToken) ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// Anonymizes a free-text question against the SAME map built from the record. First substitutes
    /// any already-known PII values (so entities shared with the record reuse the same placeholder and
    /// restore consistently), then runs the regex detectors for any PII unique to the question.
    /// </summary>
    private static string AnonymizeQuestion(string question, Dictionary<string, string> piiMap)
    {
        var result = question;

        // Replace known originals (longest first to avoid partial overlaps) with their placeholders.
        foreach (var (placeholder, original) in piiMap.OrderByDescending(p => p.Value.Length))
        {
            if (!string.IsNullOrEmpty(original))
            {
                result = result.Replace(original, placeholder);
            }
        }

        // Detect and anonymize any remaining PII unique to the question (continues map numbering).
        return AnonymizePii(result, piiMap);
    }

    private static string AnonymizePii(string text, Dictionary<string, string> piiMap)
    {
        var result = text;
        // Continue numbering across calls (record then question) so placeholders never collide.
        var counter = piiMap.Count + 1;

        result = ReplacePattern(result, @"\b\d{3}-\d{2}-\d{4}\b", "SSN", piiMap, ref counter);
        result = ReplacePattern(result, @"\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", "PHONE", piiMap, ref counter);
        result = ReplacePattern(result, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "EMAIL", piiMap, ref counter);
        result = ReplacePattern(result, @"\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b", "DATE", piiMap, ref counter);
        result = ReplacePattern(result, @"\b\d+\s+[A-Za-z]+\s+(?:Street|St|Avenue|Ave|Lane|Ln|Drive|Dr|Road|Rd|Boulevard|Blvd|Court|Ct|Way|Place|Pl)[,.]?\s*(?:Apartment|Apt|Suite|Ste|Unit|#)?\s*\d*[A-Za-z]?\b", "ADDRESS", piiMap, ref counter);
        result = ReplacePattern(result, @"\b[A-Z]{2,4}[-#]?\d{5,}[-]?[A-Z]{0,2}\b", "POLICY_NUM", piiMap, ref counter);
        result = ReplacePattern(result, @"\b(?:Dr\.|Mr\.|Mrs\.|Ms\.)\s+[A-Z][a-z]+\s+[A-Z][a-z]+\b", "PERSON_TITLE", piiMap, ref counter);
        result = ReplacePattern(result, @"(?<=(?:Name|Patient|Contact|Physician|Doctor|Therapist|Educator):\s*)[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+", "PERSON_NAME", piiMap, ref counter);
        result = ReplacePattern(result, @"\bage\s+\d{1,3}\b", "AGE", piiMap, ref counter, RegexOptions.IgnoreCase);
        result = ReplacePattern(result, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?,\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?\b", "LOCATION", piiMap, ref counter);

        return result;
    }

    private static string ReplacePattern(string text, string pattern, string category,
        Dictionary<string, string> piiMap, ref int counter, RegexOptions options = RegexOptions.None)
    {
        var matches = Regex.Matches(text, pattern, options);
        var result = text;

        // Process in reverse order to keep match indexes valid as we substitute.
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var placeholder = $"[{category}_{counter++}]";
            piiMap[placeholder] = match.Value;
            result = result.Remove(match.Index, match.Length).Insert(match.Index, placeholder);
        }

        return result;
    }

    /// <summary>
    /// Restores original PII values from placeholders on the complete cloud response and reports how
    /// many placeholders remain unmatched (a privacy/quality signal).
    /// </summary>
    private static (string restored, int leftovers) RestoreAndValidate(string text, Dictionary<string, string> piiMap)
    {
        var result = text;
        foreach (var (placeholder, original) in piiMap)
        {
            result = result.Replace(placeholder, original);
        }

        var leftovers = PlaceholderRegex.Matches(result).Count;
        return (result, leftovers);
    }

    private static string GetPiiCategory(string placeholder)
    {
        if (placeholder.Contains("SSN")) return "Social Security Numbers";
        if (placeholder.Contains("PHONE")) return "Phone Numbers";
        if (placeholder.Contains("EMAIL")) return "Email Addresses";
        if (placeholder.Contains("DATE")) return "Dates";
        if (placeholder.Contains("ADDRESS")) return "Addresses";
        if (placeholder.Contains("POLICY")) return "Policy/Account Numbers";
        if (placeholder.Contains("PERSON")) return "Person Names";
        if (placeholder.Contains("AGE")) return "Ages";
        if (placeholder.Contains("LOCATION")) return "Locations";
        return "Other";
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
