using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class MemoryCrudTests
{
    [Fact]
    public async Task Update_archive_pin_and_delete()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);

        var id = await api.CreateAsync("content", type: "note", ns: "X");
        var ok = await api.UpdateAsync(id, title: "T", importance: 0.9, tags: new[] { "a", "b" });
        Assert.True(ok);
        var it = await api.GetAsync(id);
        Assert.Equal("T", it!.Title);
        Assert.Equal(0.9, it.Importance, 3);
        Assert.Contains("a", it.Tags);

        Assert.True(await api.UpdateAsync(id, archived: true));
        var (items, next) = await api.ListAdvancedAsync(ns: "X", archived: true, limit: 10);
        Assert.Contains(items, m => m.Id == id);
        Assert.Null(next);

        Assert.True(await api.UpdateAsync(id, pin: true));
        var got = await api.GetAsync(id);
        Assert.True(got!.Pin);

        var soft = await api.DeleteAsync(id, hard: false);
        Assert.Equal(1, soft);
        var removed = await api.GetAsync(id);
        Assert.NotNull(removed); // soft delete keeps row archived

        var hard = await api.DeleteAsync(id, hard: true);
        Assert.Equal(1, hard);
        var gone = await api.GetAsync(id);
        Assert.Null(gone);
    }

    [Fact]
    public async Task List_filters_and_cursor()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);
        for (int i = 0; i < 5; i++)
            await api.CreateAsync($"note {i}", type: "note", ns: "N", tags: new[] { i % 2 == 0 ? "even" : "odd" });

        var (first, cursor) = await api.ListAdvancedAsync(ns: "N", tags: new[] { "even" }, limit: 2);
        Assert.Equal(2, first.Count);
        Assert.NotNull(cursor);
        var (second, cursor2) = await api.ListAdvancedAsync(ns: "N", tags: new[] { "even" }, limit: 2, cursor: cursor);
        Assert.True(second.Count >= 1);
    }
}

