public record ScoredChunk(
    IndexedChunk Chunk,
    float OriginalSimilarity,
    double FinalScore,
    double KeywordScore
);

public class HeuristicReranker
{
    public List<ScoredChunk> Rerank(string query, List<(IndexedChunk chunk, float similarity)> candidates)
    {
        var queryWords = query.ToLowerInvariant()
            .Split([' ', '\t', '\n', '?', '!', '.', ',', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Distinct()
            .ToHashSet();

        return candidates.Select(c => {
            var chunkText = c.chunk.Content.ToLowerInvariant();
            var chunkWords = chunkText.Split([' ', '\t', '\n', '.', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

            var matchedKeywords = queryWords.Count(qw => chunkWords.Contains(qw));
            var keywordScore = queryWords.Count > 0 ? (double)matchedKeywords / queryWords.Count : 0;

            var wordCount = chunkWords.Length;
            var lengthBoost = wordCount switch
            {
                < 50 => 0.5,
                < 200 => 1.0,
                < 500 => 0.9,
                _ => 0.7
            };

            var finalScore = 0.6 * c.similarity + 0.3 * keywordScore + 0.1 * lengthBoost;

            return new ScoredChunk(c.chunk, c.similarity, finalScore, keywordScore);
        }).OrderByDescending(s => s.FinalScore).ToList();
    }
}
