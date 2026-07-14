public enum RagPipelineMode
{
    Baseline,
    WithThreshold,
    WithReranker,
    FullPipeline,
    CitationEnforced
}

public record RagResult(
    string Context,
    string[] Sources,
    string OriginalQuestion,
    string? RewrittenQuestion,
    List<ScoredChunk> Chunks,
    RagPipelineMode Mode,
    float SimilarityThreshold,
    int TopKPre,
    int TopKPost,
    int FilteredCount,
    float MaxChunkSimilarity,
    ConfidenceLevel Confidence,
    bool IsUnknown,
    string? UnknownReason
);
