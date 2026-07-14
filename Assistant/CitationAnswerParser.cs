using System.Text.Json;
using System.Text.RegularExpressions;

public class CitationAnswerParser
{
    public static string ExtractJson(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return "{}";

        var markdownMatch = Regex.Match(rawResponse, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (markdownMatch.Success)
        {
            var inner = markdownMatch.Groups[1].Value.Trim();
            if (IsValidJsonStart(inner))
                return inner;
        }

        var firstBrace = rawResponse.IndexOf('{');
        var lastBrace = rawResponse.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var candidate = rawResponse[firstBrace..(lastBrace + 1)];
            if (IsValidJsonStart(candidate))
                return candidate;
        }

        if (IsValidJsonStart(rawResponse.Trim()))
            return rawResponse.Trim();

        throw new JsonException("Could not extract valid JSON from LLM response.");
    }

    private static bool IsValidJsonStart(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("{") && trimmed.EndsWith("}");
    }

    public static CitationAnswer Parse(string rawResponse, List<ScoredChunk> contextChunks)
    {
        var json = ExtractJson(rawResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var answer = root.TryGetProperty("answer", out var answerEl)
            ? answerEl.GetString() ?? ""
            : "";

        var confidenceStr = root.TryGetProperty("confidence", out var confEl)
            ? confEl.GetString() ?? "unknown"
            : "unknown";

        var confidence = confidenceStr.ToLowerInvariant() switch
        {
            "high" => ConfidenceLevel.High,
            "medium" => ConfidenceLevel.Medium,
            "low" => ConfidenceLevel.Low,
            _ => ConfidenceLevel.Unknown
        };

        var clarificationRequest = root.TryGetProperty("clarification_request", out var cr)
            ? cr.GetString()
            : null;

        var sources = new List<SourceReference>();
        if (root.TryGetProperty("sources", out var sourcesEl))
        {
            foreach (var s in sourcesEl.EnumerateArray())
            {
                var srcPath = s.TryGetProperty("source", out var srcP) ? srcP.GetString() ?? "" : "";
                var chunkId = s.TryGetProperty("chunk_id", out var cid) ? cid.GetString() ?? "" : "";

                var matchingChunk = contextChunks.FirstOrDefault(
                    c => c.Chunk.ChunkId.Equals(chunkId, StringComparison.OrdinalIgnoreCase)
                      || c.Chunk.Source.Equals(srcPath, StringComparison.OrdinalIgnoreCase));

                var relevanceScore = ReadFloatProperty(s, "score", 0f);
                var chunkIndex = s.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                sources.Add(new SourceReference(
                    Source: srcPath,
                    Section: s.TryGetProperty("section", out var sec) ? sec.GetString() : null,
                    ChunkId: chunkId,
                    RelevanceScore: relevanceScore,
                    ChunkIndex: chunkIndex,
                    TotalChunks: matchingChunk?.Chunk.TotalChunks ?? 0
                ));
            }
        }

        var citations = new List<Citation>();
        if (root.TryGetProperty("citations", out var citationsEl))
        {
            foreach (var c in citationsEl.EnumerateArray())
            {
                var quote = c.TryGetProperty("quote", out var qEl) ? qEl.GetString() ?? "" : "";
                var sourceIndex = c.TryGetProperty("source_index", out var siEl) ? siEl.GetInt32() : 0;
                var explanation = c.TryGetProperty("explanation", out var exEl) ? exEl.GetString() : null;

                citations.Add(new Citation(quote, sourceIndex, explanation));
            }
        }

        return new CitationAnswer(answer, confidence, clarificationRequest, sources, citations);
    }

    private static float ReadFloatProperty(JsonElement element, string propertyName, float defaultValue)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return defaultValue;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetSingle(),
            JsonValueKind.String => float.TryParse(prop.GetString(), out var v) ? v : defaultValue,
            _ => defaultValue
        };
    }

    public static bool ValidateQuoteExists(string quote, string chunkContent)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(chunkContent))
            return false;

        var normalizedQuote = NormalizeWhitespace(quote);
        var normalizedContent = NormalizeWhitespace(chunkContent);
        return normalizedContent.Contains(normalizedQuote, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    public static string ExtractSafeQuote(string content, int maxLength)
    {
        var lines = content.Split('\n');
        var bodyStart = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#')) continue;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            bodyStart = i;
            break;
        }

        var body = string.Join(" ", lines[bodyStart..]).Trim();
        if (body.Length <= maxLength) return body;

        var end = maxLength;
        while (end > maxLength / 2 && body[end] != '.' && body[end] != '!' && body[end] != '?')
            end--;
        if (end <= maxLength / 2) end = maxLength;
        else end++;

        return body[..end];
    }
}
