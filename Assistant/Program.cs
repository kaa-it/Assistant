using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

const string Usage = """
Assistant <repository-url> [target-directory] [--chat]

Clones a Git repository at startup, then indexes the cloned content.
With --chat flag: starts interactive RAG chat with a local LLM after indexing.

Arguments:
  repository-url    URL of the Git repository (required)
  target-directory  Optional output directory (default: ./cloned-repo)

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

for (int i = 1; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("--chat", StringComparison.OrdinalIgnoreCase))
    {
        runChat = true;
    }
    else if (!targetDir.Equals("./cloned-repo", StringComparison.Ordinal) || i > 1)
    {
        // First non-flag arg is target directory
        if (targetDir.Equals("./cloned-repo", StringComparison.Ordinal))
            targetDir = arg;
    }
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
    await RunIndexingAsync(targetDir, runChat, resolvedTargetDir);
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
        await RunIndexingAsync(targetDir, runChat, resolvedTargetDir);
    }
    else
    {
        Console.Error.WriteLine($"Error: Git clone failed with exit code {process.ExitCode}.");

        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine(error);

        Environment.ExitCode = 1;
    }
}

static async Task RunIndexingAsync(string targetDir, bool runChat, string resolvedTargetDir)
{
    var dbPath = Path.Combine(Path.GetFullPath(targetDir), "document_index.db");

    if (File.Exists(dbPath))
    {
        Console.WriteLine($"\nИндекс уже существует: {dbPath}");

        if (runChat)
        {
            Console.WriteLine("\n=== Запуск интерактивного чата ===");
            await RunChatAsync(targetDir, dbPath, resolvedTargetDir);
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
        await RunChatAsync(targetDir, dbPath, resolvedTargetDir);
    }
}

static async Task RunChatAsync(string targetDir, string dbPath, string resolvedTargetDir)
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
        chat = new ChatService(llm, rag, validator, mcpManager, resolvedTargetDir);
    }
    else if (rag != null)
    {
        chat = new ChatService(llm, rag, validator);
    }
    else
    {
        chat = new ChatService(llm, mcpManager);
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
