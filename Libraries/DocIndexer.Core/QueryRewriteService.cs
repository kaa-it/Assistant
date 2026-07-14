public interface IQueryRewriteService
{
    Task<string> RewriteAsync(string query, CancellationToken ct = default);
}

public class HeuristicQueryRewriteService : IQueryRewriteService
{
    public Task<string> RewriteAsync(string query, CancellationToken ct = default)
    {
        var words = query.Split([' ', '\t', '\n', '?', '!', '.', ','], StringSplitOptions.RemoveEmptyEntries);
        var lowerWords = words.Select(w => w.ToLowerInvariant()).ToHashSet();

        var rewritten = query;

        if (!lowerWords.Contains("rust"))
            rewritten += " Rust";
        if (!lowerWords.Contains("pattern") && !lowerWords.Contains("idiom") && !lowerWords.Contains("anti-pattern"))
            rewritten += " pattern";

        return Task.FromResult(rewritten.Trim());
    }
}
