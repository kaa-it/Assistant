public class FixedSizeChunkingStrategy(int chunkSize = 512, int overlap = 50) : IChunkingStrategy
{
    public ChunkingStrategy StrategyType => ChunkingStrategy.Structural;

    public IEnumerable<DocumentChunk> Chunk(string filePath, string content)
    {
        var words = content.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        var step = chunkSize - overlap;
        if (step <= 0) step = 1;

        var now = DateTime.UtcNow;
        var title = Path.GetFileName(filePath);
        var index = 0;

        int totalChunks;
        if (words.Length <= chunkSize)
            totalChunks = 1;
        else
            totalChunks = 1 + (words.Length - chunkSize + step - 1) / step;

        for (int offset = 0; offset < words.Length; offset += step, index++)
        {
            var count = Math.Min(chunkSize, words.Length - offset);
            var chunkWords = words[offset..(offset + count)];
            var content_ = string.Join(' ', chunkWords);

            yield return new DocumentChunk
            {
                ChunkId = Guid.NewGuid().ToString(),
                Source = filePath,
                Title = title,
                Section = null,
                Content = content_,
                ChunkIndex = index,
                TotalChunks = totalChunks,
                Strategy = ChunkingStrategy.Structural,
                IndexedAt = now
            };
        }
    }
}
