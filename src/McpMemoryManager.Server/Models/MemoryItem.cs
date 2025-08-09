namespace McpMemoryManager.Server.Models;

public record MemoryItem(
    string Id,
    string AgentId,
    string Namespace,
    string Type,
    string? Title,
    string Content,
    Dictionary<string, object>? Metadata,
    List<string> Tags,
    List<string> Refs,
    double Importance,
    bool Pin,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt
);

public sealed record ScoredMemoryItem(MemoryItem Item, double Score)
{
    public string Id => Item.Id;
    public string Type => Item.Type;
    public string Namespace => Item.Namespace;
    public string Content => Item.Content;
    public DateTimeOffset CreatedAt => Item.CreatedAt;
}