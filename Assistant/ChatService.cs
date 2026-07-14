using System.Net.Http.Json;
using System.Text.Json;
using OpenAI.Chat;

public class ChatService
{
    private readonly ILlmService _llm;
    private readonly EnhancedRagPipeline _rag;
    private readonly CitationValidator _validator;
    private const int MaxTokens = 4096;

    // MCP integration (optional)
    private readonly McpServerManager? _mcpManager;
    private readonly ToolExecutor? _toolExecutor;
    private readonly List<ChatTool>? _openAiTools;

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
    }

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator,
        McpServerManager mcpManager) : this(llm, rag, validator)
    {
        _mcpManager = mcpManager;

        if (mcpManager.IsConnected && mcpManager.Tools != null)
        {
            _openAiTools = McpToolMapper.ToOpenAITools(mcpManager.Tools);
            _toolExecutor = new ToolExecutor(mcpManager);

            Console.WriteLine($"\nИнструменты MCP доступны: {_openAiTools.Count}");
        }
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

        var systemPrompt = PromptBuilder.SystemPrompt;

        var history = session.GetHistoryContext(6);
        if (!string.IsNullOrEmpty(history))
            systemPrompt += "\n\n[DIALOG HISTORY]\n" + history;

        var userPrompt = PromptBuilder.BuildUserPrompt(userMessage, ragResult.Chunks, ragResult.Confidence);

        CitationAnswer? parsedAnswer = null;
        var maxRetries = GetEnvInt("RAG_MAX_RETRIES", 3);

        if (_openAiTools != null && _toolExecutor != null)
        {
            parsedAnswer = await ProcessWithToolCallingAsync(userPrompt, systemPrompt, ragResult, maxRetries, ct);
        }
        else
        {
            parsedAnswer = await ProcessWithoutToolsAsync(userPrompt, systemPrompt, ragResult, maxRetries, ct);
        }

        return parsedAnswer;
    }

    private async Task<CitationAnswer> ProcessWithToolCallingAsync(
        string userPrompt, string systemPrompt, RagResult ragResult, int maxRetries, CancellationToken ct)
    {
        var llmApiUrl = Environment.GetEnvironmentVariable("LLM_API_URL") ?? "http://192.168.1.15:1234";
        var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "qwen/qwen3.6-35b-a3b";
        var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(llmApiUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };

        if (!string.IsNullOrEmpty(llmApiKey))
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", llmApiKey);

        var messages = new List<Dictionary<string, string>>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt });

        var currentRagResult = ragResult;
        CitationAnswer? lastParsedAnswer = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var request = new Dictionary<string, object>
            {
                ["model"] = llmModel,
                ["messages"] = messages,
                ["max_tokens"] = MaxTokens,
                ["stream"] = false,
                ["temperature"] = 0.2,
                ["top_p"] = 0.9,
            };

            if (_openAiTools != null)
                request["tools"] = _openAiTools.Select(t => SerializeChatToolToJson(t)).ToList();

            request["tool_choice"] = "auto";

            var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ToolChatResponse>(ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (choice == null) continue;

            var toolCalls = choice.ToolCalls ?? [];
            string? content = choice.Message?.Content;

            if (!string.IsNullOrEmpty(content) && toolCalls.Count == 0)
            {
                var rawResponse = content;
                lastParsedAnswer = CitationAnswerParser.Parse(rawResponse, currentRagResult!.Chunks);

                if (lastParsedAnswer.Confidence == ConfidenceLevel.Unknown && !currentRagResult.IsUnknown)
                {
                    if (attempt < maxRetries)
                    {
                        userPrompt += "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]";
                        continue;
                    }
                }

                if (!GetEnvBool("RAG_ENABLE_VALIDATION", true))
                    break;

                var validation = _validator.Validate(lastParsedAnswer, currentRagResult.Chunks);
                if (validation.IsValid)
                    break;

                if (attempt < maxRetries)
                {
                    userPrompt += $"\n\n[SYSTEM FEEDBACK: Previous response had validation errors: {string.Join("; ", validation.Errors)}. " +
                        $"Please fix and respond with valid JSON only.]";
                }
                else
                {
                    lastParsedAnswer = CreateFallbackAnswer(currentRagResult);
                }

                currentRagResult = null;
            }

            if (toolCalls.Count > 0)
            {
                Console.WriteLine($"\nLLM запросил(а) {toolCalls.Count} инструмент(ов)...");

                foreach (var toolCall in toolCalls)
                {
                    var arguments = McpToolMapper.ParseToolArguments(BinaryData.FromString(toolCall.FunctionArgs?.ToString() ?? "{}"));

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[Инструмент: {toolCall.Function?.Name}]");
                    if (arguments.Count > 0)
                        Console.WriteLine($"Аргументы: {JsonSerializer.Serialize(arguments)}");
                    Console.ResetColor();

                    var callResult = await _mcpManager!.CallToolAsync(toolCall.Function?.Name ?? "", arguments);

                    var formattedResult = McpToolMapper.FormatToolResult(callResult);
                    Console.ForegroundColor = ConsoleColor.Gray;
                    var preview = formattedResult.Length > 200 ? formattedResult[..200] + "..." : formattedResult;
                    Console.WriteLine($"Результат: {preview}");
                    Console.ResetColor();

                    messages.Add(new Dictionary<string, string>
                    {
                        ["role"] = "tool",
                        ["content"] = formattedResult,
                        ["tool_call_id"] = toolCall.Id ?? $"tool_{Guid.NewGuid().ToString("N")[..8]}"
                    });

                }

            }

            currentRagResult = null;
        }

        lastParsedAnswer ??= CreateFallbackAnswer(ragResult, "All retries failed.");
        return lastParsedAnswer;
    }

    private async Task<CitationAnswer> ProcessWithoutToolsAsync(
        string userPrompt, string systemPrompt, RagResult ragResult, int maxRetries, CancellationToken ct)
    {
        CitationAnswer? parsedAnswer = null;
        var lastException = "";

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var rawResponse = await _llm.AskAsync(userPrompt, systemPrompt, MaxTokens, ct);

                parsedAnswer = CitationAnswerParser.Parse(rawResponse, ragResult.Chunks);

                if (parsedAnswer.Confidence == ConfidenceLevel.Unknown && !ragResult.IsUnknown)
                {
                    if (attempt < maxRetries)
                    {
                        userPrompt += "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]";
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
                }
                else
                {
                    parsedAnswer = CreateFallbackAnswer(ragResult);
                }
            }
            catch (JsonException ex)
            {
                lastException = ex.Message;
                if (attempt < maxRetries)
                {
                    userPrompt += "\n\n[SYSTEM FEEDBACK: Your previous response was not valid JSON. " +
                        "Respond with raw JSON only.]";
                }
                else
                {
                    parsedAnswer = CreateFallbackAnswer(ragResult, $"JSON parse error: {ex.Message}");
                }
            }
        }

        parsedAnswer ??= CreateFallbackAnswer(ragResult, $"All {maxRetries} retries failed. Last error: {lastException}");

        return parsedAnswer;
    }

    public async Task RunInteractiveAsync(CancellationToken ct = default)
    {
        var session = new ChatSession();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║           RAG CHAT — interactive mode            ║");
        Console.WriteLine("╠══════════════════════════════════════════════════╣");
        Console.WriteLine("║ /help  — commands                                ║");
        Console.WriteLine("║ /exit  — quit                                    ║");
        Console.WriteLine("║ /reset — new session                             ║");
        Console.WriteLine("║ /query <question> — ask about indexed project    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.ResetColor();

        while (!ct.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"\nChat [{session.SessionId}] > ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input == null) break;

            var trimmed = input.Trim();

            if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (trimmed.Equals("/reset", StringComparison.OrdinalIgnoreCase))
            {
                session = new ChatSession();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Session reset.");
                Console.ResetColor();
                continue;
            }

            if (trimmed.Equals("/state", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Session has " + session.History.Count + " messages.");
                continue;
            }

            if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Commands: /exit, /reset, /state, /query <question>, /help");
                continue;
            }

            string question = trimmed;
            bool isQueryCommand = false;

            if (trimmed.StartsWith("/query ", StringComparison.OrdinalIgnoreCase))
            {
                question = trimmed["/query ".Length..].Trim();
                isQueryCommand = true;
            }

            if (isQueryCommand)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n[Querying indexed project...]");
                Console.ResetColor();

                question = "[This is a query about the indexed project. Base your answer on the retrieved context chunks from the indexed documentation.] " + question;
            }

            session.AddUserMessage(question);

            try
            {
                var answer = await ProcessMessageAsync(question, session, ct);
                session.AddAssistantMessage(answer);

                if (answer.Confidence == ConfidenceLevel.Unknown && answer.ClarificationRequest != null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n{answer.ClarificationRequest}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"\n{answer.Answer}");
                }

                if (answer.Sources.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("\n--- Sources ---");
                    foreach (var src in answer.Sources)
                    {
                        Console.WriteLine($"  [{src.ChunkIndex}] {src.Source}{(src.Section != null ? $" ({src.Section})" : "")} (score: {src.RelevanceScore:F3})");
                    }
                    Console.ResetColor();
                }

                if (answer.Citations.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("--- Citations ---");
                    foreach (var cit in answer.Citations)
                    {
                        var quotePreview = cit.Quote.Length > 100 ? cit.Quote[..100] + "..." : cit.Quote;
                        Console.WriteLine($"  [{cit.SourceIndex}] \"{quotePreview}\"");
                    }
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
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

    private static string SerializeChatToolToJson(ChatTool tool)
    {
        try
        {
            var data = ((System.ClientModel.Primitives.IPersistableModel<ChatTool>)tool).Write(null);
            return data.ToString();
        }
        catch { /* fallback below */ }

        var rawData = ((System.ClientModel.Primitives.IPersistableModel<ChatTool>)tool).Write(null);
        return rawData.ToString();
    }

    private class ToolChatResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("choices")]
        public List<ToolChoice>? Choices { get; set; }
    }

    private class ToolChoice
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public ToolMessage? Message { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
        public List<ToolCall>? ToolCalls { get; set; }
    }

    private class ToolMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private class ToolCall
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("function")]
        public ToolFunction? Function { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("function_args")]
        public string? FunctionArgs { get; set; }
    }

    private class ToolFunction
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }
}
