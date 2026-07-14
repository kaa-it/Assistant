public class EnhancedRagPipeline
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IQueryRewriteService? _rewriteService;
    private readonly SimilarityThresholdFilter _thresholdFilter;
    private readonly HeuristicReranker _reranker;
    private readonly UnknownThresholdHandler _thresholdHandler;
    private readonly int _topKPre;
    private readonly int _topKPost;
    private readonly bool _enableRewrite;
    private readonly bool _enableRerank;

    public EnhancedRagPipeline(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IQueryRewriteService? rewriteService = null)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _rewriteService = rewriteService;
        _thresholdFilter = new SimilarityThresholdFilter(GetEnvFloat("RAG_SIMILARITY_THRESHOLD", 0.5f));
        _reranker = new HeuristicReranker();
        _thresholdHandler = new UnknownThresholdHandler(
            GetEnvFloat("RAG_UNKNOWN_THRESHOLD", 0.45f),
            GetEnvFloat("RAG_HIGH_CONFIDENCE_THRESHOLD", 0.65f));
        _topKPre = GetEnvInt("RAG_TOP_K_PRE", 10);
        _topKPost = GetEnvInt("RAG_TOP_K_POST", 3);
        _enableRewrite = GetEnvBool("RAG_ENABLE_REWRITE", true);
        _enableRerank = GetEnvBool("RAG_ENABLE_RERANK", true);
    }

    public async Task<RagResult> ExecuteAsync(
        string question,
        RagPipelineMode mode,
        CancellationToken ct = default)
    {
        var isEnhanced = mode == RagPipelineMode.FullPipeline || mode == RagPipelineMode.CitationEnforced;

        var originalQuestion = question;
        string? rewrittenQuestion = null;

        if (isEnhanced && _enableRewrite && _rewriteService != null)
        {
            rewrittenQuestion = await _rewriteService.RewriteAsync(question, ct);
            question = rewrittenQuestion;
        }

        var queryEmbedding = await _embeddingService.GenerateQueryEmbeddingAsync(question, ct);

        var searchK = mode == RagPipelineMode.Baseline ? _topKPost : _topKPre;
        var rawResults = await _vectorStore.SearchSimilarWithScoresAsync(queryEmbedding, searchK, ChunkingStrategy.Structural);
        var rawResultsList = rawResults.ToList();

        List<(IndexedChunk chunk, float similarity)> filtered = rawResultsList;
        var filteredCount = 0;
        if (mode != RagPipelineMode.Baseline)
        {
            filtered = _thresholdFilter.Filter(rawResultsList);
            filteredCount = filtered.Count;
            if (filtered.Count == 0)
                filtered = rawResultsList.Take(_topKPost).ToList();
        }

        List<ScoredChunk> ranked;
        if ((mode == RagPipelineMode.WithReranker || mode == RagPipelineMode.FullPipeline || mode == RagPipelineMode.CitationEnforced) && _enableRerank)
            ranked = _reranker.Rerank(question, filtered);
        else
            ranked = filtered.Select(f => new ScoredChunk(f.chunk, f.similarity, f.similarity, 0)).ToList();

        var finalChunks = ranked.Take(_topKPost).ToList();

        var maxScore = finalChunks.Count > 0 ? (float)finalChunks.Max(c => c.FinalScore) : 0f;
        var assessment = mode == RagPipelineMode.CitationEnforced
            ? _thresholdHandler.AssessRelevance(finalChunks)
            : new RelevanceAssessment(CanAnswer: true, MaxSimilarity: maxScore, Reason: null, Confidence: ConfidenceLevel.High);

        if (!assessment.CanAnswer)
        {
            return new RagResult(
                Context: "", Sources: [], originalQuestion, rewrittenQuestion, finalChunks,
                mode, _thresholdFilter.Threshold, _topKPre, _topKPost, filteredCount,
                assessment.MaxSimilarity, assessment.Confidence,
                IsUnknown: true, UnknownReason: assessment.Reason
            );
        }

        var contextParts = finalChunks.Select((c, i) =>
        {
            var sectionInfo = c.Chunk.Section != null ? $"[Section: {c.Chunk.Section}]" : "";
            return $"--- Source {i + 1}: {c.Chunk.Title} {sectionInfo} (score: {c.FinalScore:F3}) ---\n{c.Chunk.Content}";
        });

        var context = string.Join("\n\n", contextParts);

        var sources = finalChunks
            .Select(c => c.Chunk.Source)
            .Select(p =>
            {
                var normalized = p.Replace('\\', '/');
                var idx = normalized.IndexOf("patterns/", StringComparison.OrdinalIgnoreCase);
                return idx >= 0 ? normalized[idx..] : Path.GetFileName(p);
            })
            .Distinct()
            .ToArray();

        return new RagResult(
            context, sources, originalQuestion, rewrittenQuestion, finalChunks,
            mode, _thresholdFilter.Threshold, _topKPre, _topKPost, filteredCount,
            assessment.MaxSimilarity, assessment.Confidence,
            !assessment.CanAnswer, assessment.Reason
        );
    }

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;
    private static float GetEnvFloat(string name, float defaultValue) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;
    private static bool GetEnvBool(string name, bool defaultValue) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;
}
