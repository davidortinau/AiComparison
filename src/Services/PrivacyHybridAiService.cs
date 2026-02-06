using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AiComparison.Models;
using Microsoft.Extensions.AI;

namespace AiComparison.Services;

/// <summary>
/// Privacy-preserving hybrid AI service that:
/// 1. Local AI: Identifies and anonymizes PII (replaces with placeholders)
/// 2. Cloud AI: Summarizes the anonymized text
/// 3. Local AI: Restores original PII values in the summary
/// 
/// This enables using powerful cloud AI while keeping sensitive data on-device.
/// </summary>
public class PrivacyHybridAiService : IAiService
{
    private readonly IChatClient _localClient;
    private readonly IChatClient _cloudClient;

    public string Name => "Hybrid AI (Privacy)";
    public string Description => "Local anonymizes → Cloud summarizes → Local restores PII";

    public PrivacyHybridAiService(IChatClient localClient, IChatClient cloudClient)
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
            // Phase 1: Anonymize PII
            var (anonymizedText, piiMap) = AnonymizePii(text);

            // Phase 2: Cloud summarizes anonymized text
            var prompt = CreateSummaryPrompt(anonymizedText);
            var response = await _cloudClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var anonymizedSummary = response.Text ?? string.Empty;

            // Phase 3: Restore PII
            var finalSummary = RestorePii(anonymizedSummary, piiMap);
            
            stopwatch.Stop();
            var memoryAfter = GC.GetTotalMemory(false);

