# Copilot Instructions for AiComparison

## Project Overview

This is a **.NET MAUI** app demonstrating AI service comparison patterns—local (Apple Intelligence), cloud (Azure OpenAI), and hybrid approaches. Targets iOS/macOS/Android/Windows with .NET 10.

## Build & Run

```bash
# Build for all platforms
dotnet build src/AiComparison.csproj

# Build for specific platform
dotnet build src/AiComparison.csproj -f net10.0-maccatalyst
dotnet build src/AiComparison.csproj -f net10.0-ios
dotnet build src/AiComparison.csproj -f net10.0-android

# Run on Mac Catalyst
dotnet build src/AiComparison.csproj -f net10.0-maccatalyst -t:Run
```

## Architecture

### AI Service Layer (`Services/`)

All AI services implement `IAiService` with streaming support:

| Service | Purpose |
|---------|---------|
| `LocalAiService` | Apple Intelligence on-device (iOS/macOS 26+) |
| `CloudAiService` | Azure OpenAI via `Microsoft.Extensions.AI` |
| `HybridAiService` | Chunked summarization: local splits text → cloud synthesizes |
| `PrivacyHybridAiService` | PII anonymization: local anonymizes → cloud processes → local restores |

The hybrid services demonstrate two patterns:
1. **Chunked processing** - handles documents exceeding local AI context limits (~500 words/chunk)
2. **Privacy-preserving** - regex-based PII detection with placeholder substitution

### Dependency Injection Pattern

Services are registered with keyed DI in `MauiProgram.cs`:
- `"local"` key → Apple Intelligence or fallback to cloud
- `"cloud"` key → Azure OpenAI

Platform-specific code uses `#if IOS || MACCATALYST` for Apple Intelligence features.

### MVVM Structure

- **ViewModels/**: Uses `CommunityToolkit.Mvvm` with `[ObservableProperty]` and `[RelayCommand]`
- **Views**: XAML with compiled bindings (`x:DataType`)
- **Models/**: `BenchmarkResult` and `SummarizationResult` records

## Key Conventions

### AI Client Usage
```csharp
// Use Microsoft.Extensions.AI abstraction
IChatClient _chatClient;

// Non-streaming
var response = await _chatClient.GetResponseAsync(prompt, cancellationToken);

// Streaming (preferred for UX)
await foreach (var update in _chatClient.GetStreamingResponseAsync(prompt))
{
    yield return update.Text;
}
```

### Benchmark Pattern
All AI operations track: `TotalTimeMs`, `FirstTokenLatencyMs`, `TokensPerSecond`, `MemoryDeltaBytes`. Use `Stopwatch` and `GC.GetTotalMemory()`.

### Configuration
Azure OpenAI settings come from `appsettings.json` (embedded resource) or environment variables:
- `AZURE_OPENAI_ENDPOINT`
- `AZURE_OPENAI_DEPLOYMENT`
- `AZURE_OPENAI_KEY`

## Platform Notes

- **Apple Intelligence** requires iOS/macOS 26+ at runtime with proper entitlements
- `#pragma warning disable CA1416` is used for platform compatibility where runtime checks handle availability
- Non-Apple platforms fall back to cloud services for "local" AI
