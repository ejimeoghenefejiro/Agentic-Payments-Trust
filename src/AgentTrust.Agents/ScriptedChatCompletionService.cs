using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentTrust.Agents;

/// <summary>
/// Deterministic stand-in for a real LLM chat-completion connector. Used for reproducible
/// tests and for offline/no-API-key experiments, matching the concept document's separation
/// between deterministic tests and LLM-assisted evaluation. Always returns the configured
/// canned response regardless of the prompt, so scenario ground truth stays reproducible.
/// </summary>
public sealed class ScriptedChatCompletionService : IChatCompletionService
{
    private readonly string _response;

    public ScriptedChatCompletionService(string response) => _response = response;

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> result = new List<ChatMessageContent>
        {
            new(AuthorRole.Assistant, _response)
        };
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, _response);
        await Task.CompletedTask;
    }
}
