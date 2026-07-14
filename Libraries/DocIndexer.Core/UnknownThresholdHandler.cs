public class UnknownThresholdHandler
{
    private readonly float _minSimilarity;
    private readonly float _minHighConfidenceSimilarity;

    public UnknownThresholdHandler(
        float minSimilarity = 0.45f,
        float minHighConfidence = 0.65f)
    {
        _minSimilarity = minSimilarity;
        _minHighConfidenceSimilarity = minHighConfidence;
    }

    public RelevanceAssessment AssessRelevance(List<ScoredChunk> chunks)
    {
        if (chunks.Count == 0)
            return new RelevanceAssessment(false, 0, "No chunks found");

        var maxSim = chunks.Max(c => c.FinalScore);

        if (maxSim < _minSimilarity)
        {
            return new RelevanceAssessment(
                CanAnswer: false,
                MaxSimilarity: (float)maxSim,
                Reason: $"Best chunk relevance ({maxSim:F3}) below threshold ({_minSimilarity})"
            );
        }

        var confidence = maxSim >= _minHighConfidenceSimilarity
            ? ConfidenceLevel.High
            : (maxSim >= _minSimilarity + 0.1f ? ConfidenceLevel.Medium : ConfidenceLevel.Low);

        return new RelevanceAssessment(CanAnswer: true, MaxSimilarity: (float)maxSim, Reason: null, Confidence: confidence);
    }
}

public record RelevanceAssessment(
    bool CanAnswer,
    float MaxSimilarity,
    string? Reason,
    ConfidenceLevel Confidence = ConfidenceLevel.Unknown
);
