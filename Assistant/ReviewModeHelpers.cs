using System.Text.Json;

// Helper classes for review mode LLM responses (used in Program.cs)
internal class QwenChatResponseForReview
{
    [System.Text.Json.Serialization.JsonPropertyName("choices")]
    public List<QwenChoiceForReview>? Choices { get; set; }
}

internal class QwenChoiceForReview
{
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public QwenMessageForReview? Message { get; set; }
}

internal class QwenMessageForReview
{
    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string? Content { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
    public List<QwenToolCallForReview>? ToolCalls { get; set; }
}

internal class QwenToolCallForReview
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("function")]
    public QwenFunctionForReview? Function { get; set; }
}

internal class QwenFunctionForReview
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}
