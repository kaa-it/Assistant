using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public static class McpToolMapper
{
    public static List<Dictionary<string, object>> BuildTools(IList<McpClientTool> mcpTools)
    {
        var tools = new List<Dictionary<string, object>>();

        foreach (var mcpTool in mcpTools)
        {
            var toolObj = new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = mcpTool.Name,
                    ["description"] = mcpTool.Description ?? ""
                }
            };

            var parameters = MapSchemaToDictionary(mcpTool.JsonSchema);
            ((Dictionary<string, object>)toolObj["function"])["parameters"] = parameters;

            tools.Add(toolObj);
        }

        return tools;
    }

    public static Dictionary<string, object> MapSchemaToDictionary(JsonElement jsonSchema)
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
            {
                if (req.GetString() is string r)
                    requiredList.Add(r);
            }
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

    public static Dictionary<string, object> ParseToolArguments(BinaryData functionArguments)
    {
        if (functionArguments == null || functionArguments.ToString().Trim() == "{}")
            return new Dictionary<string, object>();

        var json = functionArguments.ToString();
        using var doc = JsonDocument.Parse(json);
        return ParseJsonElement(doc.RootElement);
    }

    public static Dictionary<string, object> ParseToolCallArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return new Dictionary<string, object>();

        using var doc = JsonDocument.Parse(arguments);
        return ParseJsonElement(doc.RootElement);
    }

    public static Dictionary<string, object> ParseJsonElement(JsonElement element)
    {
        var result = new Dictionary<string, object>();

        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = JsonValueToCSharp(prop.Value);
        }

        return result;
    }

    public static object JsonValueToCSharp(JsonElement element)
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
        if (result.Content == null || !result.Content.Any())
            return "(no content)";

        var textParts = new List<string>();
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textContent)
                textParts.Add(textContent.Text ?? "");
        }

        if (result.IsError == true)
        {
            return "Error executing tool: " + string.Join("; ", textParts);
        }

        return string.Join("\n", textParts);
    }
}
