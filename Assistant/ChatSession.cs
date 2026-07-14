public class ChatSession
{
    public List<ChatMessage> History { get; } = [];
    public string? SessionId { get; }

    public ChatSession()
    {
        SessionId = Guid.NewGuid().ToString("N")[..12];
    }

    public void AddUserMessage(string content)
    {
        History.Add(new ChatMessage(
            ChatRole.User,
            content,
            DateTime.UtcNow
        ));
    }

    public void AddAssistantMessage(CitationAnswer answer)
    {
        History.Add(new ChatMessage(
            ChatRole.Assistant,
            answer.Answer,
            DateTime.UtcNow,
            answer.Sources.Count > 0 ? answer.Sources : null,
            answer.Citations.Count > 0 ? answer.Citations : null
        ));
    }

    public string GetHistoryContext(int maxMessages = 6)
    {
        var history = History.AsEnumerable();
        if (History.Count > 0 && History[^1].Role == ChatRole.User)
            history = history.Take(History.Count - 1);

        var recent = history.TakeLast(maxMessages).ToList();
        var lines = new List<string>();

        foreach (var msg in recent)
        {
            var role = msg.Role == ChatRole.User ? "User" : "Assistant";
            lines.Add($"{role}: {msg.Content}");
        }

        return string.Join("\n", lines);
    }
}
