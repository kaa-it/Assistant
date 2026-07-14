using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public class OpenAiCompatibleLlmService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _defaultMaxTokens;
    private bool _disposed;

    public OpenAiCompatibleLlmService(
        string? baseUrl = null,
        string? model = null,
        string? apiKey = null,
        int defaultMaxTokens = 1024)
    {
        var url = (baseUrl ?? Environment.GetEnvironmentVariable("LLM_API_URL") ?? "http://localhost:1234").TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(120)
        };

        var key = apiKey ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (!string.IsNullOrEmpty(key))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        _model = model ?? Environment.GetEnvironmentVariable("LLM_MODEL") ?? "qwen/qwen3.6-35b-a3b";
        _defaultMaxTokens = defaultMaxTokens;
    }

    public async Task<string> AskAsync(string prompt, string? systemPrompt = null, int? maxTokens = null, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

        messages.Add(new ChatMessage { Role = "user", Content = prompt });

        var request = new ChatRequest
        {
            Model = _model,
            Messages = messages,
            MaxTokens = maxTokens ?? _defaultMaxTokens,
            Stream = false,
            Temperature = 0.2,
            TopP = 0.9,
            TopK = 40,
        };

        var result = await SendWithRetryAsync(request, ct);
        return result;
    }

    private async Task<string> SendWithRetryAsync(ChatRequest request, CancellationToken ct)
    {
        var maxRetries = 3;
        var delay = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
                var content = result?.Choices?.FirstOrDefault()?.Message?.Content;

                if (string.IsNullOrEmpty(content))
                    throw new InvalidOperationException("Empty response from LLM");

                return content;
            }
            catch (Exception ex) when (attempt < maxRetries && (ex is HttpRequestException or TaskCanceledException))
            {
                if (ct.IsCancellationRequested) throw;
                await Task.Delay(delay * attempt, ct);
            }
        }

        throw new HttpRequestException("Failed to get LLM response after 3 retries");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }

    private class ChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
        
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
        
        [JsonPropertyName("top_p")]
        public double TopP { get; set; }
        
        [JsonPropertyName("top_k")]
        public int TopK { get; set; }
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    private class ChatResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; set; } = [];
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }
}
