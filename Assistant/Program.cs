using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string Usage = """
Assistant <repository-url> [target-directory] [--chat [--project-id <id>]] [--review --project-id <id> --merge-request-iid <iid>]

Clones a Git repository at startup, then indexes the cloned content.
With --chat flag: starts interactive RAG chat with a local LLM after indexing.
With --review flag: performs an automated code review of a GitLab merge request (one-shot, no chat).

Arguments:
  repository-url    URL of the Git repository (required)
  target-directory  Optional output directory (default: ./cloned-repo)

Flags:
  --chat                    Start interactive RAG chat mode after indexing
  --review                  Enable automated code review mode (one-shot)
  --project-id <value>      GitLab project ID (used with --chat or --review)
  --merge-request-iid <val> Merge request IID (required with --review)

Environment variables:
  GIT_PERSONAL_ACCESS_TOKEN    Personal Access Token for HTTPS URLs (optional, required when using https://)
  EMBEDDING_API_URL            Embedding API URL (default: http://localhost:1234)
  EMBEDDING_API_KEY            Embedding API key (optional)
  EMBEDDING_MODEL              Embedding model name (default: nomic-embed-text)
  LLM_API_URL                  LLM API URL (default: http://localhost:1234)
  LLM_API_KEY                  LLM API key (optional)
  LLM_MODEL                    LLM model name (default: qwen/qwen3.6-35b-a3b)
  GITLAB_PERSONAL_ACCESS_TOKEN Personal Access Token for GitLab MCP server (optional)
  GITLAB_API_URL               Base URL for GitLab API, e.g. https://gitlab.example.com/api/v4 (optional)

Examples:
  dotnet run -- git@gitserver.local.yurion.ru:andreyk/rust-design-patterns.git
  dotnet run -- https://gitserver.local.yurion.ru/andreyk/rust-design-patterns.git ./output-dir
  dotnet run -- https://gitserver.local.yurion.ru/andreyk/rust-design-patterns.git ./output-dir --chat
  dotnet run -- https://gitserver.local.yurion.ru/andreyk/rust-design-patterns.git ./output-dir --chat LLM_API_URL=http://localhost:11434
  dotnet run -- https://gitserver.local.yurion.ru/andreyk/rust-design-patterns.git ./output-dir --review --project-id 42 --merge-request-iid 123
""";

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    Environment.ExitCode = 1;
    return;
}

var repoUrl = args[0];
string targetDir = "./cloned-repo";
bool runChat = false;
bool runReview = false;
string? projectId = null;
string? mergeRequestIid = null;

for (int i = 1; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("--chat", StringComparison.OrdinalIgnoreCase))
    {
        runChat = true;
    }
    else if (arg.Equals("--review", StringComparison.OrdinalIgnoreCase))
    {
        runReview = true;
    }
    else if (arg.Equals("--project-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        projectId = args[++i];
    }
    else if (arg.Equals("--merge-request-iid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        mergeRequestIid = args[++i];
    }
    else if (!targetDir.Equals("./cloned-repo", StringComparison.Ordinal) || i > 1)
    {
        // First non-flag arg is target directory
        if (targetDir.Equals("./cloned-repo", StringComparison.Ordinal))
            targetDir = arg;
    }
}

// Validate --review mode arguments
if (runReview)
{
    if (string.IsNullOrWhiteSpace(projectId))
    {
        Console.Error.WriteLine("Error: --project-id is required when using --review.");
        Environment.ExitCode = 1;
        return;
    }

    if (string.IsNullOrWhiteSpace(mergeRequestIid))
    {
        Console.Error.WriteLine("Error: --merge-request-iid is required when using --review.");
        Environment.ExitCode = 1;
        return;
    }

    // In review mode, skip cloning and go straight to review
    var reviewTargetDir = Path.GetFullPath(targetDir);
    await RunReviewAsync(targetDir, reviewTargetDir, projectId!, mergeRequestIid!);
    return;
}

if (runChat && !string.IsNullOrWhiteSpace(projectId))
{
    Console.WriteLine($"Project ID: {projectId}");
}

var isHttps = repoUrl.StartsWith("https://", StringComparison.Ordinal);
string? token = null;

if (isHttps)
{
    token = Environment.GetEnvironmentVariable("GIT_PERSONAL_ACCESS_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("Error: GIT_PERSONAL_ACCESS_TOKEN environment variable is not set (required for HTTPS URLs).");
        Environment.ExitCode = 1;
        return;
    }
}

Console.WriteLine($"Repository URL: {repoUrl}");
var resolvedTargetDir = Path.GetFullPath(targetDir);
Console.WriteLine($"Target directory: {resolvedTargetDir}");

bool isCloned = Directory.Exists(resolvedTargetDir) && Directory.Exists(Path.Combine(resolvedTargetDir, ".git"));

if (isCloned)
{
    Console.WriteLine($"Repository already cloned to: {targetDir}");

    // === ИНДЕКСАЦИЯ (пропускаем клонирование) ===
    await RunIndexingAsync(targetDir, runChat, resolvedTargetDir, projectId);
}
else
{
    var normalizedUrl = NormalizeGitUrl(repoUrl, token);
    Console.WriteLine($"Normalized URL: {normalizedUrl}");

    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = $"clone \"{normalizedUrl}\" \"{targetDir}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");

    var outputBuilder = new StringBuilder();
    var errorBuilder = new StringBuilder();

    process.OutputDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            outputBuilder.AppendLine(e.Data);
    };

    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            errorBuilder.AppendLine(e.Data);
    };

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    if (!process.WaitForExit(300_000))
    {
        Console.Error.WriteLine("Error: Git clone timed out after 5 minutes.");
        process.Kill();
        Environment.ExitCode = 1;
        return;
    }

    string output = outputBuilder.ToString().TrimEnd();
    string error = errorBuilder.ToString().TrimEnd();

    if (process.ExitCode == 0)
    {
        Console.WriteLine($"Successfully cloned repository to: {Path.GetFullPath(targetDir)}");

        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output);

        // === ИНДЕКСАЦИЯ (после успешного git clone) ===
        await RunIndexingAsync(targetDir, runChat, resolvedTargetDir, projectId);
    }
    else
    {
        Console.Error.WriteLine($"Error: Git clone failed with exit code {process.ExitCode}.");

        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine(error);

        Environment.ExitCode = 1;
    }
}