            return new SummarizationResult
            {
                Text = finalSummary,
                Benchmark = new BenchmarkResult
                {
                    TotalTimeMs = stopwatch.ElapsedMilliseconds,
                    FirstTokenLatencyMs = stopwatch.ElapsedMilliseconds,
                    TokensPerSecond = EstimateTokensPerSecond(finalSummary, stopwatch.ElapsedMilliseconds),
                    MemoryDeltaBytes = memoryAfter - memoryBefore,
                    InputWordCount = inputWordCount,
                    OutputWordCount = CountWords(finalSummary),
                    OutputTokenCount = EstimateTokenCount(finalSummary)
                }
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
        var memoryBefore = GC.GetTotalMemory(true);
        var firstTokenReceived = false;
        long firstTokenLatency = 0;
        var tokenCount = 0;

        // Check if this is a Q&A request (format: "QUESTION:...\n---\nHEALTH_RECORD:...")
        string? question = null;
        string healthRecord = text;
        
        if (text.Contains("QUESTION:") && text.Contains("---"))
        {
            var parts = text.Split("---", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                question = parts[0].Replace("QUESTION:", "").Trim();
                healthRecord = parts[1].Replace("HEALTH_RECORD:", "").Trim();
            }
        }
        
        var inputWordCount = CountWords(healthRecord);

        // Phase 1: Anonymize PII locally
        yield return "📍 Phase 1: Anonymizing PII locally...\n\n";
        
        var (anonymizedText, piiMap) = AnonymizePii(healthRecord);
        
        yield return $"✓ Found and anonymized {piiMap.Count} PII items:\n";
        foreach (var category in piiMap.GroupBy(p => GetPiiCategory(p.Key)))
        {
            yield return $"  • {category.Key}: {category.Count()} items\n";
        }
        yield return "\n";

        // Show a preview of the anonymized text
        yield return "📄 Anonymized text preview:\n";
        yield return "─────────────────────────────\n";
        var preview = anonymizedText.Length > 500 
            ? anonymizedText.Substring(0, 500) + "..." 
            : anonymizedText;
        yield return preview + "\n";
        yield return "─────────────────────────────\n\n";

        // Phase 2: Cloud processes anonymized text (with network context if Q&A)
        var phaseDescription = question != null 
            ? "📍 Phase 2: Cloud AI answering question (with network context)...\n\n"
            : "📍 Phase 2: Cloud AI summarizing anonymized text...\n\n";
        yield return phaseDescription;
        
        var outputBuilder = new StringBuilder();
        var prompt = question != null 
            ? CreateQuestionPrompt(anonymizedText, question)
            : CreateSummaryPrompt(anonymizedText);
        
        await foreach (var update in _cloudClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
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

        var anonymizedResponse = outputBuilder.ToString();

        // Phase 3: Restore PII
        yield return "\n\n📍 Phase 3: Restoring original PII values...\n\n";
        
        var finalResponse = RestorePii(anonymizedResponse, piiMap);
        
        var resultLabel = question != null ? "Final answer" : "Final summary";
        yield return $"✓ **{resultLabel} with restored PII:**\n\n";
        yield return "─────────────────────────────\n";
        yield return finalResponse;
        yield return "\n─────────────────────────────\n";

        stopwatch.Stop();
        onBenchmarkUpdate?.Invoke(CreateBenchmark(
            stopwatch.ElapsedMilliseconds,
            firstTokenLatency,
            finalResponse,
            memoryBefore,
            inputWordCount));
    }

    /// <summary>
    /// Anonymizes PII in the text using regex patterns and returns a mapping for restoration.
    /// </summary>
    private static (string anonymizedText, Dictionary<string, string> piiMap) AnonymizePii(string text)
    {
        var piiMap = new Dictionary<string, string>();
        var result = text;
        var counter = 1;

        // SSN pattern: XXX-XX-XXXX
        result = ReplacePattern(result, @"\b\d{3}-\d{2}-\d{4}\b", "SSN", piiMap, ref counter);

        // Phone numbers: (XXX) XXX-XXXX or XXX-XXX-XXXX
        result = ReplacePattern(result, @"\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", "PHONE", piiMap, ref counter);

        // Email addresses
        result = ReplacePattern(result, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "EMAIL", piiMap, ref counter);

        // Dates (various formats)
        result = ReplacePattern(result, @"\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b", "DATE", piiMap, ref counter);

        // Street addresses (simplified pattern)
        result = ReplacePattern(result, @"\b\d+\s+[A-Za-z]+\s+(?:Street|St|Avenue|Ave|Lane|Ln|Drive|Dr|Road|Rd|Boulevard|Blvd|Court|Ct|Way|Place|Pl)[,.]?\s*(?:Apartment|Apt|Suite|Ste|Unit|#)?\s*\d*[A-Za-z]?\b", "ADDRESS", piiMap, ref counter);

        // Policy/Account numbers (alphanumeric with dashes)
        result = ReplacePattern(result, @"\b[A-Z]{2,4}[-#]?\d{5,}[-]?[A-Z]{0,2}\b", "POLICY_NUM", piiMap, ref counter);

        // Names following specific patterns (Dr., Mr., Mrs., etc.)
        result = ReplacePattern(result, @"\b(?:Dr\.|Mr\.|Mrs\.|Ms\.)\s+[A-Z][a-z]+\s+[A-Z][a-z]+\b", "PERSON_TITLE", piiMap, ref counter);

        // Full names in specific contexts (after "Name:", "Patient:", "Contact:", etc.)
        result = ReplacePattern(result, @"(?<=(?:Name|Patient|Contact|Physician|Doctor|Therapist|Educator):\s*)[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+", "PERSON_NAME", piiMap, ref counter);

        // Ages
        result = ReplacePattern(result, @"\bage\s+\d{1,3}\b", "AGE", piiMap, ref counter, RegexOptions.IgnoreCase);

        // City, State ZIP
        result = ReplacePattern(result, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?,\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?\b", "LOCATION", piiMap, ref counter);

        return (result, piiMap);
    }

    private static string ReplacePattern(string text, string pattern, string category,
        Dictionary<string, string> piiMap, ref int counter, RegexOptions options = RegexOptions.None)
    {
        var matches = Regex.Matches(text, pattern, options);
        var result = text;
        
        // Process in reverse order to maintain correct positions
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
    /// Restores original PII values from placeholders in the summary.
    /// </summary>
    private static string RestorePii(string text, Dictionary<string, string> piiMap)
    {
        var result = text;
        foreach (var (placeholder, original) in piiMap)
        {
            result = result.Replace(placeholder, original);
        }
        return result;
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

    // Cloud prompt includes network context for richer answers
    private static string CreateQuestionPrompt(string anonymizedRecord, string question) =>
        $"""
        You are a medical assistant with access to both a patient's health record AND their insurance network information.
        
        ANONYMIZED HEALTH RECORD:
        {anonymizedRecord}

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
        
        Note: Patient identifying information has been replaced with placeholders like [PERSON_NAME_1].
        Keep these placeholders in your answer where relevant - they will be restored afterward.

        QUESTION:
        {question}

        ANSWER (incorporating both record data and network resources where helpful):
        """;

    // Fallback for summarization (non-question mode)
    private static string CreateSummaryPrompt(string text) =>
        $"""
        Summarize the following medical record. Focus on:
        - Key medical conditions and their current management
        - Important family medical history and risk factors  
        - Recent concerns and recommended next steps

        Note: Some identifying information has been replaced with placeholders like [PERSON_NAME_1].
        Keep these placeholders in your summary where relevant.

        Medical Record:
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
        (int)(CountWords(text) * 1.3);

    private static double EstimateTokensPerSecond(string text, long milliseconds) =>
        milliseconds > 0 ? EstimateTokenCount(text) / (milliseconds / 1000.0) : 0;
}
