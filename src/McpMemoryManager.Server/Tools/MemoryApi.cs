using McpMemoryManager.Server.MemoryStore;
using McpMemoryManager.Server.Models;

namespace McpMemoryManager.Server.Tools;

/// <summary>
/// Thin application layer used by both the CLI loop and (later) the MCP tool host.
/// NOTE: This class should only call methods on SqliteStore and must not reference
/// internal store helpers like Open() or Map().
/// </summary>
public sealed class MemoryApi
{
    private readonly SqliteStore _store;
    public MemoryApi(SqliteStore store) => _store = store;

    public Task<string> CreateAsync(
        string content,
        string type = "note",
        string? title = null,
        string agentId = "",
        string ns = "default",
        Dictionary<string, object>? metadata = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? refs = null,
        double importance = 0.3,
        bool pin = false,
        DateTimeOffset? expiresAt = null)
        => _store.CreateMemoryAsync(content, type, title, agentId, ns, metadata, tags, refs, importance, pin, expiresAt);

    public Task<MemoryItem?> GetAsync(string id) => _store.GetMemoryAsync(id);

    public Task<IReadOnlyList<MemoryItem>> ListAsync(string? ns = null, string? type = null, int limit = 50)
        => _store.ListMemoriesAsync(ns, type, limit);

    public Task<IReadOnlyList<ScoredMemoryItem>> SearchAsync(string query, string? ns = null, int limit = 20)
        => _store.SearchAsync(query, ns, limit);

    public Task<int> CleanupExpireAsync(string? ns = null) => _store.CleanupExpireAsync(ns);

    public Task<bool> UpdateAsync(
        string id,
        string? content = null,
        string? title = null,
        Dictionary<string, object>? metadata = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? refs = null,
        double? importance = null,
        bool? pin = null,
        bool? archived = null,
        DateTimeOffset? expiresAt = null)
        => _store.UpdateMemoryAsync(id, content, title, metadata, tags, refs, importance, pin, archived, expiresAt);

    public Task<int> DeleteAsync(string id, bool hard = false) => _store.DeleteMemoryAsync(id, hard);

    public Task<(IReadOnlyList<MemoryItem> Items, string? NextCursor)> ListAdvancedAsync(
        string? agentId = null,
        string? ns = null,
        IEnumerable<string>? types = null,
        IEnumerable<string>? tags = null,
        bool? pinned = null,
        bool? archived = null,
        DateTimeOffset? before = null,
        DateTimeOffset? after = null,
        int limit = 50,
        string? cursor = null)
        => _store.ListMemoriesAdvancedAsync(agentId, ns, types, tags, pinned, archived, before, after, limit, cursor);

    public Task<IReadOnlyList<MemoryItem>> ExportAsync(string? ns = null) => _store.ExportMemoriesAsync(ns);
    public Task<int> UpsertAsync(MemoryItem item) => _store.UpsertMemoryAsync(item);
}
