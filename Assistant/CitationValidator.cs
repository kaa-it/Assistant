using System.Text.RegularExpressions;

public class CitationValidator
{
    public ValidationResult Validate(CitationAnswer answer, List<ScoredChunk> contextChunks)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (answer.Confidence != ConfidenceLevel.Unknown && answer.Sources.Count == 0)
            errors.Add("No sources provided despite non-unknown confidence");

        if (answer.Confidence != ConfidenceLevel.Unknown && answer.Citations.Count == 0)
            errors.Add("No citations provided despite non-unknown confidence");

        foreach (var citation in answer.Citations)
        {
            if (citation.SourceIndex < 0 || citation.SourceIndex >= contextChunks.Count)
            {
                errors.Add($"Citation references invalid source index {citation.SourceIndex} (max {contextChunks.Count - 1})");
                continue;
            }

            var chunk = contextChunks[citation.SourceIndex];
            if (!CitationAnswerParser.ValidateQuoteExists(citation.Quote, chunk.Chunk.Content))
                errors.Add($"Quote not found in chunk {citation.SourceIndex}: '{citation.Quote[..Math.Min(50, citation.Quote.Length)]}...'");
        }

        if (answer.Confidence != ConfidenceLevel.Unknown)
        {
            var citationRefs = Regex.Matches(answer.Answer, @"\[CITATION:(\d+)\]");
            if (citationRefs.Count == 0)
                errors.Add("Answer has no inline [CITATION:N] references — every factual claim must be cited");

            var referencedIndices = citationRefs.Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            var availableIndices = answer.Citations.Select((c, i) => i).ToHashSet();
            var missing = referencedIndices.Except(availableIndices).ToList();
            if (missing.Count > 0)
                errors.Add($"Answer references missing citations: {string.Join(", ", missing)}");
        }

        var minLen = GetEnvInt("RAG_CITATION_MIN_LENGTH", 30);
        var maxLen = GetEnvInt("RAG_CITATION_MAX_LENGTH", 200);
        foreach (var c in answer.Citations)
        {
            if (c.Quote.Length < minLen)
                warnings.Add($"Citation quote too short ({c.Quote.Length} < {minLen} chars)");
            if (c.Quote.Length > maxLen)
                warnings.Add($"Citation quote too long ({c.Quote.Length} > {maxLen} chars)");
        }

        return new ValidationResult(errors.Count == 0, errors, warnings);
    }

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;
}

public record ValidationResult(bool IsValid, List<string> Errors, List<string> Warnings);
