public interface IVectorStore
{
    Task InitializeAsync();
    Task SaveChunksAsync(IEnumerable<IndexedChunk> chunks);
    Task<IEnumerable<IndexedChunk>> SearchSimilarAsync(float[] queryEmbedding, int topK = 5, ChunkingStrategy? strategy = null);
    Task<IEnumerable<(IndexedChunk chunk, float similarity)>> SearchSimilarWithScoresAsync(float[] queryEmbedding, int topK = 5, ChunkingStrategy? strategy = null);
    Task<long> GetChunkCountAsync(ChunkingStrategy? strategy = null);
}
