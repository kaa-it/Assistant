public record DocumentChunk
{
    public required string ChunkId { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string? Section { get; init; }
    public required string Content { get; init; }
    public required int ChunkIndex { get; init; }
    public required int TotalChunks { get; init; }
    public required ChunkingStrategy Strategy { get; init; }
    public required DateTime IndexedAt { get; init; }
}

public record IndexedChunk : DocumentChunk
{
    public required float[] Embedding { get; init; }
}

public record IndexingResult(int FilesProcessed, int ChunksCreated, TimeSpan Duration);

public record ChunkingStats(
    int ChunkCount,
    double AvgChunkLengthChars,
    double AvgChunkLengthWords,
    int FilesWithMultipleChunks,
    int? SectionsDetected
);