static async Task RunIndexingAsync(string targetDir, bool runChat, string resolvedTargetDir, string? projectId)
{
    var dbPath = Path.Combine(Path.GetFullPath(targetDir), "document_index.db");

    if (File.Exists(dbPath))
    {
        Console.WriteLine($"\nИндекс уже существует: {dbPath}");

        if (runChat)
        {
            Console.WriteLine("\n=== Запуск интерактивного чата ===");
            await RunChatAsync(targetDir, dbPath, resolvedTargetDir, projectId);
        }

        return;
    }

    Console.WriteLine("\n=== Запуск индексации ===");
    var store = new SqliteVectorStore(dbPath);
    await store.InitializeAsync();

    // OpenAI-совместимый embedding service (работает с Ollama, vLLM, LM Studio и др.)
    var embeddingApiUrl = Environment.GetEnvironmentVariable("EMBEDDING_API_URL") ?? "http://192.168.1.15:1234";
    var embeddingModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-nomic-embed-text-v1.5";

    var embeddingService = new OpenAiCompatibleEmbeddingService(embeddingApiUrl, embeddingModel);

    // Проверка доступности API
    Console.WriteLine($"Проверка embedding API ({embeddingApiUrl})...");
    try 
    {
        var available = await embeddingService.CheckAvailabilityAsync();
        if (!available)
        {
            Console.WriteLine($"Embedding API недоступен на {embeddingApiUrl}. Индексация пропущена.");
            Console.WriteLine("Убедитесь, что API запущен и модель nomic-embed-text доступна.");
            return;
        }
    }
    catch (Exception ex) 
    {
        Console.WriteLine($"Embedding API недоступен: {ex.Message}");
        Console.WriteLine("Индексация пропущена. Убедитесь, что API запущен на {0}", embeddingApiUrl);
        return;
    }

    var extensions = new[] { ".txt", ".md", ".cs", ".json", ".xml", ".yaml", ".yml", ".html", ".js", ".py" };

    // Индексация: FixedSize
    Console.WriteLine("\nИндексация: Fixed Size Strategy (chunk=512, overlap=50)");
    var progress1 = new Progress<double>(p => Console.Write($"\r  Прогресс: {p:P0}"));
    var pipeline1 = new IndexingPipeline(new FixedSizeChunkingStrategy(512, 50), embeddingService, store);
    var result1 = await pipeline1.RunAsync(targetDir, extensions, progress1);
    Console.WriteLine($"\nГотово: {result1.ChunksCreated} чанков из {result1.FilesProcessed} файлов за {result1.Duration.TotalSeconds:F1}с");

    // Индексация: Structural
    Console.WriteLine("\nИндексация: Structural Strategy");
    var progress2 = new Progress<double>(p => Console.Write($"\r  Прогресс: {p:P0}"));
    var pipeline2 = new IndexingPipeline(new StructuralChunkingStrategy(512, 50), embeddingService, store);
    var result2 = await pipeline2.RunAsync(targetDir, extensions, progress2);
    Console.WriteLine($"\nГотово: {result2.ChunksCreated} чанков из {result2.FilesProcessed} файлов за {result2.Duration.TotalSeconds:F1}с");

    Console.WriteLine($"\nИндексация завершена. База: {dbPath}");
    Console.WriteLine($"  FixedSize чанков: {result1.ChunksCreated}");
    Console.WriteLine($"  Structural чанков: {result2.ChunksCreated}");

    embeddingService.Dispose();

    if (runChat)
    {
        Console.WriteLine("\n=== Запуск интерактивного чата ===");
        await RunChatAsync(targetDir, dbPath, resolvedTargetDir, projectId);
    }
}

static async Task RunChatAsync(string targetDir, string dbPath, string resolvedTargetDir, string? projectId)
{
    var store = new SqliteVectorStore(dbPath);
    await store.InitializeAsync();

    var embeddingApiUrl = Environment.GetEnvironmentVariable("EMBEDDING_API_URL") ?? "http://192.168.1.15:1234";
    var embeddingModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-nomic-embed-text-v1.5";
    var embeddingService = new OpenAiCompatibleEmbeddingService(embeddingApiUrl, embeddingModel);

    var llmApiUrl = Environment.GetEnvironmentVariable("LLM_API_URL") ?? "http://192.168.1.15:1234";
    var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "qwen/qwen3.6-35b-a3b";
    var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");

    Console.WriteLine($"Проверка LLM API ({llmApiUrl})...");
    ILlmService llm;
    try 
    {
        llm = new OpenAiCompatibleLlmService(llmApiUrl, llmModel, llmApiKey, 4096);
        // Quick connectivity check by making a minimal request
        var testPrompt = "Respond with exactly: OK";
        await llm.AskAsync(testPrompt, "You are a test assistant. Respond with exactly 'OK' and nothing else.", 10);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ LLM API доступен\n");
        Console.ResetColor();
    }
    catch (Exception ex) 
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ LLM API недоступен: {ex.Message}");
        Console.ResetColor();
        Console.WriteLine("Убедитесь, что LLM API запущен на {0}", llmApiUrl);
        Console.WriteLine("Запустите без --chat для работы только с индексацией.");
        return;
    }

    var extensions = new[] { ".txt", ".md", ".cs", ".json", ".xml", ".yaml", ".yml", ".html", ".js", ".py" };

    var rewrite = new HeuristicQueryRewriteService();
    var rag = new EnhancedRagPipeline(embeddingService, store, rewrite);
    var validator = new CitationValidator();

    McpServerManager? mcpManager = null;
    try
    {
        mcpManager = new McpServerManager();

        // Git MCP server (uvx)
        mcpManager.AddServer("git", "uvx", new[] { "mcp-server-git" });

        // GitLab MCP server (direct executable)
        var gitlabToken = Environment.GetEnvironmentVariable("GITLAB_PERSONAL_ACCESS_TOKEN");
        var gitlabApiUrl  = Environment.GetEnvironmentVariable("GITLAB_API_URL");

        if (!string.IsNullOrEmpty(gitlabToken) && !string.IsNullOrEmpty(gitlabApiUrl))
        {
            mcpManager.AddServer("zereight-mcp-gitlab", "zereight-mcp-gitlab",
                new[] { "--token=" + gitlabToken, "--api-url=" + gitlabApiUrl });
        }

        await mcpManager.ConnectAsync();
    }
    catch { /* MCP connection is optional, continue without it */ }

    ChatService chat;
    if (mcpManager != null && mcpManager.IsConnected)
    {
        chat = new ChatService(llm, rag, validator, mcpManager, resolvedTargetDir, projectId);
    }
    else if (rag != null)
    {
        chat = new ChatService(llm, rag, validator, projectId: projectId);
    }
    else
    {
        chat = new ChatService(llm, mcpManager, projectId: projectId);
    }

    try { await chat.RunInteractiveAsync(); }
    finally
    {
        if (mcpManager != null)
            await mcpManager.DisposeAsync();
        llm.Dispose();
        embeddingService.Dispose();
    }
}

