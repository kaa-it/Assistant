using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public class OpenAiCompatibleEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private bool _disposed;

    public OpenAiCompatibleEmbeddingService(
        string? baseUrl = null,
        string? model = null)
    {
        var url = (baseUrl ?? Environment.GetEnvironmentVariable("EMBEDDING_API_URL") ?? "http://localhost:1234").TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(60)
        };

        var key = Environment.GetEnvironmentVariable("EMBEDDING_API_KEY");
        if (!string.IsNullOrEmpty(key))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        _model = model ?? Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "nomic-embed-text";
    }

    public async Task<float[]> GenerateQueryEmbeddingAsync(string query, CancellationToken ct = default)
    {
        var prefixedTexts = new[] { $"search_query: {query}" };
        var embeddings = await EmbedWithRetryAsync(prefixedTexts.ToList(), ct);
        if (embeddings[0].Length != 768)
            throw new InvalidOperationException($"Expected 768-dimensional embedding, got {embeddings[0].Length}");
        return embeddings[0];
    }

    public async Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return [];

        var batches = new List<List<string>>();
        var batch = new List<string>();
        foreach (var t in textList)
        {
            batch.Add(t);
            if (batch.Count >= 10)
            {
                batches.Add(batch);
                batch = [];
            }
        }
        if (batch.Count > 0) batches.Add(batch);

        var results = new float[textList.Count][];
        var index = 0;

        foreach (var b in batches)
        {
            var prefixedTexts = b.Select(t => $"search_document: {t}").ToList();
            var embeddings = await EmbedWithRetryAsync(prefixedTexts, ct);
            foreach (var emb in embeddings)
            {
                if (emb.Length != 768)
                    throw new InvalidOperationException($"Expected 768-dimensional embedding, got {emb.Length}");
                results[index++] = emb;
            }
        }

        return results;
    }

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            await EmbedWithRetryAsync(new List<string> { "test" }, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }

    private async Task<List<float[]>> EmbedWithRetryAsync(List<string> texts, CancellationToken ct)
    {
        var maxRetries = 3;
        var delay = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var request = new EmbeddingRequest
                {
                    Input = texts,
                    Model = _model
                };

                var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
                if (result?.Data == null || result.Data.Count != texts.Count)
                    throw new InvalidOperationException("Invalid embedding response");

                return result.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
            }
            catch (Exception ex) when (attempt < maxRetries && (ex is HttpRequestException or TaskCanceledException))
            {
                if (ct.IsCancellationRequested) throw;
                await Task.Delay(delay * attempt, ct);
            }
        }

        throw new HttpRequestException("Failed to generate embeddings after 3 retries");
    }

    private class EmbeddingRequest
    {
        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = [];

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";
    }

    private class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData> Data { get; set; } = [];
    }

    private class EmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }
}
