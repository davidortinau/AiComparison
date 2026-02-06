using System.Text;
using System.Text.RegularExpressions;
using AiComparison.Models;
using AiComparison.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiComparison.ViewModels;

public enum ScenarioType
{
    Power,   // Compare raw summarization capabilities
    Privacy  // Demonstrate PII handling - local works, cloud blocked, hybrid anonymizes
}

public partial class MainViewModel : ObservableObject
{
    private readonly IAiService _localService;
    private readonly IAiService _cloudService;
    private readonly HybridAiService _hybridService;
    private readonly PrivacyHybridAiService _privacyHybridService;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isLoadingContent;

    [ObservableProperty]
    private BenchmarkResult _benchmark = BenchmarkResult.Empty;

    // Separate benchmarks for comparison table
    [ObservableProperty]
    private BenchmarkResult _localBenchmark = BenchmarkResult.Empty;

    [ObservableProperty]
    private BenchmarkResult _cloudBenchmark = BenchmarkResult.Empty;

    [ObservableProperty]
    private BenchmarkResult _hybridBenchmark = BenchmarkResult.Empty;

    // Cached summaries for comparison
    [ObservableProperty]
    private string _localSummary = string.Empty;

    [ObservableProperty]
    private string _cloudSummary = string.Empty;

    [ObservableProperty]
    private string _hybridSummary = string.Empty;