static async Task RunReviewAsync(string targetDir, string resolvedTargetDir, string projectId, string mergeRequestIid)
{
    Console.WriteLine($"\n=== CODE REVIEW MODE ===");
    Console.WriteLine($"Project ID: {projectId}");
    Console.WriteLine($"Merge Request IID: {mergeRequestIid}\n");

    // Step 1: Check if index exists
    var dbPath = Path.Combine(Path.GetFullPath(targetDir), "document_index.db");

    // Step 2: Check LLM API availability
    var llmApiUrl = Environment.GetEnvironmentVariable("LLM_API_URL") ?? "http://192.168.1.15:1234";
    var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "qwen/qwen3.6-35b-a3b";
    var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");

    Console.WriteLine($"Проверка LLM API ({llmApiUrl})...");
    ILlmService llm;
    try 
    {
        llm = new OpenAiCompatibleLlmService(llmApiUrl, llmModel, llmApiKey, 4096);
        var testPrompt = "Respond with exactly: OK";
        await llm.AskAsync(testPrompt, "You are a test assistant. Respond with exactly 'OK' and nothing else.", 10);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ LLM API доступен\n");
        Console.ResetColor();
    }
    catch (Exception ex) 
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ LLM API недоступен: {ex.Message}");
        Console.ResetColor();
        Console.WriteLine("Убедитесь, что LLM API запущен на {0}", llmApiUrl);
        return;
    }

    // Step 3: Connect MCP servers (GitLab is required for get_merge_request_diffs)
    McpServerManager? mcpManager = null;
    try
    {
        Console.WriteLine("\nПодключение к MCP серверам...");
        mcpManager = new McpServerManager();

        // GitLab MCP server (required for review)
        var gitlabToken = Environment.GetEnvironmentVariable("GITLAB_PERSONAL_ACCESS_TOKEN");
        var gitlabApiUrl  = Environment.GetEnvironmentVariable("GITLAB_API_URL");

        if (!string.IsNullOrEmpty(gitlabToken) && !string.IsNullOrEmpty(gitlabApiUrl))
        {
            mcpManager.AddServer("zereight-mcp-gitlab", "zereight-mcp-gitlab",
                new[] { "--token=" + gitlabToken, "--api-url=" + gitlabApiUrl });
        }

        await mcpManager.ConnectAsync();
    }
    catch (Exception ex) 
    {
        Console.WriteLine($"Предупреждение: не удалось подключить MCP серверы: {ex.Message}");
        Console.WriteLine("Рецензирование будет выполнено без доступа к GitLab API.\n");
    }

    // Step 4: Get merge request diff via MCP (with LLM tool-calling as fallback)
    string? diffContent = null;

    if (mcpManager != null && mcpManager.IsConnected)
    {
        try
        {
            Console.WriteLine("Получение diff мердж-реквеста...");

            // Try LLM tool-calling first (LLM decides to call the tool)
            var llmResult = await InvokeReviewToolCallViaLlmAsync(
                llmApiUrl, llmModel, llmApiKey, mcpManager, projectId, mergeRequestIid);

            // If LLM returned a tool call (null result), execute via MCP directly
            if (llmResult == null)
            {
                diffContent = await CallGetMergeRequestDiffViaMcpAsync(mcpManager, projectId, mergeRequestIid);
                Console.WriteLine($"Получен diff мердж-реквеста: {diffContent.Length} символов\n");
            }

            // If LLM didn't provide a tool call, try direct MCP call as fallback
            if (string.IsNullOrEmpty(diffContent))
            {
                diffContent = await CallGetMergeRequestDiffViaMcpAsync(mcpManager, projectId, mergeRequestIid);
                Console.WriteLine($"Получен diff мердж-реквеста: {diffContent.Length} символов\n");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: не удалось получить diff через MCP: {ex.Message}");
            Console.WriteLine("Рецензирование будет выполнено без diff.\n");
        }
    }

    // Fallback: try direct MCP call even if LLM tool-calling failed
    if (string.IsNullOrEmpty(diffContent) && mcpManager != null && mcpManager.IsConnected)
    {
        try
        {
            diffContent = await CallGetMergeRequestDiffViaMcpAsync(mcpManager, projectId, mergeRequestIid);
            Console.WriteLine($"Получен diff мердж-реквеста: {diffContent.Length} символов\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: не удалось получить diff через MCP: {ex.Message}");
            Console.WriteLine("Рецензирование будет выполнено без diff.\n");
        }
    }

    // If MCP is not connected, try direct HTTP call to GitLab API as last resort
    if (string.IsNullOrEmpty(diffContent))
    {
        var gitlabToken = Environment.GetEnvironmentVariable("GITLAB_PERSONAL_ACCESS_TOKEN");
        var gitlabApiUrl  = Environment.GetEnvironmentVariable("GITLAB_API_URL");

        if (!string.IsNullOrEmpty(gitlabToken) && !string.IsNullOrEmpty(gitlabApiUrl))
        {
            try
            {
                Console.WriteLine("Попытка прямого вызова GitLab API...");

                using var httpClient = new HttpClient { BaseAddress = new Uri(gitlabApiUrl), Timeout = TimeSpan.FromSeconds(120) };
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gitlabToken);
                httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabToken);

                var uri = $"/projects/{Uri.EscapeDataString(projectId)}/merge_requests/{Uri.EscapeDataString(mergeRequestIid)}/diffs";
                var response = await httpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                diffContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Получен diff через GitLab API: {diffContent.Length} символов\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Предупреждение: не удалось получить diff через GitLab API: {ex.Message}");
                Console.WriteLine("Рецензирование будет выполнено без diff.\n");
            }
        }
    }

    // Fallback: try direct MCP call even if LLM tool-calling failed
    if (string.IsNullOrEmpty(diffContent) && mcpManager != null && mcpManager.IsConnected)
    {
        try
        {
            diffContent = await CallGetMergeRequestDiffViaMcpAsync(mcpManager, projectId, mergeRequestIid);
            Console.WriteLine($"Получен diff мердж-реквеста: {diffContent.Length} символов\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: не удалось получить diff через MCP: {ex.Message}");
            Console.WriteLine("Рецензирование будет выполнено без diff.\n");
        }
    }

    // If MCP is not connected, try direct HTTP call to GitLab API as last resort
    if (string.IsNullOrEmpty(diffContent))
    {
        var gitlabToken = Environment.GetEnvironmentVariable("GITLAB_PERSONAL_ACCESS_TOKEN");
        var gitlabApiUrl  = Environment.GetEnvironmentVariable("GITLAB_API_URL");

        if (!string.IsNullOrEmpty(gitlabToken) && !string.IsNullOrEmpty(gitlabApiUrl))
        {
            try
            {
                Console.WriteLine("Попытка прямого вызова GitLab API...");

                using var httpClient = new HttpClient { BaseAddress = new Uri(gitlabApiUrl), Timeout = TimeSpan.FromSeconds(120) };
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gitlabToken);
                httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabToken);

                var uri = $"/projects/{Uri.EscapeDataString(projectId)}/merge_requests/{Uri.EscapeDataString(mergeRequestIid)}/diffs";
                var response = await httpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                diffContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Получен diff через GitLab API: {diffContent.Length} символов\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Предупреждение: не удалось получить diff через GitLab API: {ex.Message}");
                Console.WriteLine("Рецензирование будет выполнено без diff.\n");
            }
        }
    }

    // Step 5: RAG context retrieval (if index exists)
    List<string> ragChunks = [];

    if (File.Exists(dbPath))
    {
        try
        {
            Console.WriteLine("RAG-поиск контекста проекта...");

            var embeddingApiUrl = Environment.GetEnvironmentVariable("EMBEDDING_API_URL") ?? "http://192.168.1.15:1234";
            var embeddingModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-nomic-embed-text-v1.5";
            var embeddingService = new OpenAiCompatibleEmbeddingService(embeddingApiUrl, embeddingModel);

            var rewrite = new HeuristicQueryRewriteService();
            var ragPipeline = new EnhancedRagPipeline(embeddingService, new SqliteVectorStore(dbPath), rewrite);

            var ragResult = await ragPipeline.ExecuteAsync(
                "code review best practices, architecture patterns, common bugs in this project", 
                RagPipelineMode.FullPipeline);

            foreach (var chunk in ragResult.Chunks.Take(5))
            {
                var content = chunk.Chunk.Content;
                if (!string.IsNullOrWhiteSpace(content))
                    ragChunks.Add($"[{chunk.Chunk.Source}] {content}");
            }

            embeddingService.Dispose();

            if (ragChunks.Count > 0)
                Console.WriteLine($"RAG: найдено {ragChunks.Count} релевантных чанков\n");
            else
                Console.WriteLine("RAG: контекст не найден в индексе.\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: RAG-поиск не выполнен: {ex.Message}\n");
        }
    }
    else
    {
        Console.WriteLine("Индекс не найден. RAG-контекст будет пропущен.\n");
    }

    // Step 6: Build final review prompt and get LLM answer
    Console.WriteLine("Формирование финального промпта для ревью...\n");

    var diffSection = !string.IsNullOrEmpty(diffContent)
        ? $"=== MERGE REQUEST DIFF ===\n{diffContent}\n"
        : "=== MERGE REQUEST DIFF ===\n(diff not available — MCP server was not connected)\n";

    var ragSection = ragChunks.Count > 0
        ? $"=== PROJECT CONTEXT (from RAG) ===\n{string.Join("\n\n---\n", ragChunks)}\n"
        : "";

    var finalUserPrompt = $"{diffSection}\n{ragSection}Please provide a code review comment covering:\n1. Potential bugs and edge cases\n2. Architecture concerns\n3. Recommendations for improvement";

    var systemPrompt = "You are an expert code reviewer analyzing a GitLab merge request. Analyze the provided diff and project context to identify potential bugs, architecture issues, security concerns, performance problems, and provide actionable recommendations. If the diff is not available, note that in your review.";

    Console.WriteLine("=== CODE REVIEW: MR #" + mergeRequestIid + " ===\n");

    string? cleaned = null;
    try
    {
        var reviewAnswer = await llm.AskAsync(finalUserPrompt, systemPrompt, 20000);
        cleaned = CleanReviewResponse(reviewAnswer);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== CODE REVIEW: MR #" + mergeRequestIid + " ===");
        Console.ResetColor();

        Console.WriteLine($"\n{cleaned}");

        if (ragChunks.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n--- RAG Sources used ---");
            foreach (var chunk in ragChunks.Take(3))
            {
                var source = chunk.Split(']').Length > 1 ? chunk.Substring(0, chunk.IndexOf(']') + 1) : "unknown";
                Console.WriteLine($"  {source}");
            }
            Console.ResetColor();
        }

        if (!string.IsNullOrEmpty(diffContent))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n--- Diff source: GitLab MCP (get_merge_request_diffs) ---");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Ошибка ревью: {ex.Message}");
        Console.ResetColor();
    }

    // Step 7: Post review comment to GitLab MR via MCP tool (create_merge_request_note)
    if (!string.IsNullOrEmpty(cleaned) && mcpManager != null && mcpManager.IsConnected)
    {
        try
        {
            Console.WriteLine("\n=== Публикация комментария ревью в GitLab MR ===");

            var posted = await PostReviewCommentViaMcpAsync(
                llmApiUrl, llmModel, llmApiKey, 
                mcpManager, projectId, mergeRequestIid, cleaned);

            if (posted)
                Console.WriteLine("Комментарий ревью успешно опубликован в GitLab MR.");
            else
                Console.WriteLine("Не удалось опубликовать комментарий — LLM не вернул корректный вызов инструмента.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Предупреждение: не удалось опубликовать комментарий в GitLab: {ex.Message}");
            Console.ResetColor();
        }
    }

    llm.Dispose();

    if (mcpManager != null)
        await mcpManager.DisposeAsync();
}

