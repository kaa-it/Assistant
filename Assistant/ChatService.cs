using System.Text.Json;
using System.Text.Json.Serialization;

public class ChatService
{
    private readonly ILlmService _llm;
    private readonly EnhancedRagPipeline _rag;
    private readonly CitationValidator _validator;
    private readonly McpGitClient? _mcpClient;
    private async Task<List<ToolDefinition>?> GetToolDefinitionsAsync()
    {
        if (_mcpClient == null) return null;
        var tools = await _mcpClient.GetMcpToolsDefinitionsAsync();
        return tools?.ToList();
    }

    private async Task<string?> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(toolCall.Name)) return null;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Executing MCP tool: {toolCall.Name}]");
        Console.ResetColor();

        var result = await _mcpClient!.CallToolAsync(toolCall.Name, toolCall.Arguments);
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var preview = result.Length > 300 ? result[..300] + "..." : result;
        Console.WriteLine($"[MCP tool '{toolCall.Name}' result: {preview}]");
        Console.ResetColor();

        return $"[MCP TOOL RESULT for '{toolCall.Name}]:\n{result}";
    }

    private const int MaxTokens = 4096;

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator,
        McpGitClient? mcpClient = null)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
        _mcpClient = mcpClient;
    }

    public async Task<CitationAnswer> ProcessMessageAsync(string userMessage, ChatSession session, CancellationToken ct = default)
    {
        var ragResult = await _rag.ExecuteAsync(userMessage, RagPipelineMode.CitationEnforced, ct);

        if (ragResult.IsUnknown)
        {
            return new CitationAnswer(
                Answer: "",
                Confidence: ConfidenceLevel.Unknown,
                ClarificationRequest: "Не найдено релевантного контекста. Переформулируйте вопрос.",
                Sources: [],
                Citations: []
            );
        }

        var systemPrompt = PromptBuilder.BuildSystemPrompt();

        var history = session.GetHistoryContext(6);
        if (!string.IsNullOrEmpty(history))
            systemPrompt += "\n\n[DIALOG HISTORY]\n" + history;

        var userPrompt = PromptBuilder.BuildUserPrompt(userMessage, ragResult.Chunks, ragResult.Confidence);

        CitationAnswer? parsedAnswer = null;
        var maxRetries = GetEnvInt("RAG_MAX_RETRIES", 3);

        // Build message history for tool calling (including previous tool results)
        var messages = new List<ChatMessage>();

        // Add system message
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

        // Add previous conversation history (last 6 messages)
        var recentHistory = session.History.AsEnumerable();
        if (session.History.Count > 0 && session.History[^1].Role == ChatRole.User)
            recentHistory = recentHistory.Take(session.History.Count - 1);

        foreach (var msg in recentHistory.TakeLast(6))
        {
            messages.Add(new ChatMessage { Role = msg.Role == ChatRole.User ? "user" : "assistant", Content = msg.Content });
        }

        // Get tool definitions asynchronously (OpenAI Function Calling API)
        var _toolDefinitions = await GetToolDefinitionsAsync();

        // Main loop: send user message, handle tool calls if any, then parse final answer
        var currentMessage = new ChatMessage { Role = "user", Content = userPrompt };

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Add current message to history for this attempt
                messages.Add(currentMessage);

                ToolCallResult? toolResult;
                try 
                {
                    // Try with tools if MCP is available (OpenAI Function Calling API)
                    toolResult = await _llm.AskWithToolsAsync(
                        userPrompt, 
                        systemPrompt, 
                        _toolDefinitions, 
                        MaxTokens, 
                        ct);
                }
                catch (Exception) when (_toolDefinitions == null || _toolDefinitions.Count == 0)
                {
                    // Fallback: no tools available, use text-only API
                    toolResult = await CallWithoutToolsAsync(messages, ct);
                }

                // Check if LLM wants to call a tool (OpenAI Function Calling response)
                if (toolResult.ToolCalls != null && toolResult.ToolCalls.Count > 0)
                {
                    // Add LLM's assistant message with tool_calls to history
                    messages.Add(new ChatMessage 
                    { 
                        Role = "assistant", 
                        Content = toolResult.Content,
                        ToolCalls = toolResult.ToolCalls.Select(tc => new ToolCallData 
                        { 
                            Id = tc.Id,
                            Function = new FunctionCall { Name = tc.Name, Arguments = SerializeArguments(tc.Arguments) }
                        }).ToList() 
                    });

                    // Execute each tool call and add results to messages (OpenAI Function Calling loop)
                    foreach (var tc in toolResult.ToolCalls)
                    {
                        var toolExecResult = await ExecuteToolCallAsync(tc, ct);
                        
                        // Add tool result as a message with role "tool" (OpenAI Function Calling format)
                        messages.Add(new ChatMessage 
                        { 
                            Role = "tool", 
                            Content = toolExecResult,
                            ToolCallId = tc.Id,
                            Name = tc.Name 
                        });

                        Console.WriteLine($"\n{toolExecResult}");
                    }

                    // Reset current message to trigger another LLM call with tool results (OpenAI Function Calling loop)
                    currentMessage = new ChatMessage { Role = "user", Content = "" };
                    continue;
                }

                // No tool calls — parse the final answer as JSON citation response
                var rawContent = toolResult.Content ?? "";

                parsedAnswer = CitationAnswerParser.Parse(rawContent, ragResult.Chunks);

                if (parsedAnswer.Confidence == ConfidenceLevel.Unknown && !ragResult.IsUnknown)
                {
                    if (attempt < maxRetries)
                    {
                        userPrompt += "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]";
                        currentMessage = new ChatMessage { Role = "user", Content = userPrompt };
                        continue;
                    }
                }

                if (!GetEnvBool("RAG_ENABLE_VALIDATION", true))
                    break;

                var validation = _validator.Validate(parsedAnswer, ragResult.Chunks);

                if (validation.IsValid)
                    break;

                if (attempt < maxRetries)
                {
                    userPrompt += $"\n\n[SYSTEM FEEDBACK: Previous response had validation errors: {string.Join("; ", validation.Errors)}. " +
                        $"Please fix and respond with valid JSON only.]";
                    currentMessage = new ChatMessage { Role = "user", Content = userPrompt };
                }
                else
                {
                    parsedAnswer = CreateFallbackAnswer(ragResult);
                }
            }
            catch (JsonException ex)
            {
                if (attempt < maxRetries)
                {
                    userPrompt += "\n\n[SYSTEM FEEDBACK: Your previous response was not valid JSON. " +
                        "Respond with raw JSON only.]";
                    currentMessage = new ChatMessage { Role = "user", Content = userPrompt };
                }
                else
                {
                    parsedAnswer = CreateFallbackAnswer(ragResult, $"JSON parse error: {ex.Message}");
                }
            }
        }

        parsedAnswer ??= CreateFallbackAnswer(ragResult);

        return parsedAnswer;
    }

    private async Task<ToolCallResult> CallWithoutToolsAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        // Build a text-only request by reconstructing the prompt from messages
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var userMessages = messages.Where(m => m.Role != "system").ToList();

        // For text-only fallback, we need to reconstruct the user prompt
        var lastUserMsg = userMessages.LastOrDefault(m => m.Role == "user");
        if (lastUserMsg != null)
            return await _llm.AskAsync(lastUserMsg.Content!, systemMsg, MaxTokens, ct)
                .ContinueWith(t => new ToolCallResult(Content: t.Result ?? "", ToolCalls: []));

        return await _llm.AskAsync("", systemMsg, MaxTokens, ct)
            .ContinueWith(t => new ToolCallResult(Content: t.Result ?? "", ToolCalls: []));
    }

    private static string SerializeArguments(Dictionary<string, object?> args)
    {
        try 
        {
            return System.Text.Json.JsonSerializer.Serialize(args);
        }
        catch 
        {
            return "{}";
        }
    }

    private static CitationAnswer CreateFallbackAnswer(RagResult ragResult, string? errorReason = null)
    {
        if (ragResult.Chunks.Count == 0)
        {
            return new CitationAnswer(
                Answer: "Unable to generate an answer based on the retrieved context.",
                Confidence: ConfidenceLevel.Unknown,
                ClarificationRequest: errorReason ?? "No relevant chunks were retrieved.",
                Sources: [],
                Citations: []
            );
        }

        var topChunk = ragResult.Chunks.OrderByDescending(c => c.FinalScore).First();
        var quote = CitationAnswerParser.ExtractSafeQuote(topChunk.Chunk.Content, 150);

        return new CitationAnswer(
            Answer: $"Based on the retrieved context [CITATION:0]: {quote}",
            Confidence: ConfidenceLevel.Low,
            ClarificationRequest: errorReason,
            Sources:
            [
                new SourceReference(
                    Source: topChunk.Chunk.Source,
                    Section: topChunk.Chunk.Section,
                    ChunkId: topChunk.Chunk.ChunkId,
                    RelevanceScore: (float)topChunk.FinalScore,
                    ChunkIndex: topChunk.Chunk.ChunkIndex,
                    TotalChunks: topChunk.Chunk.TotalChunks
                )
            ],
            Citations:
            [
                new Citation(Quote: quote, SourceIndex: 0, Explanation: errorReason ?? "Top retrieved chunk")
            ]
        );
    }

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;

    private static bool GetEnvBool(string name, bool defaultValue) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;

    // Internal types for tool calling messages (serialized to JSON)
    private class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; } = "";
        [JsonPropertyName("tool_calls")] public List<ToolCallData>? ToolCalls { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private class ToolCallData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public FunctionCall? Function { get; set; }
    }

    private class FunctionCall
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    private class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = [];
    }

    private class Choice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }

    private class HttpClient : System.Net.Http.HttpClient
    {
        // This is just for the internal request building — we need to access _httpClient from OpenAiCompatibleLlmService
        // Actually, ChatService doesn't have direct HTTP access. We need to refactor.
    }

    public async Task RunInteractiveAsync()
    {
        Console.WriteLine("Интерактивный режим. Введите 'quit' для выхода.\n");

        var session = new ChatSession();
        
        while (true)
        {
            Console.Write("Вы: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            Console.WriteLine("\nОбработка...\n");

            var result = await ProcessMessageAsync(input, session);

            if (!string.IsNullOrEmpty(result.Answer))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Ответ: {result.Answer}");
                Console.ResetColor();

                if (result.Sources.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"\nИсточники ({result.Sources.Count}):");
                    foreach (var source in result.Sources)
                    {
                        Console.WriteLine($"  [{source.ChunkIndex}] {source.Source}{(string.IsNullOrEmpty(source.Section) ? "" : $" ({source.Section})")}");
                    }
                    Console.ResetColor();
                }

                session.AddAssistantMessage(result);
            }
        }
    }
}
