using System.Text.Json;
using MCPSharp;
using MCPSharp.Model;
using MCPSharp.Model.Schemas;
using MCPSharp.Model.Results;
using OpenAI.Chat;

public static class McpToolMapper
{
    public static List<ChatTool> ToOpenAITools(IEnumerable<Tool> mcpTools)
    {
        var openAiTools = new List<ChatTool>();

        foreach (var mcpTool in mcpTools)
        {
            var parameters = MapSchemaToBinaryData(mcpTool.InputSchema);
            openAiTools.Add(ChatTool.CreateFunctionTool(
                functionName: mcpTool.Name,
                functionDescription: mcpTool.Description ?? "",
                functionParameters: parameters));
        }

        return openAiTools;
    }

    private static BinaryData MapSchemaToBinaryData(InputSchema? schema)
    {
        if (schema == null)
            return BinaryData.FromString("""{"type":"object","properties":{}}""");

        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        if (schema.Properties != null)
        {
            foreach (var prop in schema.Properties)
            {
                properties[prop.Key] = new Dictionary<string, object>
                {
                    ["type"] = MapTypeToOpenAI(prop.Value.Type),
                    ["description"] = prop.Value.Description ?? ""
                };

                if (prop.Value.Required)
                    requiredList.Add(prop.Key);
            }
        }

        var schemaObj = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (requiredList.Count > 0)
            schemaObj["required"] = requiredList;

        return BinaryData.FromObjectAsJson(schemaObj);
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

    public static Dictionary<string, object> ParseToolArguments(BinaryData functionArguments)
    {
        if (functionArguments == null || functionArguments.ToString().Trim() == "{}")
            return new Dictionary<string, object>();

        var json = functionArguments.ToString();
        using var doc = JsonDocument.Parse(json);
        return ParseJsonElement(doc.RootElement);
    }

    private static Dictionary<string, object> ParseJsonElement(JsonElement element)
    {
        var result = new Dictionary<string, object>();

        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = JsonValueToCSharp(prop.Value);
        }

        return result;
    }

    private static object JsonValueToCSharp(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParseJsonElement(element),
            JsonValueKind.Array => element.EnumerateArray().Select(e => (object)JsonValueToCSharp(e)).ToList(),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.ToString()
        };
    }

    public static string FormatToolResult(CallToolResult result)
    {
        if (result.Content == null || result.Content.Length == 0)
            return "(no content)";

        var textParts = result.Content.Where(c => !result.IsError).Select(c => c.Text ?? "").ToList();
        var errorParts = result.Content.Where(c => c.Text != null).Select(c => c.Text!).ToList();

        if (result.IsError)
        {
            return "Error executing tool: " + string.Join("; ", errorParts);
        }

        return string.Join("\n", textParts);
    }
}
