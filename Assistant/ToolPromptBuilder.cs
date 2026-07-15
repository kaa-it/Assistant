using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

/// <summary>
/// Formats MCP tools into the Qwen chat template format for embedding in the system prompt.
/// The model (qwen3.6-35b-a3b) has native MCP support via chat template,
/// meaning tools are passed as part of the system message rather than a separate "tools" API parameter.
/// </summary>
public static class ToolPromptBuilder
{
    public const string ToolsSectionPrefix = "\n\n# Tools\n\nYou may call one or more functions to assist with the user query.\n\nYou are provided with function signatures within <tools></tools> XML tags:\n<tools>";
    public const string ToolsSectionSuffix = """</tools>\n\nFor each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags: <tool_call>{"name": "<function-name>", "arguments": <args-json-object>}</tool_call>\n""";

    /// <summary>
    /// Builds a tool instructions string from MCP tools, formatted for the Qwen chat template.
    /// Output example:
    /// <tools>
    /// {"type": "function", "function": {"name": "...", "description": "...", "parameters": {...}}}
    /// </tools>
    /// For each function call, return a json object with function name and arguments within `` XML tags:
    /// ``{"name": "...", "arguments": {...}}``
    /// </summary>
    public static string BuildToolInstructions(IList<McpClientTool> mcpTools)
    {
        if (mcpTools == null || mcpTools.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(ToolsSectionPrefix + "[");

        var s = 0;

        foreach (var mcpTool in mcpTools)
        {
            if (s != 0)
            {
                sb.AppendLine(",");
            }
            
            var toolObj = new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = mcpTool.Name,
                    ["description"] = mcpTool.Description ?? "",
                }
            };

            var parameters = MapSchemaToDictionary(mcpTool.JsonSchema);
            ((Dictionary<string, object>)toolObj["function"])["parameters"] = parameters;

            sb.Append(JsonSerializer.Serialize(toolObj));

            s++;
        }

        sb.AppendLine("]" + ToolsSectionSuffix);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a complete system prompt with tools embedded for native MCP support via chat template.
    /// The model receives tool definitions as part of the system message, enabling native tool calling.
    /// </summary>
    public static string BuildSystemPromptWithTools(string baseSystemPrompt, IList<McpClientTool> mcpTools)
    {
        var toolInstructions = BuildToolInstructions(mcpTools);

        if (string.IsNullOrEmpty(toolInstructions))
            return baseSystemPrompt;

        return baseSystemPrompt + toolInstructions;
    }

    private static Dictionary<string, object> MapSchemaToDictionary(JsonElement jsonSchema)
    {
        if (jsonSchema.ValueKind == JsonValueKind.Null || jsonSchema.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() };

        var schemaObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonSchema.GetRawText())
            ?? new Dictionary<string, JsonElement>();

        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        if (schemaObj.TryGetValue("properties", out var propsElement) &&
            propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                var propObj = new Dictionary<string, object>();

                if (prop.Value.TryGetProperty("type", out var typeProp))
                    propObj["type"] = MapTypeToOpenAI(typeProp.GetString() ?? "string");

                if (prop.Value.TryGetProperty("description", out var descProp) && !string.IsNullOrEmpty(descProp.GetString()))
                    propObj["description"] = descProp.GetString();

                properties[prop.Name] = propObj;
            }
        }

        if (schemaObj.TryGetValue("required", out var reqElement) &&
            reqElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in reqElement.EnumerateArray())
                if (req.GetString() is string r) requiredList.Add(r);
        }

        var result = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (requiredList.Count > 0)
            result["required"] = requiredList;

        return result;
    }

    private static string MapTypeToOpenAI(string mcpType)
    {
        return mcpType?.ToLowerInvariant() switch
        {
            "string" => "string",
            "integer" or "int32" or "int64" => "number",
            "number" or "float" or "double" => "number",
            "boolean" or "bool" => "boolean",
            "array" => "array",
            "object" => "object",
            _ => "string"
        };
    }

    /// <summary>
    /// Checks whether tool instructions are embedded in the system prompt.
    /// </summary>
    public static bool HasToolsInPrompt(string systemPrompt) =>
        !string.IsNullOrEmpty(systemPrompt) && systemPrompt.Contains(ToolsSectionPrefix);

    /// <summary>
    /// Extracts tool names from a system prompt that was built with ToolPromptBuilder.
    /// Returns the list of tool names found in the <tools> section.
    /// </summary>
    public static List<string>? ExtractToolNames(string systemPrompt)
    {
        var startIndex = systemPrompt.IndexOf(ToolsSectionPrefix);
        if (startIndex < 0) return null;

        var endIndex = systemPrompt.IndexOf(ToolsSectionSuffix, startIndex);
        if (endIndex < 0) return null;

        var toolsBlock = systemPrompt[(startIndex + ToolsSectionPrefix.Length)..endIndex];
        var toolNames = new List<string>();

        // Parse JSON function objects from the tools block
        using var doc = JsonDocument.Parse($"[{toolsBlock}]");

        foreach (var toolObj in doc.RootElement.EnumerateArray())
        {
            if (toolObj.TryGetProperty("function", out var funcProp) &&
                funcProp.TryGetProperty("name", out var nameProp))
            {
                toolNames.Add(nameProp.GetString() ?? "");
            }
        }

        return toolNames.Count > 0 ? toolNames : null;
    }
}
