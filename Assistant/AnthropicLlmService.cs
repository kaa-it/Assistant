using Anthropic;
using Anthropic.Models.Messages;

public class AnthropicLlmService : ILlmService
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly int _defaultMaxTokens;
    private bool _disposed;

    public AnthropicClient Client => _client;
    public string Model => _model;

    public AnthropicLlmService(
        string? apiKey = null,
        string? model = null,
        int defaultMaxTokens = 4096)
    {
        var key = apiKey
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? "";

        _client = new AnthropicClient { ApiKey = "", BaseUrl = "http://192.168.1.15:1234" };
        _model = model
                 ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
                 ?? "claude-opus-4-8";
        _defaultMaxTokens = defaultMaxTokens;
    }

    public async Task<string> AskAsync(string prompt, string? systemPrompt = null, int? maxTokens = null,
        CancellationToken ct = default)
    {
        var messages = new List<MessageParam>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = systemPrompt + "\n\n" + prompt
            });
        }
        else
        {
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = prompt
            });
        }

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens ?? _defaultMaxTokens,
            Messages = messages,
        };

        var response = await _client.Messages.Create(parameters);

        return string.Concat(response.Content
            .Select(block => block.Value)
            .OfType<TextBlock>()
            .Select(tb => tb.Text ?? string.Empty));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _disposed = true;
        }
    }
}
