public class SimilarityThresholdFilter
{
    private readonly float _threshold;

    public float Threshold => _threshold;

    public SimilarityThresholdFilter(float threshold = 0.5f)
    {
        _threshold = threshold;
    }

    public List<(IndexedChunk chunk, float similarity)> Filter(
        List<(IndexedChunk chunk, float similarity)> candidates)
    {
        return candidates.Where(c => c.similarity >= _threshold).ToList();
    }
}
