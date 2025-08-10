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

    public async Task<bool> AddTagsAsync(string id, IEnumerable<string> add)
    {
        var item = await _store.GetMemoryAsync(id);
        if (item is null) return false;
        var set = new HashSet<string>(item.Tags ?? new List<string>());
        foreach (var t in add) set.Add(t);
        return await _store.UpdateMemoryAsync(id, tags: set);
    }

    public async Task<bool> RemoveTagsAsync(string id, IEnumerable<string> remove)
    {
        var item = await _store.GetMemoryAsync(id);
        if (item is null) return false;
        var set = new HashSet<string>(item.Tags ?? new List<string>());
        foreach (var t in remove) set.Remove(t);
        return await _store.UpdateMemoryAsync(id, tags: set);
    }

    public async Task<bool> AddRefsAsync(string id, IEnumerable<string> add)
    {
        var item = await _store.GetMemoryAsync(id);
        if (item is null) return false;
        var set = new HashSet<string>(item.Refs ?? new List<string>());
        foreach (var r in add) set.Add(r);
        return await _store.UpdateMemoryAsync(id, refs: set);
    }

    public async Task<bool> RemoveRefsAsync(string id, IEnumerable<string> remove)
    {
        var item = await _store.GetMemoryAsync(id);
        if (item is null) return false;
        var set = new HashSet<string>(item.Refs ?? new List<string>());
        foreach (var r in remove) set.Remove(r);
        return await _store.UpdateMemoryAsync(id, refs: set);
    }

    public async Task<bool> LinkAsync(string fromId, string toId, string? relation = null)
    {
        var item = await _store.GetMemoryAsync(fromId);
        if (item is null) return false;
        var refs = new HashSet<string>(item.Refs ?? new List<string>()) { toId };
        Dictionary<string, object>? meta = item.Metadata is null ? new Dictionary<string, object>() : new Dictionary<string, object>(item.Metadata);
        if (!string.IsNullOrWhiteSpace(relation))
        {
            // Store simple relation map under metadata.relations[toId] = relation
            if (!meta.TryGetValue("relations", out var relObj) || relObj is null)
            {
                meta["relations"] = new Dictionary<string, string>();
            }
            var map = meta["relations"] as Dictionary<string, string> ?? new Dictionary<string, string>();
            map[toId] = relation!;
            meta["relations"] = map;
        }
        return await _store.UpdateMemoryAsync(fromId, refs: refs, metadata: meta);
    }

    public async Task<string> SummarizeAsync(string id, string? style = null)
    {
        var item = await _store.GetMemoryAsync(id);
        if (item is null) throw new InvalidOperationException("Not found");
        var text = item.Content ?? string.Empty;
        var max = 280;
        var snippet = text.Length <= max ? text : text[..max] + "…";
        if (!string.IsNullOrWhiteSpace(style))
        {
            snippet = $"[{style}] {snippet}";
        }
        var title = item.Title is null ? $"Summary of {id[..8]}" : $"Summary: {item.Title}";
        var summaryId = await _store.CreateMemoryAsync(content: snippet, type: "summary", title: title, ns: item.Namespace, refs: new [] { id });
        return summaryId;
    }

    public async Task<string> MergeAsync(IEnumerable<string> sourceIds, string? targetTitle = null, string? ns = null)
    {
        var ids = sourceIds.ToList();
        if (ids.Count == 0) throw new ArgumentException("sourceIds required");
        var items = new List<MemoryItem>();
        foreach (var id in ids)
        {
            var m = await _store.GetMemoryAsync(id);
            if (m != null) items.Add(m);
        }
        if (items.Count == 0) throw new InvalidOperationException("No valid sources");
        var nsFinal = ns ?? items[0].Namespace;
        var title = targetTitle ?? $"Merge of {items.Count} items";
        var content = string.Join("\n\n---\n\n", items.Select(i => i.Content));
        var newId = await _store.CreateMemoryAsync(content: content, type: "note", title: title, ns: nsFinal, refs: ids);
        return newId;
    }

    public async Task<string> SummarizeThreadAsync(IEnumerable<string> sourceIds, string? style = null)
    {
        var ids = sourceIds.ToList();
        if (ids.Count == 0) throw new ArgumentException("sourceIds required");
        var parts = new List<string>();
        foreach (var id in ids)
        {
            var m = await _store.GetMemoryAsync(id);
            if (m != null)
                parts.Add(m.Content);
        }
        var text = string.Join(" \u2022 ", parts);
        var max = 500;
        var snippet = text.Length <= max ? text : text[..max] + "…";
        if (!string.IsNullOrWhiteSpace(style)) snippet = $"[{style}] {snippet}";
        var nsFinal = (await _store.GetMemoryAsync(ids[0]))?.Namespace ?? "default";
        var sumId = await _store.CreateMemoryAsync(content: snippet, type: "summary", title: $"Thread summary ({ids.Count})", ns: nsFinal, refs: ids);
        return sumId;
    }
}