static async Task<string?> InvokeReviewToolCallViaLlmAsync(
    string llmApiUrl, string llmModel, string? llmApiKey, 
    McpServerManager mcpManager,
    string projectId, string mergeRequestIid)
{
    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(llmApiUrl),
        Timeout = TimeSpan.FromSeconds(120)
    };

    if (!string.IsNullOrEmpty(llmApiKey))
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", llmApiKey);

    var systemPrompt = ToolPromptBuilder.BuildSystemPromptWithTools("", mcpManager.Tools ?? []);

    var userMessage = $"Please get the merge request diffs for project_id='{projectId}' and merge_request_iid='{mergeRequestIid}'. Call the tool 'get_merge_request_diffs' with these parameters.";

    var messages = new List<Dictionary<string, object>>();
    messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt });
    messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = userMessage });

    const int maxRetries = 3;
    
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = llmModel,
            ["messages"] = messages,
        };

        request["max_tokens"] = 2048;
        request["stream"] = false;
        request["temperature"] = 0.1;

        var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<QwenChatResponse>();
        var choice = result?.Choices?.FirstOrDefault();

        if (choice == null) continue;

        var assistantMsg = choice.Message;
        string? content = assistantMsg?.Content;
        List<QwenToolCall>? toolCalls = assistantMsg?.ToolCalls ?? [];

        // Try native OpenAI-style tool_calls
        if (toolCalls != null && toolCalls.Count > 0)
        {
            foreach (var tc in toolCalls)
            {
                var funcName = tc.Function?.Name ?? "";
                if (funcName is "get_merge_request_diffs" or "getMergeRequestDiffs")
                {
                    Console.WriteLine($"LLM вернул инструмент: {funcName}");
                    return null; // signals: "execute get_merge_request_diffs via MCP manager"
                }

                Console.WriteLine($"LLM вернул инструмент: {funcName}");
            }
        }

        // Try XML/backtick-style tool call blocks in response (qwen3.6 format)
        if (!string.IsNullOrEmpty(content))
        {
            var xmlToolCalls = ExtractXmlToolCallsForReview(content);

            if (xmlToolCalls != null && xmlToolCalls.Count > 0)
            {
                foreach (var call in xmlToolCalls)
                {
                    var toolName = call["name"]?.ToString() ?? "";
                    if (toolName is "get_merge_request_diffs" or "getMergeRequestDiffs")
                    {
                        Console.WriteLine($"Найден XML инструмент: {toolName}");
                        return null; // signals: "execute get_merge_request_diffs via MCP manager"
                    }

                    Console.WriteLine($"LLM вернул XML инструмент: {toolName}");
                }
            }

            // Fallback: try parsing entire response as JSON tool call (no wrapping)
            var trimmed = content.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("name", out var nameProp) && doc.RootElement.TryGetProperty("arguments", out var argsProp))
                    {
                        if (nameProp.GetString() is "get_merge_request_diffs" or "getMergeRequestDiffs")
                        {
                            Console.WriteLine("Найден JSON инструмент (без обёртки).");
                            return null; // signals: "execute get_merge_request_diffs via MCP manager"
                        }

                        Console.WriteLine($"Найден JSON инструмент: {nameProp.GetString()}");
                    }
                }
                catch (JsonException) { /* not valid JSON */ }

                // Also try Python-style: [name:"...",arguments:{...}] without wrapping
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        var jsonStr = NormalizePythonStyleToJson(trimmed);
                        using var doc = JsonDocument.Parse(jsonStr);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp2) && doc.RootElement.TryGetProperty("arguments", out var argsProp2))
                        {
                            if (nameProp2.GetString() is "get_merge_request_diffs" or "getMergeRequestDiffs")
                            {
                                Console.WriteLine("Найден Python-style инструмент (без обёртки).");
                                return null; // signals: "execute get_merge_request_diffs via MCP manager"
                            }

                            Console.WriteLine($"Найден Python-style инструмент: {nameProp2.GetString()}");
                        }
                    }
                    catch (JsonException) { /* not valid Python-style object, ignore */ }
                }
            }
        }

        // If we got content but no tool call, add it back for retry (LLM may need to try again)
        if (!string.IsNullOrEmpty(content))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = content,
            });
        }
    }

    throw new InvalidOperationException("LLM did not return a tool call for get_merge_request_diffs after retries.");
}

