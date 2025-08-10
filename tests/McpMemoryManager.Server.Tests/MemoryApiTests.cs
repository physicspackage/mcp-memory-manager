using McpMemoryManager.Server.Tools;
using McpMemoryManager.Server.Models;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class MemoryApiTests
{
    [Fact]
    public async Task Create_and_get_memory()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);

        var id = await api.CreateAsync(content: "hello world", type: "note", ns: "test");
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(32, id.Length); // Guid N format

        var item = await api.GetAsync(id);
        Assert.NotNull(item);
        Assert.Equal("hello world", item!.Content);
        Assert.Equal("note", item.Type);
        Assert.Equal("test", item.Namespace);
    }

    [Fact]
    public async Task List_filters_by_type_and_namespace()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);

        await api.CreateAsync("a1", type: "note", ns: "A");
        await api.CreateAsync("a2", type: "log", ns: "A");
        await api.CreateAsync("b1", type: "note", ns: "B");

        var aNotes = await api.ListAsync(ns: "A", type: "note");
        Assert.Single(aNotes);
        Assert.Equal("a1", aNotes[0].Content);

        var notes = await api.ListAsync(type: "note");
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public async Task Search_finds_inserted_content()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);

        var id = await api.CreateAsync("zebra aplomb", type: "note", ns: "S");

        var hits = await api.SearchAsync("zebra", ns: "S", limit: 10);
        Assert.Contains(hits, h => h.Id == id);
    }

    [Fact]
    public async Task Cleanup_expire_removes_only_expired()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);

        var pastId = await api.CreateAsync("old", expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var futureId = await api.CreateAsync("new", expiresAt: DateTimeOffset.UtcNow.AddDays(1));

        var removed = await api.CleanupExpireAsync();
        Assert.True(removed >= 1);

        var past = await api.GetAsync(pastId);
        var future = await api.GetAsync(futureId);
        Assert.Null(past);
        Assert.NotNull(future);
    }
}

