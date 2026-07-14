using System.Diagnostics;

public class IndexingPipeline(
    IChunkingStrategy chunkingStrategy,
    IEmbeddingService embeddingService,
    IVectorStore vectorStore)
{
    public async Task<IndexingResult> RunAsync(
        string rootDirectory,
        string[] fileExtensions,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var files = Directory.EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => fileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Найдено файлов: {files.Count}");
        Console.ResetColor();

        var allChunks = new List<DocumentChunk>();
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var fileChunks = chunkingStrategy.Chunk(file, content).ToList();
                allChunks.AddRange(fileChunks);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  Пропущен {file}: {ex.Message}");
                Console.ResetColor();
            }
        }

        var total = allChunks.Count;
        if (total == 0)
            return new IndexingResult(files.Count, 0, sw.Elapsed);

        var batchSize = 10;
        var processed = 0;

        for (int i = 0; i < allChunks.Count; i += batchSize)
        {
            var batch = allChunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Content).ToList();
            var embeddings = await embeddingService.GenerateEmbeddingsAsync(texts, ct);

            var indexedBatch = new List<IndexedChunk>();
            for (int j = 0; j < batch.Count; j++)
            {
                indexedBatch.Add(new IndexedChunk
                {
                    ChunkId = batch[j].ChunkId,
                    Source = batch[j].Source,
                    Title = batch[j].Title,
                    Section = batch[j].Section,
                    Content = batch[j].Content,
                    ChunkIndex = batch[j].ChunkIndex,
                    TotalChunks = batch[j].TotalChunks,
                    Strategy = batch[j].Strategy,
                    IndexedAt = batch[j].IndexedAt,
                    Embedding = embeddings[j]
                });
            }

            await vectorStore.SaveChunksAsync(indexedBatch);
            processed += batch.Count;
            progress?.Report((double)processed / total);
        }

        sw.Stop();
        return new IndexingResult(files.Count, total, sw.Elapsed);
    }
}
