public interface IEmbeddingService
{
    Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
    Task<float[]> GenerateQueryEmbeddingAsync(string query, CancellationToken ct = default);
}
