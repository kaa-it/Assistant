public interface IChunkingStrategy
{
    ChunkingStrategy StrategyType { get; }
    IEnumerable<DocumentChunk> Chunk(string filePath, string content);
}
