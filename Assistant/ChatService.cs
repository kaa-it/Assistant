using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ModelContextProtocol.Protocol;
using AnthropicRole = Anthropic.Models.Messages.Role;

public class ChatService
{
    private readonly ILlmService _llm;
    private readonly EnhancedRagPipeline? _rag;
    private readonly CitationValidator? _validator;
    private const int MaxTokens = 50000;

    // MCP integration (optional)
    private readonly McpServerManager? _mcpManager;

    // Path to the cloned repository (for LLM awareness)
    private readonly string? _repoPath;

    // GitLab project ID (for LLM awareness in chat mode)
    private readonly string? _projectId;

    // Anthropic integration (optional)
    private readonly AnthropicClient? _anthropicClient;
    private readonly string? _anthropicModel;
    private readonly bool _useAnthropic;

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator) : this(llm, rag: rag, validator: validator, mcpManager: null, repoPath: null, projectId: null)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
    }

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator,
        McpServerManager mcpManager) : this(llm, rag: rag, validator: validator, mcpManager: mcpManager, repoPath: null, projectId: null)
    {
        _mcpManager = mcpManager;

        if (mcpManager.IsConnected && mcpManager.Tools != null)
            Console.WriteLine($"\nИнструменты MCP доступны: {mcpManager.Tools.Count} (серверов: {mcpManager.ServerCount})");
    }

    public ChatService(
        ILlmService llm,
        McpServerManager mcpManager,
        string? projectId = null) : this(llm, null, validator: null, mcpManager: mcpManager, repoPath: null, projectId: projectId)
    {
        _llm = llm;

        if (mcpManager.IsConnected && mcpManager.Tools != null)
            Console.WriteLine($"\nИнструменты MCP доступны: {mcpManager.Tools.Count} (серверов: {mcpManager.ServerCount})");
    }

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator,
        McpServerManager mcpManager,
        string? repoPath,
        string? projectId = null,
        AnthropicClient? anthropicClient = null,
        string? anthropicModel = null)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
        _mcpManager = mcpManager;
        _repoPath = repoPath;
        _projectId = projectId;
        _anthropicClient = anthropicClient;
        _anthropicModel = anthropicModel;
        _useAnthropic = anthropicClient != null;

        if (mcpManager?.IsConnected == true && mcpManager.Tools != null)
            Console.WriteLine($"\nИнструменты MCP доступны: {mcpManager.Tools.Count} (серверов: {mcpManager.ServerCount})");
    }

    public ChatService(
        ILlmService llm,
        EnhancedRagPipeline rag,
        CitationValidator validator,
        string? projectId = null) : this(llm, rag: rag, validator: validator, mcpManager: null, repoPath: null, projectId: projectId)
    {
        _llm = llm;
        _rag = rag;
        _validator = validator;
    }

    public string? RepoPath => _repoPath;

    public async Task<CitationAnswer> ProcessMessageAsync(string userMessage, ChatSession session, bool useRag = true, CancellationToken ct = default)
    {
        var systemPrompt = PromptBuilder.SystemPrompt;

        var history = session.GetHistoryContext(6);
        if (!string.IsNullOrEmpty(history))
            systemPrompt += "\n\n[DIALOG HISTORY]\n" + history;

        CitationAnswer? parsedAnswer = null;
        var maxRetries = GetEnvInt("RAG_MAX_RETRIES", 3);

        bool hasMcpTools = _mcpManager?.IsConnected == true && _mcpManager.Tools != null;

        if (useRag)
        {
            var ragResult = await _rag!.ExecuteAsync(userMessage, RagPipelineMode.CitationEnforced, ct);

            if (ragResult.IsUnknown)
            {
                if (!hasMcpTools)
                {
                    parsedAnswer = await ProcessWithoutToolsAsync(userMessage, systemPrompt, null!, maxRetries, ct);
                }
                else
                {
                    parsedAnswer = _useAnthropic
                        ? await ProcessWithAnthropicToolCallingAsync(userMessage, systemPrompt, null, maxRetries, ct)
                        : await ProcessWithToolCallingAsync(userMessage, systemPrompt, null, maxRetries, ct);
                }

                goto end;
            }
            else
            {
                if (hasMcpTools)
                {
                    // Include RAG chunks as context but without "RAW JSON only" instructions
                    // that conflict with tool calling
                    var userPromptForTools = PromptBuilder.BuildUserPromptForTools(userMessage, ragResult.Chunks, ragResult.Confidence);
                    parsedAnswer = _useAnthropic
                        ? await ProcessWithAnthropicToolCallingAsync(userPromptForTools, systemPrompt, ragResult, maxRetries, ct)
                        : await ProcessWithToolCallingAsync(userPromptForTools, systemPrompt, ragResult, maxRetries, ct);
                }
                else
                {
                    var userPrompt = PromptBuilder.BuildUserPrompt(userMessage, ragResult.Chunks, ragResult.Confidence);
                    parsedAnswer = await ProcessWithoutToolsAsync(userPrompt, systemPrompt, ragResult, maxRetries, ct);
                }

                goto end;
            }
        }
        else
        {
            // Non-RAG mode: skip RAG pipeline, use MCP tools directly or direct LLM
            if (!hasMcpTools)
            {
                parsedAnswer = await ProcessWithoutToolsAsync(userMessage, systemPrompt, null!, maxRetries, ct);
            }
            else
            {
                parsedAnswer = _useAnthropic
                    ? await ProcessWithAnthropicToolCallingAsync(userMessage, systemPrompt, null, maxRetries, ct)
                    : await ProcessWithToolCallingAsync(userMessage, systemPrompt, null, maxRetries, ct);
            }

            goto end;
        }

        end:
        return parsedAnswer;
    }

    /// <summary>
    /// Processes a message using native MCP tool calling via chat template.
    /// Tools are embedded in the system prompt (not sent as a separate "tools" API parameter),
    /// which is how qwen3.6-35b-a3b expects tool definitions when using native MCP support.
    /// The model parses tools from the system message and returns tool_calls in its response,
    /// which we execute via MCP and feed back as "tool" role messages.
    /// </summary>
    private async Task<CitationAnswer> ProcessWithToolCallingAsync(
        string userPrompt, string systemPrompt, RagResult? ragResult, int maxRetries, CancellationToken ct)
    {
        var llmApiUrl = Environment.GetEnvironmentVariable("LLM_API_URL") ?? "http://192.168.1.15:1234";
        var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "qwen/qwen3.6-35b-a3b";
        var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(llmApiUrl),
            Timeout = TimeSpan.FromSeconds(1620)
        };

        if (!string.IsNullOrEmpty(llmApiKey))
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", llmApiKey);

        // Build system prompt with tools embedded for native MCP support via chat template.
        // The model receives tool definitions as part of the system message, enabling native tool calling.
        var effectiveSystemPrompt = systemPrompt;

        // Determine the correct base prompt based on whether RAG is being used
        if (ragResult == null)
        {
            // Check if this is a non-RAG mode (no _rag instance) vs RAG mode with unknown result
            // if (_rag == null)
            // {
                // Non-RAG mode: use MCP-only prompt (no RAG references)
                effectiveSystemPrompt = PromptBuilder.McpOnlySystemPrompt;
            // }
            // else
            // {
            //     // RAG mode with unknown result: use fallback prompt (mentions indexed project)
            //     effectiveSystemPrompt = PromptBuilder.FallbackSystemPrompt;
            // }
        }

        // Embed MCP tools into the system prompt for native chat template support
        var hasMcpTools = _mcpManager?.IsConnected == true && _mcpManager.Tools != null;
        if (hasMcpTools)
        {
            var basePrompt = ragResult == null
                ? PromptBuilder.McpOnlySystemPrompt
                : PromptBuilder.FallbackSystemPrompt;
            effectiveSystemPrompt = ToolPromptBuilder.BuildSystemPromptWithTools(basePrompt, _mcpManager.Tools);
        }

        // Inform LLM about the repository path so it can pass it in tool calls
        if (!string.IsNullOrEmpty(_repoPath))
        {
            effectiveSystemPrompt += $"\n\n[REPOSITORY PATH: {_repoPath}]";
        }

        // Inform LLM about the GitLab project ID
        if (!string.IsNullOrEmpty(_projectId))
        {
            effectiveSystemPrompt += $"\n\n[PROJECT ID: {_projectId}]";
        }

        var messages = new List<Dictionary<string, object>>();
        messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = effectiveSystemPrompt });

        var userMessageContent = ragResult != null ? BuildUserMessageWithChunks(userPrompt) : userPrompt;
        messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = userMessageContent });

        var currentRagResult = ragResult;
        CitationAnswer? lastParsedAnswer = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            // Native MCP format: no "tools" or "tool_choice" parameters.
            // Tools are already embedded in the system prompt via ToolPromptBuilder.
            var request = new Dictionary<string, object>
            {
                ["model"] = llmModel,
                ["messages"] = messages,
            };

            request["max_tokens"] = MaxTokens;
            request["stream"] = false;
            request["temperature"] = 0.2;
            request["top_p"] = 0.9;

            var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<QwenChatResponse>(ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (choice == null) continue;

            var assistantMsg = choice.Message;
            string? content = assistantMsg?.Content;
            List<QwenToolCall>? toolCalls = assistantMsg?.ToolCalls ?? [];

            if (!string.IsNullOrEmpty(content) && (toolCalls == null || toolCalls.Count == 0))
            {
                var rawResponse = content;

                // Check for XML-style tool call blocks in the response
                var xmlToolCalls = ExtractXmlToolCalls(rawResponse);

                if (xmlToolCalls != null && xmlToolCalls.Count > 0)
                {
                    Console.WriteLine($"\nНайдено {xmlToolCalls.Count} вызовов инструментов в XML формате...");

                    foreach (var xmlCall in xmlToolCalls)
                    {
                        var toolName = xmlCall["name"]?.ToString() ?? "";
                        var toolArgs = xmlCall.ContainsKey("arguments") ? JsonSerializer.Serialize(xmlCall["arguments"]) : "{}";

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n[XML Инструмент: {toolName}]");
                        if (!string.IsNullOrEmpty(toolArgs) && toolArgs != "{}")
                            Console.WriteLine($"Аргументы: {toolArgs}");
                        Console.ResetColor();

                        var toolCallId = $"xml_tool_{Guid.NewGuid().ToString("N")[..8]}";

                        Dictionary<string, object>? arguments = null;
                        try
                        {
                            arguments = McpToolMapper.ParseToolCallArguments(toolArgs);
                        }
                        catch (JsonException ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Ошибка парсинга аргументов для '{toolName}': {ex.Message}");
                            Console.ResetColor();

                            messages.Add(new Dictionary<string, object>
                            {
                                ["role"] = "assistant",
                                ["content"] = null,
                                ["function_call"] = new Dictionary<string, object>
                                {
                                    ["name"] = toolName,
                                    ["arguments"] = new Dictionary<string, object>()
                                }
                            });

                            messages.Add(new Dictionary<string, object>
                            {
                                ["role"] = "tool",
                                ["content"] = $"Ошибка парсинга аргументов: {ex.Message}",
                                ["tool_call_id"] = toolCallId
                            });

                            currentRagResult = null;
                            continue;
                        }

                        CallToolResult callResult;
                        try
                        {
                            callResult = await _mcpManager!.CallToolAsync(toolName, arguments);
                        }
                        catch (Exception ex)
                        {
                            var errorMsg = $"Ошибка вызова инструмента '{toolName}': {ex.Message}";
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Ошибка: {errorMsg}");
                            Console.ResetColor();

                            messages.Add(new Dictionary<string, object>
                            {
                                ["role"] = "assistant",
                                ["content"] = null,
                                ["function_call"] = new Dictionary<string, object>
                                {
                                    ["name"] = toolName,
                                    ["arguments"] = arguments ?? new Dictionary<string, object>()
                                }
                            });

                            messages.Add(new Dictionary<string, object>
                            {
                                ["role"] = "tool",
                                ["content"] = errorMsg,
                                ["tool_call_id"] = toolCallId
                            });

                            currentRagResult = null;
                            continue;
                        }

                        var formattedResult = McpToolMapper.FormatToolResult(callResult);
                        Console.ForegroundColor = ConsoleColor.Gray;
                        var preview = formattedResult.Length > 200 ? formattedResult[..200] + "..." : formattedResult;
                        Console.WriteLine($"Результат: {preview}");
                        Console.ResetColor();

                        messages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "assistant",
                            ["content"] = null,
                            ["function_call"] = new Dictionary<string, object>
                            {
                                ["name"] = toolName,
                                ["arguments"] = arguments ?? new Dictionary<string, object>()
                            }
                        });

                        messages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "tool",
                            ["content"] = formattedResult,
                            ["tool_call_id"] = toolCallId
                        });

                    }

                    currentRagResult = null;
                    continue;
                }
                else
                {
                    if (currentRagResult != null)
                    {
                        try
                        {
                            lastParsedAnswer = CitationAnswerParser.Parse(rawResponse, currentRagResult.Chunks);
                        }
                        catch (JsonException)
                        {
                            lastParsedAnswer = new CitationAnswer(
                                Answer: rawResponse.Trim(),
                                Confidence: ConfidenceLevel.High,
                                ClarificationRequest: null,
                                Sources: [],
                                Citations: []
                            );
                        }
                    }
                    else
                    {
                        lastParsedAnswer = new CitationAnswer(
                            Answer: rawResponse.Trim(),
                            Confidence: ConfidenceLevel.High,
                            ClarificationRequest: null,
                            Sources: [],
                            Citations: []
                        );
                    }

                    if (currentRagResult != null && lastParsedAnswer?.Confidence == ConfidenceLevel.Unknown)
                    {
                        if (attempt < maxRetries)
                        {
                            var feedback = "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]";
                            var lastUserMsg = messages.Last(m => (string)m["role"] == "user");
                            ((Dictionary<string, object>)lastUserMsg)["content"] += feedback;
                            continue;
                        }
                    }

                    if (currentRagResult != null && GetEnvBool("RAG_ENABLE_VALIDATION", true))
                    {
                        var validation = _validator.Validate(lastParsedAnswer, currentRagResult.Chunks);

                        if (validation.IsValid)
                            break;

                        if (attempt < maxRetries)
                        {
                            var feedback = $"\n\n[SYSTEM FEEDBACK: Previous response had validation errors: {string.Join("; ", validation.Errors)}. " +
                                $"Please fix and respond with valid JSON only.]";
                            var lastUserMsg2 = messages.Last(m => (string)m["role"] == "user");
                            ((Dictionary<string, object>)lastUserMsg2)["content"] += feedback;
                        }
                        else
                        {
                            lastParsedAnswer = CreateFallbackAnswer(currentRagResult);
                        }

                        currentRagResult = null;
                    }
                }

            }

            if (toolCalls != null && toolCalls.Count > 0)
            {
                Console.WriteLine($"\nLLM запросил(а) {toolCalls.Count} инструмент(ов)...");

                foreach (var toolCall in toolCalls)
                {
                    var arguments = McpToolMapper.ParseToolCallArguments(toolCall.Function?.Arguments ?? "{}");

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[Инструмент: {toolCall.Function?.Name}]");
                    if (arguments.Count > 0)
                        Console.WriteLine($"Аргументы: {JsonSerializer.Serialize(arguments)}");
                    Console.ResetColor();

                    var toolCallId = toolCall.Id ?? $"tool_{Guid.NewGuid().ToString("N")[..8]}";
                    CallToolResult callResult;
                    try
                    {
                        callResult = await _mcpManager!.CallToolAsync(toolCall.Function?.Name ?? "", arguments);
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Ошибка вызова инструмента '{toolCall.Function?.Name}': {ex.Message}";
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Ошибка: {errorMsg}");
                        Console.ResetColor();

                        messages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "tool",
                            ["content"] = errorMsg,
                            ["tool_call_id"] = toolCallId
                        });

                        currentRagResult = null;
                        continue;
                    }

                    var formattedResult = McpToolMapper.FormatToolResult(callResult);
                    Console.ForegroundColor = ConsoleColor.Gray;
                    var preview = formattedResult.Length > 200 ? formattedResult[..200] + "..." : formattedResult;
                    Console.WriteLine($"Результат: {preview}");
                    Console.ResetColor();

                    messages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool",
                        ["content"] = formattedResult,
                        ["tool_call_id"] = toolCallId
                    });

                }

            }

            currentRagResult = null;
        }

        lastParsedAnswer ??= (ragResult != null) 
            ? CreateFallbackAnswer(ragResult, "All retries failed.")
            : new CitationAnswer(
                Answer: "Unable to generate an answer. Please try again.",
                Confidence: ConfidenceLevel.Unknown,
                ClarificationRequest: "All retries failed. No context was available.",
                Sources: [],
                Citations: []
            );
        return lastParsedAnswer;
    }

    private async Task<CitationAnswer> ProcessWithAnthropicToolCallingAsync(
        string userPrompt, string systemPrompt, RagResult? ragResult, int maxRetries, CancellationToken ct)
    {
        const int maxToolRounds = 20;

        var anthropicTools = AnthropicToolMapper.ConvertToAnthropicTools(_mcpManager!.Tools!);

        var effectiveSystemPrompt = ragResult == null
            ? (_rag == null ? PromptBuilder.McpOnlySystemPrompt : PromptBuilder.FallbackSystemPrompt)
            : systemPrompt;

        if (!string.IsNullOrEmpty(_repoPath))
            effectiveSystemPrompt += $"\n\n[REPOSITORY PATH: {_repoPath}]";

        if (!string.IsNullOrEmpty(_projectId))
            effectiveSystemPrompt += $"\n\n[PROJECT ID: {_projectId}]";

        var messages = new List<MessageParam>();
        messages.Add(new MessageParam
        {
            Role = AnthropicRole.User,
            Content = ragResult != null ? BuildUserMessageWithChunks(userPrompt) : userPrompt
        });

        var currentRagResult = ragResult;
        CitationAnswer? lastParsedAnswer = null;

        for (int toolRound = 1; toolRound <= maxToolRounds; toolRound++)
        {
            Console.WriteLine($"\n[Anthropic] Раунд {toolRound}/{maxToolRounds}...");

            var parameters = new MessageCreateParams
            {
                Model = _anthropicModel!,
                MaxTokens = 4096,
                System = effectiveSystemPrompt,
                Messages = messages,
                Tools = anthropicTools,
            };

            Anthropic.Models.Messages.Message response;
            try
            {
                response = await _anthropicClient!.Messages.Create(parameters, ct);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Anthropic] Ошибка API: {ex.Message}");
                Console.ResetColor();
                break;
            }

            Console.WriteLine($"[Anthropic] stop_reason={response.StopReason}, content blocks={response.Content.Count}");

            var toolUseBlocks = response.Content
                .Select(block => block.Value)
                .OfType<ToolUseBlock>()
                .ToList();

            if (toolUseBlocks.Count > 0)
            {
                Console.WriteLine($"\nAnthropic запросил(а) {toolUseBlocks.Count} инструмент(ов)...");

                var assistantBlocks = new List<ContentBlockParam>();
                foreach (var block in response.Content)
                {
                    if (block.Value is ToolUseBlock tb)
                    {
                        assistantBlocks.Add(new ContentBlockParam(
                            new ToolUseBlockParam
                            {
                                ID = tb.ID,
                                Name = tb.Name,
                                Input = tb.Input,
                                Type = JsonSerializer.SerializeToElement("tool_use")
                            }));
                    }
                }

                messages.Add(new MessageParam
                {
                    Role = AnthropicRole.Assistant,
                    Content = assistantBlocks
                });

                foreach (var toolUse in toolUseBlocks)
                {
                    var input = toolUse.Input.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[Инструмент: {toolUse.Name}]");
                    if (input.Count > 0)
                        Console.WriteLine($"Аргументы: {JsonSerializer.Serialize(input)}");
                    Console.ResetColor();

                    CallToolResult callResult;
                    try
                    {
                        callResult = await _mcpManager.CallToolAsync(toolUse.Name, input);
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Ошибка вызова инструмента '{toolUse.Name}': {ex.Message}";
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Ошибка: {errorMsg}");
                        Console.ResetColor();

                        var errorResultBlock = new ContentBlockParam(
                            new ToolResultBlockParam(toolUse.ID)
                            {
                                Content = new ToolResultBlockParamContent(errorMsg),
                                IsError = true
                            });

                        messages.Add(new MessageParam
                        {
                            Role = AnthropicRole.User,
                            Content = new List<ContentBlockParam> { errorResultBlock }
                        });

                        continue;
                    }

                    var formattedResult = McpToolMapper.FormatToolResult(callResult);
                    Console.ForegroundColor = ConsoleColor.Gray;
                    var preview = formattedResult.Length > 200 ? formattedResult[..200] + "..." : formattedResult;
                    Console.WriteLine($"Результат: {preview}");
                    Console.ResetColor();

                    var resultBlock = new ContentBlockParam(
                        new ToolResultBlockParam(toolUse.ID)
                        {
                            Content = new ToolResultBlockParamContent(formattedResult),
                            IsError = false
                        });

                    messages.Add(new MessageParam
                    {
                        Role = AnthropicRole.User,
                        Content = new List<ContentBlockParam> { resultBlock }
                    });
                }

                continue;
            }

            var assistantText = string.Concat(response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(tb => tb.Text ?? string.Empty));

            Console.WriteLine($"[Anthropic] Текстовый ответ получен ({assistantText.Length} символов).");

            if (currentRagResult != null)
            {
                try
                {
                    lastParsedAnswer = CitationAnswerParser.Parse(assistantText, currentRagResult.Chunks);
                }
                catch (JsonException)
                {
                    lastParsedAnswer = new CitationAnswer(
                        Answer: assistantText.Trim(),
                        Confidence: ConfidenceLevel.High,
                        ClarificationRequest: null,
                        Sources: [],
                        Citations: []
                    );
                }
            }
            else
            {
                lastParsedAnswer = new CitationAnswer(
                    Answer: assistantText.Trim(),
                    Confidence: ConfidenceLevel.High,
                    ClarificationRequest: null,
                    Sources: [],
                    Citations: []
                );
            }

            break;
        }

        if (lastParsedAnswer != null && currentRagResult != null && lastParsedAnswer.Confidence == ConfidenceLevel.Unknown)
        {
            for (int retry = 1; retry <= maxRetries; retry++)
            {
                Console.WriteLine($"[Anthropic] Confidence=unknown, попытка корректировки {retry}/{maxRetries}...");

                var retryMessages = new List<MessageParam>(messages);
                retryMessages.Add(new MessageParam
                {
                    Role = AnthropicRole.User,
                    Content = "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]"
                });

                var retryParams = new MessageCreateParams
                {
                    Model = _anthropicModel!,
                    MaxTokens = 4096,
                    System = effectiveSystemPrompt,
                    Messages = retryMessages,
                    Tools = anthropicTools,
                };

                try
                {
                    var response = await _anthropicClient!.Messages.Create(retryParams, ct);
                    var text = string.Concat(response.Content
                        .Select(b => b.Value)
                        .OfType<TextBlock>()
                        .Select(tb => tb.Text ?? string.Empty));

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try
                        {
                            lastParsedAnswer = CitationAnswerParser.Parse(text, currentRagResult.Chunks);
                        }
                        catch (JsonException)
                        {
                            lastParsedAnswer = new CitationAnswer(
                                Answer: text.Trim(),
                                Confidence: ConfidenceLevel.High,
                                ClarificationRequest: null,
                                Sources: [],
                                Citations: []
                            );
                        }

                        if (lastParsedAnswer.Confidence != ConfidenceLevel.Unknown)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Anthropic] Ошибка API при корректировке: {ex.Message}");
                    Console.ResetColor();
                    break;
                }
            }
        }

        if (lastParsedAnswer != null && currentRagResult != null && GetEnvBool("RAG_ENABLE_VALIDATION", true))
        {
            var validation = _validator.Validate(lastParsedAnswer, currentRagResult.Chunks);
            if (!validation.IsValid)
            {
                Console.WriteLine($"[Anthropic] Валидация не пройдена: {string.Join("; ", validation.Errors)}");
                lastParsedAnswer = CreateFallbackAnswer(currentRagResult,
                    $"Validation failed: {string.Join("; ", validation.Errors)}");
            }
        }

        lastParsedAnswer ??= (ragResult != null)
            ? CreateFallbackAnswer(ragResult, "No text response received from Anthropic.")
            : new CitationAnswer(
                Answer: "Unable to generate an answer. Please try again.",
                Confidence: ConfidenceLevel.Unknown,
                ClarificationRequest: "No text response received from Anthropic.",
                Sources: [],
                Citations: []
            );
        return lastParsedAnswer;
    }

    private async Task<CitationAnswer> ProcessWithoutToolsAsync(
        string userPrompt, string systemPrompt, RagResult? ragResult, int maxRetries, CancellationToken ct)
    {
        CitationAnswer? parsedAnswer = null;
        var lastException = "";

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var effectiveSystemPrompt = ragResult == null
                    ? (_rag == null ? PromptBuilder.McpOnlySystemPrompt : PromptBuilder.FallbackSystemPrompt)
                    : systemPrompt;

                // Inform LLM about the repository path so it can pass it in tool calls
                if (!string.IsNullOrEmpty(_repoPath))
                {
                    effectiveSystemPrompt += $"\n\n[REPOSITORY PATH: {_repoPath}]";
                }

                // Inform LLM about the GitLab project ID
                if (!string.IsNullOrEmpty(_projectId))
                {
                    effectiveSystemPrompt += $"\n\n[PROJECT ID: {_projectId}]";
                }

                var rawResponse = await _llm.AskAsync(userPrompt, effectiveSystemPrompt, MaxTokens, ct);

                parsedAnswer = ragResult != null
                    ? CitationAnswerParser.Parse(rawResponse, ragResult.Chunks)
                    : new CitationAnswer(
                        Answer: rawResponse.Trim(),
                        Confidence: ConfidenceLevel.High,
                        ClarificationRequest: null,
                        Sources: [],
                        Citations: []
                    );

                if (ragResult != null && parsedAnswer.Confidence == ConfidenceLevel.Unknown)
                {
                    if (attempt < maxRetries)
                    {
                        userPrompt += "\n\n[SYSTEM: The context IS sufficient. Do NOT output confidence='unknown'. Provide a concrete answer with citations.]";
                        continue;
                    }
                }

                if (ragResult == null)
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
                else if (ragResult != null)
                    {
                        parsedAnswer = CreateFallbackAnswer(ragResult, $"JSON parse error: {ex.Message}");
                    }
                else
                {
                    parsedAnswer = new CitationAnswer(
                        Answer: "Unable to generate an answer. Please try again.",
                        Confidence: ConfidenceLevel.Unknown,
                        ClarificationRequest: $"JSON parse error after {maxRetries} retries.",
                        Sources: [],
                        Citations: []
                    );
                }

            }
        }

        if (parsedAnswer == null)
        {
            parsedAnswer = ragResult != null
                ? CreateFallbackAnswer(ragResult, $"All {maxRetries} retries failed. Last error: {lastException}")
                : new CitationAnswer(
                    Answer: "Unable to generate an answer. Please try again.",
                    Confidence: ConfidenceLevel.Unknown,
                    ClarificationRequest: $"All {maxRetries} retries failed. Last error: {lastException}",
                    Sources: [],
                    Citations: []
                );
        }

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
                var answer = await ProcessMessageAsync(question, session, useRag: isQueryCommand, ct);
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

    private static string BuildUserMessageWithChunks(string userPrompt) => userPrompt;

    private static CitationAnswer CreateFallbackAnswer(RagResult? ragResult, string? errorReason = null)
    {
        if (ragResult == null || ragResult.Chunks.Count == 0)
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

    private List<Dictionary<string, object>>? ExtractXmlToolCalls(string response)
    {
        var results = new List<Dictionary<string, object>>();

        // Step 1: try regex for properly closed tags: <tool_call>...</tool_call>
        var closedPattern = new Regex(@"<tool_call>\n?(?<json>[\s\S]*?)\n?</tool_call>", RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        var matches = closedPattern.Matches(response);
        foreach (Match match in matches)
        {
            var parsed = TryParseToolCallJson(match.Groups["json"].Value.Trim());
            if (parsed != null) results.Add(parsed);
        }

        // Step 2: if no results, try splitting by <tool_call> to handle unclosed tags
        if (results.Count == 0 && response.Contains("<tool_call>"))
        {
            var parts = response.Split("<tool_call>", StringSplitOptions.None);
            for (int i = 1; i < parts.Length; i++)
            {
                var segment = parts[i];
                var closeIdx = segment.IndexOf("</tool_call>");
                if (closeIdx >= 0)
                    segment = segment[..closeIdx];

                segment = segment.Trim();
                if (string.IsNullOrEmpty(segment)) continue;

                var parsed = TryParseToolCallJson(segment);
                if (parsed != null) results.Add(parsed);
            }
        }

        // Fallback: try parsing the entire response as a single JSON tool call
        if (results.Count == 0)
        {
            var parsed = TryParseToolCallJson(response.Trim());
            if (parsed != null) results.Add(parsed);
        }

        return results.Count > 0 ? results : null;
    }

    private static Dictionary<string, object>? TryParseToolCallJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        string jsonStr;
        try
        {
            using var _ = JsonDocument.Parse(rawJson);
            jsonStr = rawJson;
        }
        catch (JsonException)
        {
            // Repair step 1: fix unquoted values like "name":read_file -> "name":"read_file"
            var repaired = Regex.Replace(rawJson, @":\s*([a-zA-Z_][a-zA-Z0-9_\-\.\/]*)", @":""$1""");

            // Repair step 2: unescape LLM's backslash-quotes: \" -> "
            repaired = repaired.Replace("\\\"", "\"");

            try
            {
                using var _ = JsonDocument.Parse(repaired);
                jsonStr = repaired;
                goto parsed;
            }
            catch (JsonException) { }

            // Repair step 3: try unescape only (without unquoted-value fix)
            var unescaped = rawJson.Replace("\\\"", "\"");
            try
            {
                using var _ = JsonDocument.Parse(unescaped);
                jsonStr = unescaped;
                goto parsed;
            }
            catch (JsonException) { }

            jsonStr = NormalizePythonStyleToJson(rawJson);
            try
            {
                using var _ = JsonDocument.Parse(jsonStr);
            }
            catch (JsonException)
            {
                string trimmed = rawJson.TrimEnd();
                while (trimmed.Length > 1 && trimmed.EndsWith('}'))
                {
                    trimmed = trimmed[..^1].TrimEnd();
                    try
                    {
                        using var __ = JsonDocument.Parse(trimmed);
                        jsonStr = trimmed;
                        goto parsed;
                    }
                    catch (JsonException) { }
                }
                return null;
            }
        }

        parsed:
        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;

        if (!root.TryGetProperty("name", out var nameProp))
            return null;

        var callObj = new Dictionary<string, object>
        {
            ["name"] = nameProp.GetString() ?? "",
        };

        if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
        {
            callObj["arguments"] = McpToolMapper.ParseJsonElement(argsProp);
        }

        return callObj;
    }

    private static string NormalizePythonStyleToJson(string pythonStyle)
    {
        var sb = new StringBuilder();

        // Remove leading [ and trailing ] if present (Python list representation)
        var trimmed = pythonStyle.Trim();
        bool isList = trimmed.StartsWith('[');

        if (isList)
            sb.Append('{');
        else
            sb.Append(trimmed);

        var text = isList ? trimmed[1..^1].Trim() : sb.ToString();

        // Replace unquoted keys: name:value -> "name":value
        // Handle both quoted and unquoted values
        var result = new StringBuilder();
        bool inString = false;
        char escapeChar = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                result.Append(c);
                if (c == escapeChar) inString = false;
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inString = true;
                    escapeChar = c;

                    // If we're at a key position (after { or ,) and the next chars are unquoted letters/digits/underscore
                    if (i > 0)
                    {
                        char prev = text[i - 1];
                        if (prev == '{' || prev == ',')
                        {
                            // Check if this is an unquoted key: starts with letter/underscore, followed by letters/digits
                            int j = i;
                            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                                j++;

                            int keyLen = j - i;
                            if (keyLen > 0 && !char.IsDigit(text[i]))
                            {
                                // This is an unquoted key — replace quotes with double quotes and add colon if missing
                                string key = text.Substring(i, keyLen);

                                // Skip past the unquoted value (could be a string in quotes or an identifier)
                                int k = j;

                                // Skip whitespace after key
                                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;

                                // Check for colon
                                if (k < text.Length && text[k] == ':') k++;

                                // Skip whitespace after colon
                                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;

                                // Now skip the value
                                if (k < text.Length && (text[k] == '"' || text[k] == '\''))
                                {
                                    // Quoted string value — find closing quote
                                    char q = text[k];
                                    k++;
                                    while (k < text.Length && text[k] != q) k++;
                                    if (k < text.Length) k++; // closing quote
                                }
                                else if (k < text.Length && char.IsLetterOrDigit(text[k]) || text[k] == '_')
                                {
                                    // Unquoted identifier value (e.g., git_branch, all)
                                    while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                                }

                                // Replace: unquoted_key:value -> "key":"value" (wrap value in quotes if it was unquoted)
                                string rawValue = text.Substring(j, k - j).Trim();

                                if (k > 0 && (text[k - 1] == '"' || text[k - 1] == '\''))
                                {
                                    // Value was quoted — keep as-is but ensure double quotes
                                    result.Append($"\"{key}\":");
                                }
                                else if (rawValue == "null" || rawValue == "true" || rawValue == "false")
                                {
                                    result.Append($"\"{key}\":{rawValue}");
                                }
                                else
                                {
                                    result.Append($"\"{key}\":\"{rawValue}\"");
                                }

                                i = k - 1; // -1 because loop increments
                                continue;
                            }
                        }

                        result.Append(c);
                    }
                    else
                    {
                        result.Append(c);
                    }
                }
                else if (c == ':' && i > 0)
                {
                    // Check if there's a colon without quotes before it (unquoted key)
                    int j = i - 1;
                    while (j >= 0 && char.IsWhiteSpace(text[j])) j--;

                    if (j >= 0)
                    {
                        // Check backwards for unquoted key characters
                        int k = j;
                        bool isUnquotedKey = true;

                        while (k >= 0 && char.IsWhiteSpace(text[k])) k--;
                        if (!char.IsLetterOrDigit(text[k]) && text[k] != '_') isUnquotedKey = false;

                        if (isUnquotedKey)
                        {
                            // Find the start of this unquoted key
                            while (k > 0)
                            {
                                k--;
                                if (!char.IsLetterOrDigit(text[k]) && text[k] != '_') { k++; break; }
                            }

                            string key = text.Substring(k, j - k + 1);

                            // Skip whitespace after colon
                            int m = i + 1;
                            while (m < text.Length && char.IsWhiteSpace(text[m])) m++;

                            // Skip the value
                            if (m < text.Length && (text[m] == '"' || text[m] == '\''))
                            {
                                char q = text[m];
                                m++;
                                while (m < text.Length && text[m] != q) m++;
                                if (m < text.Length) m++; // closing quote

                                result.Append($"\"{key}\":");
                                i = m - 1; // will be incremented by loop
                                continue;
                            }
                            else if (m < text.Length && char.IsLetterOrDigit(text[m]) || text[m] == '_')
                            {
                                while (m < text.Length && (char.IsLetterOrDigit(text[m]) || text[m] == '_')) m++;

                                string rawValue = text.Substring(i + 1, m - i - 1).Trim();
                                if (rawValue == "null" || rawValue == "true" || rawValue == "false")
                                    result.Append($"\"{key}\":{rawValue}");
                                else
                                    result.Append($"\"{key}\":\"{rawValue}\"");

                                i = m - 1;
                                continue;
                            }
                        }

                        result.Append(c);
                    }
                }
                else
                {
                    result.Append(c);
                }
            }
        }

        string finalText = isList ? result.ToString() + '}' : result.ToString();
        return finalText;
    }

    private static object JsonValueToCSharp(JsonElement element) => McpToolMapper.JsonValueToCSharp(element);

    private class QwenChatResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("choices")]
        public List<QwenChoice>? Choices { get; set; }
    }

    private class QwenChoice
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public QwenMessage? Message { get; set; }
    }

    private class QwenMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
        public List<QwenToolCall>? ToolCalls { get; set; }
    }

    private class QwenToolCall
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("function")]
        public QwenFunction? Function { get; set; }
    }

    private class QwenFunction
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }
}
