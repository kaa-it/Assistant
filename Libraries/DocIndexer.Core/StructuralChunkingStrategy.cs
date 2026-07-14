public class StructuralChunkingStrategy(int maxChunkSize = 512, int overlap = 50) : IChunkingStrategy
{
    public ChunkingStrategy StrategyType => ChunkingStrategy.Structural;

    public IEnumerable<DocumentChunk> Chunk(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fallback = new FixedSizeChunkingStrategy(maxChunkSize, overlap);
        var now = DateTime.UtcNow;

        return ext switch
        {
            ".md" => ChunkMarkdown(filePath, content, now),
            ".txt" => ChunkTxt(filePath, content, now),
            ".cs" => ChunkCSharp(filePath, content, now),
            _ => ChunkSections(filePath, content, fallback, now)
        };
    }

    private IEnumerable<DocumentChunk> ChunkMarkdown(string filePath, string content, DateTime now)
    {
        var lines = content.Split('\n');
        var sections = new List<(string? section, string content)>();
        string? currentSection = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var headingLevel = 0;
            while (headingLevel < trimmed.Length && headingLevel < 6 && trimmed[headingLevel] == '#')
                headingLevel++;

            if (headingLevel > 0 && headingLevel < trimmed.Length && trimmed[headingLevel] == ' ')
            {
                if (currentLines.Count > 0)
                    sections.Add((currentSection, string.Join('\n', currentLines)));

                currentSection = trimmed[(headingLevel + 1)..].Trim();
                currentLines = [line];
            }
            else
            {
                currentLines.Add(line);
            }
        }

        if (currentLines.Count > 0)
            sections.Add((currentSection, string.Join('\n', currentLines)));

        return ProcessSections(filePath, sections, now);
    }

    private IEnumerable<DocumentChunk> ChunkTxt(string filePath, string content, DateTime now)
    {
        var normalized = content.Replace("\r\n", "\n");
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        if (blocks.Length <= 1)
            blocks = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var sections = new List<(string? section, string content)>();
        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var lines = trimmed.Split('\n');
            var firstLine = lines[0].Trim();
            var isHeading = firstLine.Length <= 100
                && !firstLine.EndsWith('.')
                && !firstLine.EndsWith(',')
                && char.IsUpper(firstLine[0]);
            var section = isHeading ? firstLine : null;
            sections.Add((section, trimmed));
        }

        return ProcessSections(filePath, sections, now);
    }

    private IEnumerable<DocumentChunk> ChunkCSharp(string filePath, string content, DateTime now)
    {
        var lines = content.Split('\n');
        var sections = new List<(string? section, string content)>();
        string? currentRegion = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#region "))
            {
                if (currentLines.Count > 0)
                {
                    var unregioned = string.Join('\n', currentLines).Trim();
                    if (!string.IsNullOrEmpty(unregioned))
                        sections.Add((currentRegion ?? "Код вне региона", unregioned));
                }

                currentRegion = trimmed["#region ".Length..].Trim();
                currentLines = [line];
            }
            else if (trimmed == "#endregion")
            {
                currentLines.Add(line);
                var regionContent = string.Join('\n', currentLines).Trim();
                if (!string.IsNullOrEmpty(regionContent))
                    sections.Add((currentRegion, regionContent));
                currentRegion = "Код вне региона";
                currentLines = [];
            }
            else
            {
                currentLines.Add(line);
            }
        }

        if (currentLines.Count > 0)
        {
            var remaining = string.Join('\n', currentLines).Trim();
            if (!string.IsNullOrEmpty(remaining))
                sections.Add((currentRegion ?? "Код вне региона", remaining));
        }

        if (sections.Count <= 1)
        {
            sections = [];
            var blockLines = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (blockLines.Count > 0)
                    {
                        var block = string.Join('\n', blockLines).Trim();
                        if (!string.IsNullOrEmpty(block))
                        {
                            var first = blockLines[0].Trim();
                            var sec = IsCSharpSectionHeader(first) ? first : null;
                            sections.Add((sec, block));
                        }
                        blockLines = [];
                    }
                }
                else
                {
                    blockLines.Add(line);
                }
            }
            if (blockLines.Count > 0)
            {
                var block = string.Join('\n', blockLines).Trim();
                if (!string.IsNullOrEmpty(block))
                {
                    var first = blockLines[0].Trim();
                    var sec = IsCSharpSectionHeader(first) ? first : null;
                    sections.Add((sec, block));
                }
            }
        }

        return ProcessSections(filePath, sections, now);
    }

    private static bool IsCSharpSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 100 && (
            trimmed.StartsWith("//") ||
            trimmed.StartsWith("/*") ||
            trimmed.StartsWith("public ") ||
            trimmed.StartsWith("private ") ||
            trimmed.StartsWith("internal ") ||
            trimmed.StartsWith("protected ") ||
            trimmed.StartsWith("class ") ||
            trimmed.StartsWith("interface ") ||
            trimmed.StartsWith("enum ") ||
            trimmed.StartsWith("record ") ||
            trimmed.StartsWith("struct ")
        );
    }

    private IEnumerable<DocumentChunk> ChunkSections(string filePath, string content, FixedSizeChunkingStrategy fallback, DateTime now)
    {
        var fallbackChunks = fallback.Chunk(filePath, content).ToList();
        var total = fallbackChunks.Count;
        for (int i = 0; i < fallbackChunks.Count; i++)
        {
            yield return fallbackChunks[i] with
            {
                Strategy = ChunkingStrategy.Structural,
                ChunkIndex = i,
                TotalChunks = total
            };
        }
    }

    private IEnumerable<DocumentChunk> ProcessSections(string filePath, List<(string? section, string content)> sections, DateTime now)
    {
        var fallback = new FixedSizeChunkingStrategy(maxChunkSize, overlap);
        var fileChunks = new List<DocumentChunk>();

        foreach (var (section, sectionContent) in sections)
        {
            if (string.IsNullOrWhiteSpace(sectionContent)) continue;

            var wordCount = sectionContent.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

            if (wordCount <= maxChunkSize)
            {
                fileChunks.Add(new DocumentChunk
                {
                    ChunkId = Guid.NewGuid().ToString(),
                    Source = filePath,
                    Title = Path.GetFileName(filePath),
                    Section = section,
                    Content = sectionContent,
                    ChunkIndex = 0,
                    TotalChunks = 0,
                    Strategy = ChunkingStrategy.Structural,
                    IndexedAt = now
                });
            }
            else
            {
                var subChunks = fallback.Chunk(filePath, sectionContent).ToList();
                foreach (var sc in subChunks)
                {
                    fileChunks.Add(new DocumentChunk
                    {
                        ChunkId = sc.ChunkId,
                        Source = sc.Source,
                        Title = sc.Title,
                        Section = section,
                        Content = sc.Content,
                        ChunkIndex = 0,
                        TotalChunks = 0,
                        Strategy = ChunkingStrategy.Structural,
                        IndexedAt = now
                    });
                }
            }
        }

        var total = fileChunks.Count;
        for (int i = 0; i < fileChunks.Count; i++)
        {
            yield return fileChunks[i] with { ChunkIndex = i, TotalChunks = total };
        }
    }
}
