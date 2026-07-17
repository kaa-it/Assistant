using System.Text.Json;
using Anthropic.Models.Messages;
using ModelContextProtocol.Client;

public static class AnthropicToolMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static List<ToolUnion> ConvertToAnthropicTools(IList<McpClientTool> mcpTools)
    {
        var tools = new List<ToolUnion>();

        foreach (var mcpTool in mcpTools)
        {
            var (properties, required) = ParseMcpSchema(mcpTool.JsonSchema);

            tools.Add(new ToolUnion(new Tool
            {
                Name = mcpTool.Name,
                Description = mcpTool.Description ?? "",
                InputSchema = new InputSchema
                {
                    Type = JsonSerializer.SerializeToElement("object", JsonOptions),
                    Properties = properties,
                    Required = required ?? []
                }
            }));
        }

        return tools;
    }

    private static (Dictionary<string, JsonElement> properties, List<string>? required) ParseMcpSchema(
        JsonElement jsonSchema)
    {
        var properties = new Dictionary<string, JsonElement>();
        List<string>? required = null;

        if (jsonSchema.ValueKind != JsonValueKind.Object)
            return (properties, required);

        if (jsonSchema.TryGetProperty("properties", out var propsElement) &&
            propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElement.EnumerateObject())
            {
                properties[prop.Name] = prop.Value.Clone();
            }
        }

        if (jsonSchema.TryGetProperty("required", out var reqElement) &&
            reqElement.ValueKind == JsonValueKind.Array)
        {
            var reqList = new List<string>();
            foreach (var req in reqElement.EnumerateArray())
            {
                if (req.ValueKind == JsonValueKind.String)
                    reqList.Add(req.GetString()!);
            }
            if (reqList.Count > 0)
                required = reqList;
        }

        return (properties, required);
    }
}
