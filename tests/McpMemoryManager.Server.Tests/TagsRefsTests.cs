using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class TagsRefsTests
{
    [Fact]
    public async Task Add_and_remove_tags_refs()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);
        var id = await api.CreateAsync("c", type: "note", ns: "T", tags: new [] { "x" }, refs: new string[0]);

        Assert.True(await api.AddTagsAsync(id, new [] { "y", "z" }));
        var afterAdd = await api.GetAsync(id);
        Assert.Contains("x", afterAdd!.Tags);
        Assert.Contains("y", afterAdd!.Tags);

        Assert.True(await api.RemoveTagsAsync(id, new [] { "x" }));
        var afterRemove = await api.GetAsync(id);
        Assert.DoesNotContain("x", afterRemove!.Tags);

        Assert.True(await api.AddRefsAsync(id, new [] { "r1", "r2" }));
        var afterRefs = await api.GetAsync(id);
        Assert.Contains("r1", afterRefs!.Refs);

        Assert.True(await api.RemoveRefsAsync(id, new [] { "r1" }));
        var afterRefsRemove = await api.GetAsync(id);
        Assert.DoesNotContain("r1", afterRefsRemove!.Refs);
    }
}

