# AI Comparison

A .NET MAUI app that compares local, cloud, and hybrid AI approaches for text summarization. Demonstrates Apple Intelligence (on-device), Azure OpenAI (cloud), and hybrid patterns that combine both.

## Demo

https://github.com/davidortinau/AiComparison/raw/refs/heads/main/media/aicomparison_demo.mp4

## Requirements

- .NET 10 SDK
- For iOS/macOS: Xcode 16+ and macOS
- For Android: Android SDK
- Azure OpenAI resource (for cloud features)

## Build

```bash
# All platforms
dotnet build src/AiComparison.csproj

# Specific platform
dotnet build src/AiComparison.csproj -f net10.0-maccatalyst
dotnet build src/AiComparison.csproj -f net10.0-ios
dotnet build src/AiComparison.csproj -f net10.0-android
```

## Run

```bash
# Mac Catalyst
dotnet build src/AiComparison.csproj -f net10.0-maccatalyst -t:Run

# iOS Simulator
dotnet build src/AiComparison.csproj -f net10.0-ios -t:Run
```

## Configuration

Set Azure OpenAI credentials via environment variables:

```bash
export AZURE_OPENAI_ENDPOINT="https://your-resource.cognitiveservices.azure.com/"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o-mini"
export AZURE_OPENAI_KEY="your-api-key"
```

Or edit `src/appsettings.json`.

## Features

### Scenarios

**Power**: Compare raw summarization speed and quality across all providers.

**Privacy**: Analyze health records with PII protection. Local AI works directly, cloud is blocked, and hybrid anonymizes data before cloud processing.

### AI Services

| Service | Description |
|---------|-------------|
| Local | Apple Intelligence on-device. Requires iOS/macOS 26+. |
| Cloud | Azure OpenAI. Requires network and API credentials. |
| Hybrid | Local summarizes chunks, cloud synthesizes final result. Handles large documents. |
| Privacy Hybrid | Local anonymizes PII, cloud processes, local restores original values. |

### Benchmarks

Each summarization tracks:
- Total time (ms)
- First token latency (ms)
- Tokens per second
- Memory delta (MB)

## Project Structure

```
src/
  Services/       # AI service implementations
  ViewModels/     # MVVM view models (CommunityToolkit.Mvvm)
  Models/         # Data models
  MainPage.xaml   # Main UI
  MauiProgram.cs  # App configuration and DI setup
```

## Dependencies

- Microsoft.Extensions.AI
- Azure.AI.OpenAI
- CommunityToolkit.Mvvm
- Microsoft.Maui.Essentials.AI (Apple Intelligence)
