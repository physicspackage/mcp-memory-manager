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
}