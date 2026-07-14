using System.Text;
using ModelContextProtocol.Client;

public class McpGitClient : IAsyncDisposable
{
    private readonly string _repoPath;
    private McpClient? _client;

    public McpGitClient(string repoPath)
    {
        _repoPath = repoPath;
    }

    public static async Task<McpGitClient> ConnectAsync(string repoPath)
    {
        var client = new McpGitClient(repoPath);
        await client.ConnectAsync();
        return client;
    }

    private async Task ConnectAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "git",
            Command = "uvx",
            Arguments = new[] { "mcp-server-git" },
        });

        _client = await McpClient.CreateAsync(transport);
    }

    public async Task<IList<ToolDefinition>?> GetMcpToolsDefinitionsAsync()
    {
        if (_client == null)
            return null;

        var tools = await _client.ListToolsAsync();
        
        if (tools == null || tools.Count == 0)
            return null;

        var definitions = new List<ToolDefinition>();

        foreach (var tool in tools)
        {
            object? schema = null;

            if (tool.JsonSchema.ValueKind is not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.Undefined)
            {
                var schemaText = tool.JsonSchema.ToString();
                if (!string.IsNullOrEmpty(schemaText) && schemaText != "{}")
                    schema = tool.JsonSchema;
            }

            definitions.Add(new ToolDefinition(
                Name: tool.Name,
                Description: tool.Description ?? "",
                InputSchema: schema
            ));
        }

        return definitions;
    }

    public async Task<string> GetToolsDescription()
    {
        if (_client == null)
            return "No MCP git tools available.";

        var tools = await _client.ListToolsAsync();
        
        if (tools == null || tools.Count == 0)
            return "No MCP git tools available.";

        var sb = new StringBuilder();
        sb.AppendLine("\n=== AVAILABLE MCP GIT TOOLS ===");
        sb.AppendLine("To call a tool, respond with the format: __MCP_TOOL_CALL__: {\"tool\": \"<name>\", \"args\": {...}}");
        sb.AppendLine("The system will execute the tool and return results to you.\n");

        foreach (var tool in tools)
        {
            sb.AppendLine($"  **{tool.Name}**: {tool.Description}");

            var jsonSchema = tool.JsonSchema;
            if (jsonSchema.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                var schemaText = jsonSchema.ToString();
                if (!string.IsNullOrEmpty(schemaText) && schemaText != "{}")
                    sb.AppendLine($"    Schema: {schemaText}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object?> args)
    {
        if (_client == null)
            throw new InvalidOperationException("MCP client not connected.");

        var mergedArgs = new Dictionary<string, object?>(args)
        {
            ["repo_path"] = _repoPath,
        };

        var result = await _client.CallToolAsync(toolName, mergedArgs);
        
        if (result == null)
            return "[MCP ERROR] Null result from tool.";

        if ((bool)(result.IsError ?? false))
            return $"[MCP ERROR] {string.Join("\n", ExtractTextFromContent(result.Content))}";

        return string.Join("\n", ExtractTextFromContent(result.Content));
    }

    private static IEnumerable<string> ExtractTextFromContent(IList<ModelContextProtocol.Protocol.ContentBlock>? content)
    {
        if (content == null) yield break;

        foreach (var block in content)
        {
            var type = block.GetType();
            
            // Try Text property (TextContentBlock)
            var textProp = type.GetProperty("Text");
            if (textProp != null && textProp.PropertyType == typeof(string))
            {
                var val = textProp.GetValue(block) as string;
                if (!string.IsNullOrEmpty(val)) { yield return val; continue; }
            }

            // Try Data property (other content types)
            var dataProp = type.GetProperty("Data");
            if (dataProp != null && dataProp.PropertyType == typeof(string))
            {
                var val = dataProp.GetValue(block) as string;
                if (!string.IsNullOrEmpty(val)) { yield return val; continue; }
            }

            // Fallback: ToString() for non-image types
            if (type.Name.Contains("Image") || type.Name.Contains("Resource")) continue;
            
            var toString = block.ToString();
            if (!string.IsNullOrEmpty(toString)) yield return toString;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try { await _client.DisposeAsync(); } catch { /* ignore on dispose */ }
        }
    }
}