    [ObservableProperty]
    private string _comparisonResult = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _contentUrl = "https://raw.githubusercontent.com/dotnet/csharplang/main/proposals/unions.md";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScenarioDescription))]
    [NotifyPropertyChangedFor(nameof(IsPowerScenario))]
    [NotifyPropertyChangedFor(nameof(IsPrivacyScenario))]
    [NotifyPropertyChangedFor(nameof(ActionButtonText))]
    [NotifyPropertyChangedFor(nameof(OutputSectionTitle))]
    private ScenarioType _selectedScenario = ScenarioType.Power;

    public bool IsPowerScenario => SelectedScenario == ScenarioType.Power;
    public bool IsPrivacyScenario => SelectedScenario == ScenarioType.Privacy;
    
    public string ScenarioDescription => SelectedScenario switch
    {
        ScenarioType.Power => "Compare raw summarization speed and quality across all providers",
        ScenarioType.Privacy => "Ask questions about health records with PII protection",
        _ => ""
    };

    // Dynamic UI labels based on scenario
    public string ActionButtonText => IsPrivacyScenario ? "Ask" : "Summarize";
    public string OutputSectionTitle => IsPrivacyScenario ? "Response" : "Summary Output";

    // Questions for Privacy scenario
    public List<string> HealthQuestions { get; } =
    [
        "Based on family history and current conditions, what are the top 3 health risks this patient should prioritize?",
        "Given the symptoms (fatigue, dizziness, tingling feet), what's the likely cause and what should be ruled out?",
        "What health screenings should the patient's children receive based on family medical history?",
        "Which current symptoms might be medication side effects vs. disease progression?",
        "What specialists should be involved in this patient's ongoing care and why?"
    ];

    [ObservableProperty]
    private string _selectedQuestion = string.Empty;

    [ObservableProperty]
    private bool _localAvailable;

    [ObservableProperty]
    private bool _cloudAvailable;

    // Enable Final tab when at least 2 summaries exist
    public bool CanCompare => GetCompletedSummaryCount() >= 2;
    public int CompletedSummaryCount => GetCompletedSummaryCount();

    // Word count for loaded content
    public int WordCount => string.IsNullOrWhiteSpace(InputText) ? 0 : InputText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
    public bool HasContent => !string.IsNullOrWhiteSpace(InputText);

    // Best value indicators (for bold styling)
    public bool IsLocalBestTime => HasAllBenchmarks && LocalBenchmark.TotalTimeMs <= CloudBenchmark.TotalTimeMs && LocalBenchmark.TotalTimeMs <= HybridBenchmark.TotalTimeMs;
    public bool IsCloudBestTime => HasAllBenchmarks && CloudBenchmark.TotalTimeMs <= LocalBenchmark.TotalTimeMs && CloudBenchmark.TotalTimeMs <= HybridBenchmark.TotalTimeMs;
    public bool IsHybridBestTime => HasAllBenchmarks && HybridBenchmark.TotalTimeMs <= LocalBenchmark.TotalTimeMs && HybridBenchmark.TotalTimeMs <= CloudBenchmark.TotalTimeMs;

    public bool IsLocalBestFirstToken => HasAllBenchmarks && LocalBenchmark.FirstTokenLatencyMs <= CloudBenchmark.FirstTokenLatencyMs && LocalBenchmark.FirstTokenLatencyMs <= HybridBenchmark.FirstTokenLatencyMs;
    public bool IsCloudBestFirstToken => HasAllBenchmarks && CloudBenchmark.FirstTokenLatencyMs <= LocalBenchmark.FirstTokenLatencyMs && CloudBenchmark.FirstTokenLatencyMs <= HybridBenchmark.FirstTokenLatencyMs;
    public bool IsHybridBestFirstToken => HasAllBenchmarks && HybridBenchmark.FirstTokenLatencyMs <= LocalBenchmark.FirstTokenLatencyMs && HybridBenchmark.FirstTokenLatencyMs <= CloudBenchmark.FirstTokenLatencyMs;

    public bool IsLocalBestTokensPerSec => HasAllBenchmarks && LocalBenchmark.TokensPerSecond >= CloudBenchmark.TokensPerSecond && LocalBenchmark.TokensPerSecond >= HybridBenchmark.TokensPerSecond;
    public bool IsCloudBestTokensPerSec => HasAllBenchmarks && CloudBenchmark.TokensPerSecond >= LocalBenchmark.TokensPerSecond && CloudBenchmark.TokensPerSecond >= HybridBenchmark.TokensPerSecond;
    public bool IsHybridBestTokensPerSec => HasAllBenchmarks && HybridBenchmark.TokensPerSecond >= LocalBenchmark.TokensPerSecond && HybridBenchmark.TokensPerSecond >= CloudBenchmark.TokensPerSecond;

    public bool IsLocalBestMemory => HasAllBenchmarks && LocalBenchmark.MemoryDeltaBytes <= CloudBenchmark.MemoryDeltaBytes && LocalBenchmark.MemoryDeltaBytes <= HybridBenchmark.MemoryDeltaBytes;
    public bool IsCloudBestMemory => HasAllBenchmarks && CloudBenchmark.MemoryDeltaBytes <= LocalBenchmark.MemoryDeltaBytes && CloudBenchmark.MemoryDeltaBytes <= HybridBenchmark.MemoryDeltaBytes;
    public bool IsHybridBestMemory => HasAllBenchmarks && HybridBenchmark.MemoryDeltaBytes <= LocalBenchmark.MemoryDeltaBytes && HybridBenchmark.MemoryDeltaBytes <= CloudBenchmark.MemoryDeltaBytes;

    public bool HasAllBenchmarks => LocalBenchmark.TotalTimeMs > 0 && CloudBenchmark.TotalTimeMs > 0 && HybridBenchmark.TotalTimeMs > 0;

    public MainViewModel(
        LocalAiService localService,
        CloudAiService cloudService,
        HybridAiService hybridService,
        PrivacyHybridAiService privacyHybridService)
    {
        _localService = localService;
        _cloudService = cloudService;
        _hybridService = hybridService;
        _privacyHybridService = privacyHybridService;
    }

    public async Task InitializeAsync()
    {
        LocalAvailable = await _localService.IsAvailableAsync();
        CloudAvailable = await _cloudService.IsAvailableAsync();
        
        if (!LocalAvailable)
            StatusMessage = "⚠️ Local AI not available - check Apple Intelligence settings";
        else if (!CloudAvailable)
            StatusMessage = "⚠️ Cloud AI not configured - check Azure OpenAI settings";
        else
            StatusMessage = "Ready";
    }

    private IAiService CurrentService => (SelectedTabIndex, SelectedScenario) switch
    {
        (0, _) => _localService,
        (1, ScenarioType.Privacy) => new PrivacyBlockedCloudService(), // Cloud blocked for PII
        (1, _) => _cloudService,
        (2, ScenarioType.Privacy) => _privacyHybridService,
        (2, _) => _hybridService,
        _ => _localService
    };

    private int GetCompletedSummaryCount()
    {
        int count = 0;
        if (!string.IsNullOrEmpty(LocalSummary)) count++;
        if (!string.IsNullOrEmpty(CloudSummary)) count++;
        if (!string.IsNullOrEmpty(HybridSummary)) count++;
        return count;
    }

    [RelayCommand]
    private async Task LoadSampleTextAsync()
    {
        if (string.IsNullOrWhiteSpace(ContentUrl))
        {
            StatusMessage = "Please enter a URL";
            return;
        }

        IsLoadingContent = true;
        StatusMessage = "Loading content...";

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; AiComparison/1.0)");
            
            var content = await client.GetStringAsync(ContentUrl);
            
            // Clean up based on content type
            var text = ContentUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase) 
                ? CleanMarkdown(content) 
                : CleanHtml(content);
            
            InputText = text;
            OnPropertyChanged(nameof(WordCount));
            OnPropertyChanged(nameof(HasContent));
            StatusMessage = $"Loaded {WordCount:N0} words";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
            InputText = GetFallbackSampleText();
            OnPropertyChanged(nameof(WordCount));
            OnPropertyChanged(nameof(HasContent));
        }
        finally
        {
            IsLoadingContent = false;
        }
    }

    private static string CleanHtml(string html)
    {
        // Remove script and style tags
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        
        // Remove HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        
        // Decode common HTML entities
        html = html.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        
        // Normalize whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();
        
        return html;
    }

    private static string CleanMarkdown(string markdown)
    {
        // Remove code blocks (keep the concept, not the implementation details)
        markdown = Regex.Replace(markdown, @"```[\s\S]*?```", "[code example]", RegexOptions.Multiline);
        
        // Remove inline code but keep the text
        markdown = Regex.Replace(markdown, @"`([^`]+)`", "$1");
        
        // Remove link URLs but keep link text
        markdown = Regex.Replace(markdown, @"\[([^\]]+)\]\([^)]+\)", "$1");
        
        // Remove HTML comments
        markdown = Regex.Replace(markdown, @"<!--[\s\S]*?-->", "");
        
        // Remove reference-style anchors
        markdown = Regex.Replace(markdown, @"^\[[^\]]+\]:\s*#.*$", "", RegexOptions.Multiline);
        
        // Normalize whitespace
        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n");
        
        return markdown.Trim();
    }

    private static string GetFallbackSampleText() => """
        # C# Unions Proposal (Summary)

        Unions is a set of interlinked features that combine to provide C# support for union types.
        
        Union types are classes and structs that implement the IUnion interface. They support union 
        conversions (implicit conversion from case types), union matching (pattern matching unwraps 
        contents), union exhaustiveness (switch expressions are exhaustive when all cases matched), 
        and union nullability (enhanced null state tracking).
        
        The proposed unions in C# are unions of types, not "discriminated" or "tagged". Discriminated 
        unions can be expressed using fresh type declarations as case types.
        
        Union declarations provide a succinct syntax: public union Pet(Cat, Dog);
        
        This lowers to a record struct with IUnion implementation, constructors for each case type,
        and a Value property storing the contents as a single object reference.
        
        Key design decisions include: boxing value type cases for compactness, using pattern matching 
        semantics that unwrap union contents, and allowing both shorthand declarations and manual 
        implementations for flexibility.
        """;

    [RelayCommand]
    private void LoadHealthRecord()
    {
        InputText = GetSampleHealthRecord();
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(HasContent));
        StatusMessage = $"Loaded sample health record ({WordCount:N0} words)";
    }

    private static string GetSampleHealthRecord() => """
        PATIENT HEALTH HISTORY RECORD
        =============================
        
        Patient Information:
        - Full Name: Margaret Elizabeth Thompson
        - Date of Birth: March 15, 1978
        - Social Security Number: 428-55-7891
        - Address: 2847 Oakwood Lane, Apartment 12B, Portland, OR 97205
        - Phone: (503) 555-0147
        - Email: margaret.thompson78@gmail.com
        - Emergency Contact: Robert Thompson (husband) - (503) 555-0293
        - Insurance: BlueCross BlueShield, Policy #BCB-9928451-MT
        
        Employment:
        - Employer: Cascade Software Solutions
        - Position: Senior Software Engineer
        - Work Phone: (503) 555-8800 ext. 442
        
        Primary Care Physician: Dr. Jennifer Walsh, Providence Medical Group
        
        MEDICAL HISTORY
        ---------------
        
        Current Conditions:
        1. Type 2 Diabetes Mellitus - Diagnosed January 2019
           - Currently managed with Metformin 1000mg twice daily
           - Last HbA1c: 7.2% (December 2025)
        
        2. Hypertension - Diagnosed March 2020
           - Lisinopril 20mg daily
           - Blood pressure typically 128/82
        
        3. Generalized Anxiety Disorder - Diagnosed 2015
           - Sertraline 50mg daily
           - Sees therapist Dr. Michael Chen biweekly
        
        Surgical History:
        - Appendectomy (age 12, 1990) - Sacred Heart Hospital, Eugene, OR
        - C-section delivery (2008) - Legacy Emanuel Medical Center
        - Arthroscopic knee surgery, right knee (2021) - Dr. Sarah Kim, OHSU
        
        Allergies:
        - Penicillin (causes hives and throat swelling)
        - Sulfa drugs (severe rash)
        - Shellfish (anaphylaxis - carries EpiPen)
        
        FAMILY MEDICAL HISTORY
        ----------------------
        
        Father - William Robert Thompson (deceased, age 68)
        - Cause of death: Myocardial infarction (2020)
        - History of: Type 2 diabetes, hypertension, coronary artery disease
        - Had triple bypass surgery in 2015
        
        Mother - Dorothy Mae Thompson (née Sullivan), age 74
        - Living, resides at Sunrise Senior Living, 445 Elder Care Blvd, Beaverton, OR
        - Current conditions: Alzheimer's disease (early stage), osteoporosis, hypothyroidism
        - Mother's father died of stroke at age 71
        
        Brother - James Michael Thompson, age 52
        - Address: 1822 Pine Street, Seattle, WA 98101
        - Diagnosed with Type 2 diabetes at age 45
        - History of depression
        
        Sister - Patricia Ann Martinez (née Thompson), age 44
        - Lives in San Diego, CA with husband Carlos Martinez
        - Breast cancer survivor (diagnosed 2022, BRCA1 positive)
        - Genetic testing recommended for patient
        
        Maternal Grandmother - Rose Sullivan (deceased, age 82)
        - Died of complications from Alzheimer's disease
        - Also had history of breast cancer
        
        Children:
        - Emily Rose Thompson, age 17 (daughter)
          - Currently healthy, has asthma
          - Attends Lincoln High School
        - David Robert Thompson, age 14 (son)
          - ADHD diagnosis, takes Adderall 20mg
          - Food allergy to peanuts
        
        RECENT VISIT NOTES (January 8, 2026)
        ------------------------------------
        
        Chief Complaint: Patient reports increased fatigue over past 3 weeks, occasional 
        dizziness when standing quickly, and tingling in both feet.
        
        Assessment: Given family history of cardiovascular disease and current diabetes 
        management challenges, ordered comprehensive metabolic panel, lipid panel, and 
        HbA1c. Referred to podiatrist Dr. Amanda Foster at Portland Foot & Ankle Clinic 
        for diabetic neuropathy evaluation. Discussed importance of medication compliance 
        and scheduled follow-up with diabetes educator Sarah Mitchell, RN, CDE.
        
        Plan: 
        - Continue current medications
        - Add Vitamin B12 1000mcg daily
        - Schedule nerve conduction study
        - Return in 4 weeks for lab review
        - Patient given lab order for Quest Diagnostics, account #QD-882991-MT
        
        Billing: Submitted to BlueCross BlueShield, claim reference #CLM-2026-00847291
        
        =============================
        This record contains confidential patient health information protected under HIPAA.
        """;


    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SummarizeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            StatusMessage = IsPrivacyScenario ? "Please load the health record first" : "Please enter text to summarize";
            return;
        }

        // Validate question is selected in Privacy mode
        if (IsPrivacyScenario && string.IsNullOrWhiteSpace(SelectedQuestion))
        {
            StatusMessage = "Please select a question to ask";
            return;
        }

        // If on Final tab, run comparison instead
        if (SelectedTabIndex == 3)
        {
            await RunComparisonAsync(cancellationToken);
            return;
        }

        IsProcessing = true;
        OutputText = string.Empty;
        Benchmark = BenchmarkResult.Empty;
        StatusMessage = $"Processing with {CurrentService.Name}...";

        try
        {
            var outputBuilder = new StringBuilder();
            
            // In Privacy mode, show the question being asked
            if (IsPrivacyScenario)
            {
                outputBuilder.AppendLine("**Question Asked:**");
                outputBuilder.AppendLine(SelectedQuestion);
                outputBuilder.AppendLine();
                outputBuilder.AppendLine("**Response:**");
                outputBuilder.AppendLine();
                OutputText = outputBuilder.ToString();
            }

            // Build the appropriate prompt based on scenario and tab
            string promptText;
            if (IsPrivacyScenario)
            {
                promptText = SelectedTabIndex switch
                {
                    0 => BuildLocalQuestionPrompt(InputText, SelectedQuestion),  // Local: strict constraints
                    1 => BuildCloudQuestionPrompt(InputText, SelectedQuestion),  // Cloud: has network context (but blocked)
                    2 => $"QUESTION:{SelectedQuestion}\n---\nHEALTH_RECORD:{InputText}", // Hybrid: pass both for parsing
                    _ => BuildLocalQuestionPrompt(InputText, SelectedQuestion)
                };
            }
            else
            {
                promptText = InputText; // Power scenario: just pass the text
            }

            await foreach (var chunk in CurrentService.SummarizeStreamingAsync(
                promptText,
                b => UpdateBenchmark(b),
                cancellationToken))
            {
                outputBuilder.Append(chunk);
                OutputText = outputBuilder.ToString();
            }

            // Cache the summary for this mode
            var finalSummary = outputBuilder.ToString();
            switch (SelectedTabIndex)
            {
                case 0:
                    LocalSummary = finalSummary;
                    break;
                case 1:
                    CloudSummary = finalSummary;
                    break;
                case 2:
                    HybridSummary = finalSummary;
                    break;
            }

            OnPropertyChanged(nameof(CanCompare));
            OnPropertyChanged(nameof(CompletedSummaryCount));

            StatusMessage = $"✓ Completed in {Benchmark.TotalTimeMs:N0}ms ({CompletedSummaryCount}/3 responses done)";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            OutputText = $"An error occurred: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // Local-only prompt: strictly constrained to record data
    private static string BuildLocalQuestionPrompt(string healthRecord, string question) =>
        $"""
        You are a medical assistant analyzing a patient health record.
        
        IMPORTANT CONSTRAINTS:
        - Answer ONLY using information explicitly stated in the health record below
        - Do NOT suggest specific doctors, specialists, or facilities by name unless they appear in the record
        - Do NOT invent or assume information not present in the record
        - If asked about specialists or referrals, describe the TYPE of specialist needed, not specific names
        - If the record lacks information to fully answer, clearly state what's missing
        
        HEALTH RECORD:
        {healthRecord}

        QUESTION:
        {question}

        ANSWER (based strictly on the record above):
        """;

    // Cloud prompt: includes additional network/context data
    private static string BuildCloudQuestionPrompt(string healthRecord, string question) =>
        $"""
        You are a medical assistant with access to both the patient's health record AND their insurance network information.
        
        HEALTH RECORD:
        {healthRecord}

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
        
        QUESTION:
        {question}

        ANSWER (incorporating both record data and network resources):
        """;

    // Generic prompt for power scenario (unchanged behavior)
    private static string BuildQuestionPrompt(string healthRecord, string question) =>
        $"""
        You are a medical assistant helping analyze patient health records. 
        Answer the following question based ONLY on the information provided in the health record.
        Be thorough but concise. If the record doesn't contain enough information to fully answer, say so.

        HEALTH RECORD:
        {healthRecord}

        QUESTION:
        {question}

        ANSWER:
        """;

    private async Task RunComparisonAsync(CancellationToken cancellationToken)
    {
        if (!CanCompare)
        {
            StatusMessage = "Run at least 2 summarizations first";
            return;
        }

        IsProcessing = true;
        OutputText = string.Empty;
        StatusMessage = "Comparing summaries with Cloud AI...";

        try
        {
            var comparisonPrompt = BuildComparisonPrompt();
            var outputBuilder = new StringBuilder();

            await foreach (var chunk in _cloudService.SummarizeStreamingAsync(
                comparisonPrompt,
                null,
                cancellationToken))
            {
                outputBuilder.Append(chunk);
                OutputText = outputBuilder.ToString();
            }

            ComparisonResult = outputBuilder.ToString();
            StatusMessage = "✓ Comparison complete";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            OutputText = $"An error occurred: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private string BuildComparisonPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert at evaluating text summaries. Compare the following summaries of the same source document and determine which is the best quality summary.");
        sb.AppendLine();
        sb.AppendLine("Evaluate based on:");
        sb.AppendLine("- Accuracy: Does it capture the main points correctly?");
        sb.AppendLine("- Completeness: Does it cover the key information?");
        sb.AppendLine("- Clarity: Is it well-written and easy to understand?");
        sb.AppendLine("- Conciseness: Is it appropriately brief without losing important details?");
        sb.AppendLine();

        sb.AppendLine("=== ORIGINAL TEXT ===");
        sb.AppendLine(InputText);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(LocalSummary))
        {
            sb.AppendLine("=== SUMMARY A (Local AI) ===");
            sb.AppendLine(LocalSummary);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(CloudSummary))
        {
            sb.AppendLine("=== SUMMARY B (Cloud AI) ===");
            sb.AppendLine(CloudSummary);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(HybridSummary))
        {
            sb.AppendLine("=== SUMMARY C (Hybrid AI) ===");
            sb.AppendLine(HybridSummary);
            sb.AppendLine();
        }

        sb.AppendLine("Please provide:");
        sb.AppendLine("1. A brief analysis of each summary's strengths and weaknesses");
        sb.AppendLine("2. Your ranking from best to worst");
        sb.AppendLine("3. The winner and why it's the best choice");

        return sb.ToString();
    }

    private void UpdateBenchmark(BenchmarkResult b)
    {
        Benchmark = b;
        
        // Store in the appropriate slot based on selected tab
        switch (SelectedTabIndex)
        {
            case 0:
                LocalBenchmark = b;
                break;
            case 1:
                CloudBenchmark = b;
                break;
            case 2:
                HybridBenchmark = b;
                break;
        }

        // Notify all "best" properties to update
        OnPropertyChanged(nameof(HasAllBenchmarks));
        OnPropertyChanged(nameof(IsLocalBestTime));
        OnPropertyChanged(nameof(IsCloudBestTime));
        OnPropertyChanged(nameof(IsHybridBestTime));
        OnPropertyChanged(nameof(IsLocalBestFirstToken));
        OnPropertyChanged(nameof(IsCloudBestFirstToken));
        OnPropertyChanged(nameof(IsHybridBestFirstToken));
        OnPropertyChanged(nameof(IsLocalBestTokensPerSec));
        OnPropertyChanged(nameof(IsCloudBestTokensPerSec));
        OnPropertyChanged(nameof(IsHybridBestTokensPerSec));
        OnPropertyChanged(nameof(IsLocalBestMemory));
        OnPropertyChanged(nameof(IsCloudBestMemory));
        OnPropertyChanged(nameof(IsHybridBestMemory));
    }

    [RelayCommand]
    private void ClearOutput()
    {
        OutputText = string.Empty;
        Benchmark = BenchmarkResult.Empty;
        LocalBenchmark = BenchmarkResult.Empty;
        CloudBenchmark = BenchmarkResult.Empty;
        HybridBenchmark = BenchmarkResult.Empty;
        LocalSummary = string.Empty;
        CloudSummary = string.Empty;
        HybridSummary = string.Empty;
        ComparisonResult = string.Empty;
        StatusMessage = "Ready";
        
        // Notify all properties to update
        OnPropertyChanged(nameof(HasAllBenchmarks));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CompletedSummaryCount));
    }
}
