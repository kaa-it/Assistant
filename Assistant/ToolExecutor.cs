using System.Text.Json;
using MCPSharp;
using OpenAI.Chat;

public class ToolExecutor
{
    private readonly McpServerManager _mcpManager;

    public ToolExecutor(McpServerManager mcpManager)
    {
        _mcpManager = mcpManager;
    }

    public async Task<ToolChatMessage> ExecuteToolCallAsync(ChatToolCall toolCall, CancellationToken ct = default)
    {
        var arguments = McpToolMapper.ParseToolArguments(toolCall.FunctionArguments);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Инструмент: {toolCall.FunctionName}]");
        if (arguments.Count > 0)
            Console.WriteLine($"Аргументы: {JsonSerializer.Serialize(arguments)}");
        Console.ResetColor();

        var result = await _mcpManager.CallToolAsync(toolCall.FunctionName, arguments);

        var formattedResult = McpToolMapper.FormatToolResult(result);
        Console.ForegroundColor = ConsoleColor.Gray;
        var preview = formattedResult.Length > 200 ? formattedResult[..200] + "..." : formattedResult;
        Console.WriteLine($"Результат: {preview}");
        Console.ResetColor();

        return ToolChatMessage.CreateToolMessage(
            toolCallId: GetToolCallId(toolCall),
            content: formattedResult);
    }

    private static string GetToolCallId(ChatToolCall toolCall)
    {
        return $"tool_{Guid.NewGuid().ToString("N")[..8]}";
    }
}