static List<Dictionary<string, object>>? ExtractXmlToolCallsForReview(string response)
{
    var results = new List<Dictionary<string, object>>();

    var patterns = new[]
    {
        new Regex(@"<tool_call>\n?(?<json>[\s\S]*?)\n?</tool_call>", RegexOptions.Compiled, TimeSpan.FromSeconds(5))
    };

    foreach (var pattern in patterns)
    {
        var matches = pattern.Matches(response);
        foreach (Match match in matches)
        {
            var rawJson = match.Groups["json"].Value.Trim();

            string jsonStr;
            try
            {
                using var _ = JsonDocument.Parse(rawJson);
                jsonStr = rawJson;
            }
            catch (JsonException)
            {
                jsonStr = NormalizePythonStyleToJson(rawJson);

                try
                {
                    using var _ = JsonDocument.Parse(jsonStr);
                }
                    catch (JsonException)
                    {
                        string repaired = rawJson.TrimEnd();
                        bool fixed_ = false;
                        while (repaired.Length > 1 && repaired.EndsWith('}'))
                        {
                            repaired = repaired[..^1].TrimEnd();
                            try
                            {
                                using var __ = JsonDocument.Parse(repaired);
                                jsonStr = repaired;
                                fixed_ = true;
                                break;
                            }
                            catch (JsonException) { }
                        }

                        if (!fixed_)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Ошибка парсинга tool call блока: {rawJson}");
                            Console.ResetColor();
                            continue;
                        }
                    }
            }

            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var nameProp))
            {
                var callObj = new Dictionary<string, object>
                {
                    ["name"] = nameProp.GetString() ?? "",
                };

                if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                {
                    callObj["arguments"] = McpToolMapper.ParseJsonElement(argsProp);
                }

                results.Add(callObj);
            }
        }

        if (results.Count > 0) break;
    }

    // Fallback: try parsing the entire response as a JSON tool call (no XML/backtick wrapping)
    if (results.Count == 0)
    {
        string trimmed = response.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameProp) && root.TryGetProperty("function", out var funcProp))
                {
                    var callObj = new Dictionary<string, object>
                    {
                        ["name"] = nameProp.GetString() ?? "",
                        ["function"] = funcProp.GetString() ?? ""
                    };

                    if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                    {
                        callObj["arguments"] = McpToolMapper.ParseJsonElement(argsProp);
                    }

                    results.Add(callObj);
                }
            }
            catch (JsonException) { /* not a valid JSON object, ignore */ }
        }

        // Also try Python-style: [name:"...",function:"..."] without wrapping
        if (results.Count == 0 && trimmed.StartsWith('['))
        {
            try
            {
                string jsonStr = NormalizePythonStyleToJson(trimmed);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameProp2) && root.TryGetProperty("function", out var funcProp2))
                {
                    var callObj = new Dictionary<string, object>
                    {
                        ["name"] = nameProp2.GetString() ?? "",
                        ["function"] = funcProp2.GetString() ?? ""
                    };

                    if (root.TryGetProperty("arguments", out var argsProp2) && argsProp2.ValueKind == JsonValueKind.Object)
                    {
                        callObj["arguments"] = McpToolMapper.ParseJsonElement(argsProp2);
                    }

                    results.Add(callObj);
                }
            }
            catch (JsonException) { /* not a valid Python-style object, ignore */ }
        }
    }

    return results.Count > 0 ? results : null;
}

