public interface ILlmService : IDisposable
{
    Task<string> AskAsync(string prompt, string? systemPrompt = null, int? maxTokens = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a chat request with tool definitions. If the model decides to call a tool,
    /// returns the list of tool calls (name + arguments). Otherwise returns null content.
    /// </summary>
    Task<ToolCallResult?> AskWithToolsAsync(
        string userMessage,
        string? systemPrompt = null,
        IEnumerable<ToolDefinition>? tools = null,
        int? maxTokens = null,
        CancellationToken ct = default);
}

public record ToolCallResult(
    string? Content,
    List<ToolCall> ToolCalls);

public record ToolCall(
    string Id,
    string Name,
    Dictionary<string, object?> Arguments);

public record ToolDefinition(
    string Name,
    string Description,
    object? InputSchema = null);
