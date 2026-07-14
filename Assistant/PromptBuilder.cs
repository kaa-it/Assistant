using System.Text;

public class PromptBuilder
{
    private const string BaseSystemPrompt = @"You are a precise technical assistant answering questions about an indexed software project. 
Your answers MUST be grounded exclusively in the provided context chunks and available tools.

STRICT RULES:
1. Answer ONLY using information from the provided context chunks and tool results.
2. If the context does not contain sufficient information, output JSON with confidence: ""unknown"".
3. Every factual claim MUST be backed by a citation from the context or tool result.
4. Citations must be EXACT substrings (30-200 chars) from the context — no paraphrasing.
5. NEVER fabricate sources, citations, or facts not present in the context.
6. Respond in the same language as the user's question.";

    public static string BuildSystemPrompt() => BaseSystemPrompt;

    public static string BuildUserPrompt(string question, List<ScoredChunk> chunks, ConfidenceLevel confidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"User question: {question}");
        sb.AppendLine($"Estimated confidence based on retrieval: {confidence}");
        sb.AppendLine();
        sb.AppendLine("Context chunks (use ONLY these to answer):");
        sb.AppendLine("============================================");

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            sb.AppendLine($"--- CHUNK [{i}] ---");
            sb.AppendLine($"source: {c.Chunk.Source}");
            sb.AppendLine("section: " + (c.Chunk.Section ?? "N/A"));
            sb.AppendLine($"chunk_id: {c.Chunk.ChunkId}");
            sb.AppendLine($"chunk_index: {c.Chunk.ChunkIndex}/{c.Chunk.TotalChunks}");
            sb.AppendLine($"relevance_score: {c.FinalScore:F3}");
            sb.AppendLine("--- CONTENT ---");
            sb.AppendLine(c.Chunk.Content);
            sb.AppendLine();
        }

        sb.AppendLine("============================================");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("1. Respond with RAW JSON only. No markdown code blocks.");
        sb.AppendLine("2. Include at least one citation for every key claim.");
        sb.AppendLine("3. Use [CITATION:0], [CITATION:1], etc. inline in the answer text.");
        sb.AppendLine("4. If confidence is 'unknown', set answer to empty string and provide clarification_request.");
        sb.AppendLine("5. The 'quote' in each citation MUST be an exact substring of the corresponding chunk content.");
        sb.AppendLine("6. The 'source_index' in each citation MUST match the index of the source in the sources array.");

        return sb.ToString();
    }
}
