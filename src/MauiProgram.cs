using System.Reflection;
using AiComparison.Services;
using AiComparison.ViewModels;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
#if IOS || MACCATALYST
using Microsoft.Maui.Essentials.AI;
#endif

namespace AiComparison;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Load configuration
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("AiComparison.appsettings.json");
        
        var configBuilder = new ConfigurationBuilder();
        if (stream != null)
        {
            configBuilder.AddJsonStream(stream);
        }
        
        var config = configBuilder.Build();
        builder.Configuration.AddConfiguration(config);

        // Configure AI services
        ConfigureAIServices(builder.Services, config);

        // Register ViewModels
        builder.Services.AddTransient<MainViewModel>();

        // Register Pages
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static void ConfigureAIServices(IServiceCollection services, IConfiguration config)
    {
#if IOS || MACCATALYST
        // Register Apple Intelligence chat client for local AI
        // Note: AppleIntelligenceChatClient requires iOS/macOS 26+ at runtime
        // The service will check availability and handle gracefully if not available
#pragma warning disable CA1416 // Platform compatibility - runtime check handles this
        services.AddSingleton<AppleIntelligenceChatClient>();
        services.AddKeyedSingleton<IChatClient>("local", (sp, _) =>
        {
            var client = sp.GetRequiredService<AppleIntelligenceChatClient>();
            return client
                .AsBuilder()
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build();
        });
#pragma warning restore CA1416
#endif

        // Register Azure OpenAI chat client for cloud AI
        services.AddKeyedSingleton<IChatClient>("cloud", (sp, _) =>
        {
            var endpoint = config["AzureOpenAI:Endpoint"] 
                ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                ?? throw new InvalidOperationException("Azure OpenAI endpoint not configured");
            
            var deploymentName = config["AzureOpenAI:DeploymentName"] 
                ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
                ?? "gpt-4o-mini";
            
            var apiKey = config["AzureOpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY")
                ?? throw new InvalidOperationException("Azure OpenAI API key not configured");

            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new System.ClientModel.ApiKeyCredential(apiKey));

            return azureClient
                .GetChatClient(deploymentName)
                .AsIChatClient()
                .AsBuilder()
                .UseLogging(sp.GetRequiredService<ILoggerFactory>())
                .Build();
        });

        // Register AI services
#if IOS || MACCATALYST
        services.AddSingleton<LocalAiService>(sp =>
            new LocalAiService(sp.GetRequiredKeyedService<IChatClient>("local")));
#else
        // Fallback for non-Apple platforms - use cloud for local too
        services.AddSingleton<LocalAiService>(sp =>
            new LocalAiService(sp.GetRequiredKeyedService<IChatClient>("cloud")));
#endif

        services.AddSingleton<CloudAiService>(sp =>
            new CloudAiService(sp.GetRequiredKeyedService<IChatClient>("cloud")));

#if IOS || MACCATALYST
        services.AddSingleton<HybridAiService>(sp =>
            new HybridAiService(
                sp.GetRequiredKeyedService<IChatClient>("local"),
                sp.GetRequiredKeyedService<IChatClient>("cloud")));

        services.AddSingleton<PrivacyHybridAiService>(sp =>
            new PrivacyHybridAiService(
                sp.GetRequiredKeyedService<IChatClient>("local"),
                sp.GetRequiredKeyedService<IChatClient>("cloud")));
#else
        services.AddSingleton<HybridAiService>(sp =>
            new HybridAiService(
                sp.GetRequiredKeyedService<IChatClient>("cloud"),
                sp.GetRequiredKeyedService<IChatClient>("cloud")));

        services.AddSingleton<PrivacyHybridAiService>(sp =>
            new PrivacyHybridAiService(
                sp.GetRequiredKeyedService<IChatClient>("cloud"),
                sp.GetRequiredKeyedService<IChatClient>("cloud")));
#endif
    }
}
