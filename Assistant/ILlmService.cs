public interface ILlmService : IDisposable
{
    Task<string> AskAsync(string prompt, string? systemPrompt = null, int? maxTokens = null, CancellationToken ct = default);
}
