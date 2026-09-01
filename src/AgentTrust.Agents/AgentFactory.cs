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
    public static IPaymentAgent CreateScripted(string agentId, string scriptedJsonResponse)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new ScriptedChatCompletionService(scriptedJsonResponse));
        var kernel = builder.Build();
        return new SemanticKernelPaymentAgent(agentId, kernel);
    }

    /// <summary>
    /// Builds a live agent against an OpenAI-compatible connector. Reads OPENAI_API_KEY
    /// (required) and OPENAI_MODEL (defaults to gpt-4o-mini) from environment variables.
    /// Throws InvalidOperationException if OPENAI_API_KEY is not set — callers should check
    /// IsLiveModeConfigured first if they want to fall back gracefully.
    /// </summary>
    public static IPaymentAgent CreateLive(string agentId)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY is not set. Set it to run the agent against a real LLM, or use AgentFactory.CreateScripted for deterministic/offline runs.");
        }

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(model, apiKey);
        var kernel = builder.Build();
        return new SemanticKernelPaymentAgent(agentId, kernel);
    }

    public static bool IsLiveModeConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
}
