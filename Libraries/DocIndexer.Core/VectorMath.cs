public static class VectorMath
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Embedding dimensions mismatch: {a.Length} vs {b.Length}");

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return magnitude == 0 ? 0 : dot / magnitude;
    }
}