static async Task<bool> PostReviewCommentViaMcpAsync(
    string llmApiUrl, string llmModel, string? llmApiKey,
    McpServerManager mcpManager, 
    string projectId, string mergeRequestIid,
    string reviewComment)
{
    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(llmApiUrl),
        Timeout = TimeSpan.FromSeconds(120)
    };

    if (!string.IsNullOrEmpty(llmApiKey))
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", llmApiKey);

    var safeComment = reviewComment.Length > 10000 ? reviewComment[..10000] + "...(truncated)" : reviewComment;
    var systemPrompt = ToolPromptBuilder.BuildSystemPromptWithTools("", mcpManager.Tools ?? []);

    var userMessage = $"Please publish the following code review comment as a merge request note by calling 'create_merge_request_note' with project_id='{projectId}', merge_request_iid='{mergeRequestIid}', and body containing the review text below.\n\n{safeComment}";

    var messages = new List<Dictionary<string, object>>();
    messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt });
    messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = userMessage });

    const int maxRetries = 3;
    
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = llmModel,
            ["messages"] = messages,
        };

        request["max_tokens"] = 50000;
        request["stream"] = false;
        request["temperature"] = 0.2;

        var response = await httpClient.PostAsJsonAsync("/v1/chat/completions", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<QwenChatResponse>();
        var choice = result?.Choices?.FirstOrDefault();

        if (choice == null) continue;

        var assistantMsg = choice.Message;
        string? content = assistantMsg?.Content;
        List<QwenToolCall>? toolCalls = assistantMsg?.ToolCalls ?? [];

        // Try native OpenAI-style tool_calls
        if (toolCalls != null && toolCalls.Count > 0)
        {
            foreach (var tc in toolCalls)
            {
                var funcName = tc.Function?.Name ?? "";
                if (funcName is "create_merge_request_note" or "createMergeRequestNote")
                {
                    Console.WriteLine($"LLM вернул инструмент: {funcName}");

                    Dictionary<string, object> args;
                    try
                    {
                        args = McpToolMapper.ParseToolCallArguments(tc.Function.Arguments ?? "{}");
                    }
                    catch (JsonException)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Ошибка парсинга аргументов.");
                        Console.ResetColor();
                        return false;
                    }

                    if (!args.ContainsKey("project_id")) args["project_id"] = projectId;
                    if (!args.ContainsKey("merge_request_iid")) args["merge_request_iid"] = mergeRequestIid;

                    if (!args.ContainsKey("body"))
                    {
                        args["body"] = safeComment;
                    }

                    return await ExecuteCreateMergeRequestNoteAsync(mcpManager, projectId, mergeRequestIid, args);
                }

                Console.WriteLine($"LLM вернул инструмент: {funcName}");
            }
        }

        // Try XML/backtick-style tool call blocks in response (qwen3.6 format)
        if (!string.IsNullOrEmpty(content))
        {
            var xmlToolCalls = ExtractXmlToolCallsForReview(content);

            if (xmlToolCalls != null && xmlToolCalls.Count > 0)
            {
                foreach (var call in xmlToolCalls)
                {
                    var toolName = call["name"]?.ToString() ?? "";
                    if (toolName is "create_merge_request_note" or "createMergeRequestNote")
                    {
                        Console.WriteLine($"Найден XML инструмент: {toolName}");

                        Dictionary<string, object> args;
                        try
                        {
                            if (call.ContainsKey("arguments"))
                                args = McpToolMapper.ParseToolCallArguments(JsonSerializer.Serialize(call["arguments"]));
                            else
                                args = new Dictionary<string, object>();
                        }
                        catch (JsonException)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("Ошибка парсинга аргументов.");
                            Console.ResetColor();
                            return false;
                        }

                        if (!args.ContainsKey("project_id")) args["project_id"] = projectId;
                        if (!args.ContainsKey("merge_request_iid")) args["merge_request_iid"] = mergeRequestIid;

                        if (!args.ContainsKey("body"))
                        {
                            args["body"] = safeComment;
                        }

                        return await ExecuteCreateMergeRequestNoteAsync(mcpManager, projectId, mergeRequestIid, args);
                    }

                    Console.WriteLine($"LLM вернул XML инструмент: {toolName}");
                }
            }

            // Fallback: try parsing entire response as JSON tool call (no wrapping)
            var trimmed = content.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("name", out var nameProp) && doc.RootElement.TryGetProperty("arguments", out var argsProp))
                    {
                        if (nameProp.GetString() is "create_merge_request_note" or "createMergeRequestNote")
                        {
                            Console.WriteLine("Найден JSON инструмент (без обёртки).");

                            Dictionary<string, object> args;
                            try
                            {
                                using var doc2 = JsonDocument.Parse(argsProp.GetRawText());
                                args = McpToolMapper.ParseJsonElement(doc2.RootElement);
                            }
                            catch (JsonException)
                            {
                                var normalized = NormalizePythonStyleToJson(argsProp.GetRawText());
                                try
                                {
                                    using var doc2 = JsonDocument.Parse(normalized);
                                    args = McpToolMapper.ParseJsonElement(doc2.RootElement);
                                }
                                catch (JsonException)
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine("Ошибка парсинга аргументов.");
                                    Console.ResetColor();
                                    return false;
                                }
                            }

                            if (!args.ContainsKey("project_id")) args["project_id"] = projectId;
                            if (!args.ContainsKey("merge_request_iid")) args["merge_request_iid"] = mergeRequestIid;

                            if (!args.ContainsKey("body"))
                            {
                                args["body"] = safeComment;
                            }

                            return await ExecuteCreateMergeRequestNoteAsync(mcpManager, projectId, mergeRequestIid, args);
                        }

                        Console.WriteLine($"Найден JSON инструмент: {nameProp.GetString()}");
                    }
                }
                catch (JsonException) { /* not valid JSON */ }

                // Also try Python-style: [name:"...",arguments:{...}] without wrapping
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        var jsonStr = NormalizePythonStyleToJson(trimmed);
                        using var doc2 = JsonDocument.Parse(jsonStr);
                        if (doc2.RootElement.TryGetProperty("name", out var nameProp2) && doc2.RootElement.TryGetProperty("arguments", out var argsProp2))
                        {
                            if (nameProp2.GetString() is "create_merge_request_note" or "createMergeRequestNote")
                            {
                                Console.WriteLine("Найден Python-style инструмент (без обёртки).");

                                Dictionary<string, object> args;
                                try
                                {
                                    using var doc3 = JsonDocument.Parse(argsProp2.GetRawText());
                                    args = McpToolMapper.ParseJsonElement(doc3.RootElement);
                                }
                                catch (JsonException)
                                {
                                    var normalized = NormalizePythonStyleToJson(argsProp2.GetRawText());
                                    try
                                    {
                                        using var doc3 = JsonDocument.Parse(normalized);
                                        args = McpToolMapper.ParseJsonElement(doc3.RootElement);
                                    }
                                    catch (JsonException)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Red;
                                        Console.WriteLine("Ошибка парсинга аргументов.");
                                        Console.ResetColor();
                                        return false;
                                    }
                                }

                                if (!args.ContainsKey("project_id")) args["project_id"] = projectId;
                                if (!args.ContainsKey("merge_request_iid")) args["merge_request_iid"] = mergeRequestIid;

                                if (!args.ContainsKey("body"))
                                {
                                    args["body"] = safeComment;
                                }

                                return await ExecuteCreateMergeRequestNoteAsync(mcpManager, projectId, mergeRequestIid, args);
                            }

                            Console.WriteLine($"Найден Python-style инструмент: {nameProp2.GetString()}");
                        }
                    }
                    catch (JsonException) { /* not valid Python-style object, ignore */ }
                }
            }

            // If we got content but no tool call, add it back for retry (LLM may need to try again)
            if (!string.IsNullOrEmpty(content))
            {
                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                });
                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = "You must call the tool to publish the review. Do not output text — use the tool only.",
                });
            }
        }
    }

    return false;
}

