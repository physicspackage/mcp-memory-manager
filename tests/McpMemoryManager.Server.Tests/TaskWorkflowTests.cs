using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class TaskWorkflowTests
{
    [Fact]
    public async Task Update_status_and_add_note()
    {
        await using var ts = await TestStore.CreateAsync();
        var tasks = new TaskApi(ts.Store);
        var memory = new MemoryApi(ts.Store);

        var id = await tasks.CreateTaskAsync("Ship feature", ns: "W");
        var ok = await tasks.UpdateStatusAsync(id, "in_progress", note: "Starting now");
        Assert.True(ok);

        var taskItem = await memory.GetAsync(id);
        Assert.NotNull(taskItem);
        Assert.True(taskItem!.Metadata != null && taskItem.Metadata.ContainsKey("status"));
        Assert.Equal("in_progress", taskItem.Metadata!["status"].ToString());

        var (items, _) = await memory.ListAdvancedAsync(ns: "W", types: new[] { "note" }, tags: null, limit: 10);
        Assert.Contains(items, i => i.Refs.Contains(id));
    }
}

