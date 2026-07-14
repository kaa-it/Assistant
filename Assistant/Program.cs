using System.Diagnostics;
using System.Text;

const string Usage = """
Assistant <repository-url> [target-directory]

Clones a Git repository at startup.

Arguments:
  repository-url    URL of the Git repository (required)
  target-directory  Optional output directory (default: ./cloned-repo)

Environment variables:
  GIT_PERSONAL_ACCESS_TOKEN    Personal Access Token for HTTPS URLs (optional, required when using https://)

Examples:
  dotnet run -- git@gitserver.local.yurion.ru:andreyk/rust-design-patterns.git
  dotnet run -- https://gitserver.local.yurion.ru/andreyk/rust-design-patterns.git ./output-dir
""";

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    Environment.ExitCode = 1;
    return;
}

var repoUrl = args[0];
string targetDir = args.Length > 1 ? args[1] : "./cloned-repo";

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
Console.WriteLine($"Target directory: {Path.GetFullPath(targetDir)}");

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
}
else
{
    Console.Error.WriteLine($"Error: Git clone failed with exit code {process.ExitCode}.");

    if (!string.IsNullOrWhiteSpace(error))
        Console.Error.WriteLine(error);

    Environment.ExitCode = 1;
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
