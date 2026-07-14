public enum ChatRole { User, Assistant }

public record ChatMessage(
    ChatRole Role,
    string Content,
    DateTime Timestamp,
    List<SourceReference>? Sources = null,
    List<Citation>? Citations = null
);