static async Task<bool> ExecuteCreateMergeRequestNoteAsync(McpServerManager mcpManager, string projectId, string mergeRequestIid, Dictionary<string, object> args)
{
    try
    {
        var callResult = await mcpManager.CallToolAsync("create_merge_request_note", args);
        var formatted = McpToolMapper.FormatToolResult(callResult);

        if (callResult.IsError == true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Ошибка GitLab: {formatted}");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Комментарий успешно опубликован. Результат: {formatted}");
        Console.ResetColor();
        return true;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Ошибка вызова инструмента: {ex.Message}");
        Console.ResetColor();
        return false;
    }
}

static string NormalizePythonStyleToJson(string pythonStyle)
{
    var sb = new StringBuilder();

    var trimmed = pythonStyle.Trim();
    bool isList = trimmed.StartsWith('[');

    if (isList)
        sb.Append('{');
    else
        sb.Append(trimmed);

    var text = isList ? trimmed[1..^1].Trim() : sb.ToString();

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

                if (i > 0)
                {
                    char prev = text[i - 1];
                    if (prev == '{' || prev == ',')
                    {
                        int j = i;
                        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                            j++;

                        int keyLen = j - i;
                        if (keyLen > 0 && !char.IsDigit(text[i]))
                        {
                            string key = text.Substring(i, keyLen);

                            int k = j;
                            while (k < text.Length && char.IsWhiteSpace(text[k])) k++;

                            if (k < text.Length && text[k] == ':') k++;
                            while (k < text.Length && char.IsWhiteSpace(text[k])) k++;

                            if (k < text.Length && (text[k] == '"' || text[k] == '\''))
                            {
                                char q = text[k];
                                k++;
                                while (k < text.Length && text[k] != q) k++;
                                if (k < text.Length) k++;
                            }
                            else if (k < text.Length && char.IsLetterOrDigit(text[k]) || text[k] == '_')
                            {
                                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                            }

                            string rawValue = text.Substring(j, k - j).Trim();

                            if (k > 0 && (text[k - 1] == '"' || text[k - 1] == '\''))
                            {
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

                            i = k - 1;
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
                int j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(text[j])) j--;

                if (j >= 0)
                {
                    int k = j;
                    bool isUnquotedKey = true;

                    while (k >= 0 && char.IsWhiteSpace(text[k])) k--;
                    if (!char.IsLetterOrDigit(text[k]) && text[k] != '_') isUnquotedKey = false;

                    if (isUnquotedKey)
                    {
                        while (k > 0)
                        {
                            k--;
                            if (!char.IsLetterOrDigit(text[k]) && text[k] != '_') { k++; break; }
                        }

                        string key = text.Substring(k, j - k + 1);

                        int m = i + 1;
                        while (m < text.Length && char.IsWhiteSpace(text[m])) m++;

                        if (m < text.Length && (text[m] == '"' || text[m] == '\''))
                        {
                            char q = text[m];
                            m++;
                            while (m < text.Length && text[m] != q) m++;
                            if (m < text.Length) m++;

                            result.Append($"\"{key}\":");
                            i = m - 1;
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

static async Task<string?> CallGetMergeRequestDiffViaMcpAsync(McpServerManager mcpManager, string projectId, string mergeRequestIid)
{
    var args = new Dictionary<string, object>
    {
        ["project_id"] = projectId,
        ["merge_request_iid"] = mergeRequestIid
    };

    Console.WriteLine("Вызов инструмента get_merge_request_diffs...");

    var result = await mcpManager.CallToolAsync("get_merge_request_diffs", args);

    var formatted = McpToolMapper.FormatToolResult(result);
    Console.WriteLine($"Результат: {formatted.Length} символов");

    return formatted;
}

static string CleanReviewResponse(string rawAnswer)
{
    if (string.IsNullOrEmpty(rawAnswer)) return rawAnswer;

    var text = rawAnswer.Trim();

    // Remove JSON wrappers with recognized keys
    if (text.StartsWith('{') && text.EndsWith('}'))
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("answer", out var answerProp))
                return answerProp.GetString()?.Trim() ?? text;

            if (root.TryGetProperty("comment", out var commentProp))
                return commentProp.GetString()?.Trim() ?? text;

            if (root.TryGetProperty("review", out var reviewProp))
                return reviewProp.GetString()?.Trim() ?? text;

            if (root.TryGetProperty("content", out var contentProp))
                return contentProp.GetString()?.Trim() ?? text;

            // If it's a simple JSON object with no recognized keys, return as-is
        }
        catch { /* not valid JSON */ }
    }

    // Remove markdown code block wrappers (```language ... ```)
    var firstNewline = text.IndexOf('\n');
    if (firstNewline > 0 && text.StartsWith("```"))
        text = text[(firstNewline + 1)..];

    var lastBackticks = text.LastIndexOf("```");
    if (lastBackticks > 0 && lastBackticks < text.Length - 1)
        text = text[..lastBackticks];

    // Remove citation wrappers like [CITATION:0]...[/CITATION]
    text = Regex.Replace(text, @"\[CITATION:\d+\].*?\[/CITATION\]", "", RegexOptions.Compiled);

    return text.Trim();
}

static string NormalizeGitUrl(string url, string? token)
{
    if (url.StartsWith("git@", StringComparison.Ordinal))
    {
        var serverAndPath = url[4..];

        int colonIndex = serverAndPath.IndexOf(':');
        if (colonIndex <= 0)
            throw new ArgumentException($"Invalid SSH URL format: {url}");

        var server = serverAndPath[..colonIndex];
        var path = serverAndPath[(colonIndex + 1)..];

        return url;
    }

    if (url.StartsWith("https://", StringComparison.Ordinal))
    {
        int atIndex = url.IndexOf('@');
        if (atIndex > 0)
            return url[..atIndex] + $":oauth2:{token}@{url[(atIndex + 1)..]}";

        var uri = new Uri(url);
        return url.Replace(uri.Authority, $"oauth2:{token}@{uri.Authority}");
    }

throw new ArgumentException($"Unsupported URL scheme: {url}. Use SSH (git@...) or HTTPS.");
}
