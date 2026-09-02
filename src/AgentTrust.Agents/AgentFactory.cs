using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentTrust.Agents;

/// <summary>
/// Builds an IPaymentAgent backed by a real LLM connector when credentials are configured
/// (OPENAI_API_KEY / OPENAI_MODEL environment variables), or a deterministic scripted
/// connector for reproducible tests and offline experiments. This is also the seam for
/// Priority 8 cross-model experiments: swap the connector, keep everything downstream
/// identical.
/// </summary>
public static class AgentFactory
{
    /// <summary>
    /// Optional overrides, set once at process startup from configuration (e.g. a host reads
    /// "OpenAI:ApiKey" / "OpenAI:Model" out of appsettings.json and assigns these). Takes
    /// priority over the OPENAI_API_KEY / OPENAI_MODEL environment variables when set. Kept as
    /// static settable properties rather than a constructor/DI dependency so this stays a
    /// plain static factory usable from the Runner (no DI container) and the Api (DI container)
    /// alike.
    /// </summary>
    public static string? ConfiguredApiKey { get; set; }
    public static string? ConfiguredModel { get; set; }

    public static IPaymentAgent CreateScripted(string agentId, string scriptedJsonResponse)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new ScriptedChatCompletionService(scriptedJsonResponse));
        var kernel = builder.Build();
        return new SemanticKernelPaymentAgent(agentId, kernel);
    }

    /// <summary>
    /// Builds a live agent against an OpenAI-compatible connector. Resolves the API key from
    /// ConfiguredApiKey first, then the OPENAI_API_KEY environment variable; same precedence
    /// for the model (default gpt-4o-mini). Throws InvalidOperationException if no key is
    /// configured either way — callers should check IsLiveModeConfigured first if they want to
    /// fall back gracefully.
    /// </summary>
    public static IPaymentAgent CreateLive(string agentId)
        => new SemanticKernelPaymentAgent(agentId, CreateLiveKernel());

    /// <summary>Creates a live Semantic Kernel for bounded agents such as the Level-3 financial
    /// investigator. The caller decides which tool surface the agent can access.</summary>
    public static Kernel CreateLiveKernel()
    {
        var apiKey = ConfiguredApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key configured. Set AgentFactory.ConfiguredApiKey, the OPENAI_API_KEY environment variable, " +
                "or use AgentFactory.CreateScripted for deterministic/offline runs.");
        }

        var model = ConfiguredModel ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(model, apiKey);
        return builder.Build();
    }

    public static bool IsLiveModeConfigured =>
        !string.IsNullOrWhiteSpace(ConfiguredApiKey) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
}
