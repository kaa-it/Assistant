using System.Text.Json;

public class ChatService
{
    private readonly ILlmService _llm;
    private readonly EnhancedRagPipeline _rag;
    private readonly CitationValidator _validator;
    private const int MaxTokens = 4096;

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
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

                // Add context prefix for /query to emphasize it's about the indexed project
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
}
