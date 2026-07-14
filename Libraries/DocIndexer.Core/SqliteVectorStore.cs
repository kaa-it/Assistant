using Microsoft.Data.Sqlite;
using System.Text.Json;

public class SqliteVectorStore(string dbPath = "index.db") : IVectorStore
{
    private string ConnectionString => $"Data Source={dbPath}";

    public async Task InitializeAsync()
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                chunk_id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                section TEXT,
                content TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                total_chunks INTEGER NOT NULL,
                strategy TEXT NOT NULL,
                indexed_at TEXT NOT NULL,
                embedding TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_strategy ON chunks(strategy);
            CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source);
        """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveChunksAsync(IEnumerable<IndexedChunk> chunks)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        using var transaction = conn.BeginTransaction();
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO chunks
                (chunk_id, source, title, section, content, chunk_index, total_chunks, strategy, indexed_at, embedding)
            VALUES
                ($chunk_id, $source, $title, $section, $content, $chunk_index, $total_chunks, $strategy, $indexed_at, $embedding)
        """;

        var pChunkId = cmd.Parameters.Add("$chunk_id", SqliteType.Text);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pSection = cmd.Parameters.Add("$section", SqliteType.Text);
        var pContent = cmd.Parameters.Add("$content", SqliteType.Text);
        var pChunkIndex = cmd.Parameters.Add("$chunk_index", SqliteType.Integer);
        var pTotalChunks = cmd.Parameters.Add("$total_chunks", SqliteType.Integer);
        var pStrategy = cmd.Parameters.Add("$strategy", SqliteType.Text);
        var pIndexedAt = cmd.Parameters.Add("$indexed_at", SqliteType.Text);
        var pEmbedding = cmd.Parameters.Add("$embedding", SqliteType.Text);

        foreach (var chunk in chunks)
        {
            pChunkId.Value = chunk.ChunkId;
            pSource.Value = chunk.Source;
            pTitle.Value = chunk.Title;
            pSection.Value = chunk.Section ?? (object)DBNull.Value;
            pContent.Value = chunk.Content;
            pChunkIndex.Value = chunk.ChunkIndex;
            pTotalChunks.Value = chunk.TotalChunks;
            pStrategy.Value = chunk.Strategy.ToString();
            pIndexedAt.Value = chunk.IndexedAt.ToString("O");
            pEmbedding.Value = JsonSerializer.Serialize(chunk.Embedding);
            await cmd.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    public async Task<IEnumerable<IndexedChunk>> SearchSimilarAsync(float[] queryEmbedding, int topK = 5, ChunkingStrategy? strategy = null)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        if (strategy.HasValue)
        {
            cmd.CommandText = "SELECT * FROM chunks WHERE strategy = $strategy";
            cmd.Parameters.AddWithValue("$strategy", strategy.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT * FROM chunks";
        }

        var chunks = new List<(IndexedChunk chunk, float similarity)>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var embeddingJson = reader.GetString(reader.GetOrdinal("embedding"));
            var embedding = JsonSerializer.Deserialize<float[]>(embeddingJson) ?? [];
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, embedding);

            var chunk = new IndexedChunk
            {
                ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Title = reader.GetString(reader.GetOrdinal("title")),
                Section = reader.IsDBNull(reader.GetOrdinal("section")) ? null : reader.GetString(reader.GetOrdinal("section")),
                Content = reader.GetString(reader.GetOrdinal("content")),
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
                TotalChunks = reader.GetInt32(reader.GetOrdinal("total_chunks")),
                Strategy = Enum.Parse<ChunkingStrategy>(reader.GetString(reader.GetOrdinal("strategy"))),
                IndexedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("indexed_at"))),
                Embedding = embedding
            };
            chunks.Add((chunk, similarity));
        }

        return chunks.OrderByDescending(c => c.similarity).Take(topK).Select(c => c.chunk);
    }

    public async Task<IEnumerable<(IndexedChunk chunk, float similarity)>> SearchSimilarWithScoresAsync(float[] queryEmbedding, int topK = 5, ChunkingStrategy? strategy = null)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        if (strategy.HasValue)
        {
            cmd.CommandText = "SELECT * FROM chunks WHERE strategy = $strategy";
            cmd.Parameters.AddWithValue("$strategy", strategy.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT * FROM chunks";
        }

        var chunks = new List<(IndexedChunk chunk, float similarity)>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var embeddingJson = reader.GetString(reader.GetOrdinal("embedding"));
            var embedding = JsonSerializer.Deserialize<float[]>(embeddingJson) ?? [];
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, embedding);

            var chunk = new IndexedChunk
            {
                ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Title = reader.GetString(reader.GetOrdinal("title")),
                Section = reader.IsDBNull(reader.GetOrdinal("section")) ? null : reader.GetString(reader.GetOrdinal("section")),
                Content = reader.GetString(reader.GetOrdinal("content")),
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
                TotalChunks = reader.GetInt32(reader.GetOrdinal("total_chunks")),
                Strategy = Enum.Parse<ChunkingStrategy>(reader.GetString(reader.GetOrdinal("strategy"))),
                IndexedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("indexed_at"))),
                Embedding = embedding
            };
            chunks.Add((chunk, similarity));
        }

        return chunks.OrderByDescending(c => c.similarity).Take(topK);
    }

    public async Task<long> GetChunkCountAsync(ChunkingStrategy? strategy = null)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        if (strategy.HasValue)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE strategy = $strategy";
            cmd.Parameters.AddWithValue("$strategy", strategy.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
