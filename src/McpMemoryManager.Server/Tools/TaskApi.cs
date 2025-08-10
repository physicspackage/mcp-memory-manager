using McpMemoryManager.Server.Models;
using McpMemoryManager.Server.MemoryStore;

namespace McpMemoryManager.Server.Tools;

public sealed class TaskApi
{
    private readonly SqliteStore _store;
    public TaskApi(SqliteStore store) => _store = store;

    public Task<string> CreateTaskAsync(string title, string ns = "default")
        => _store.CreateMemoryAsync(content: title, type: "task", title: title, ns: ns);

    public async Task<IReadOnlyList<(string Id, string Title, string Status)>> ListTasksAsync(int limit = 50)
    {
        var items = await _store.ListMemoriesAsync(type: "task", limit: limit);
        return items.Select(i => (i.Id, i.Title ?? i.Content, "todo")).ToList();
    }

    public async Task<bool> UpdateStatusAsync(string id, string status, string? note = null)
    {
        // Update status in metadata
        var item = await _store.GetMemoryAsync(id);
        if (item is null || item.Type != "task") return false;
        var metadata = item.Metadata ?? new Dictionary<string, object>();
        metadata["status"] = status;
        await _store.UpdateMemoryAsync(id, metadata: metadata);

        if (!string.IsNullOrWhiteSpace(note))
        {
            await _store.CreateMemoryAsync(content: note!, type: "note", refs: new[] { id }, ns: item.Namespace);
        }
        return true;
    }

    public async Task<string> AddNoteAsync(string id, string note)
    {
        var item = await _store.GetMemoryAsync(id);
        var ns = item?.Namespace ?? "default";
        return await _store.CreateMemoryAsync(content: note, type: "note", refs: new[] { id }, ns: ns);
    }
}
