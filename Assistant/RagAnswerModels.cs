public record SourceReference(
    string Source,
    string? Section,
    string ChunkId,
    float RelevanceScore,
    int ChunkIndex,
    int TotalChunks
);

public record Citation(
    string Quote,
    int SourceIndex,
    string? Explanation
);

public record CitationAnswer(
    string Answer,
    ConfidenceLevel Confidence,
    string? ClarificationRequest,
    List<SourceReference> Sources,
    List<Citation> Citations
);
