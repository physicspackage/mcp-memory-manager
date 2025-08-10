using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class TaskApiTests
{
    [Fact]
    public async Task Create_and_list_tasks()
    {
        await using var ts = await TestStore.CreateAsync();
        var tasksApi = new TaskApi(ts.Store);

        var id = await tasksApi.CreateTaskAsync("Write tests", ns: "proj");
        Assert.False(string.IsNullOrWhiteSpace(id));

        var tasks = await tasksApi.ListTasksAsync(limit: 10);
        Assert.Single(tasks);
        Assert.Equal(id, tasks[0].Id);
        Assert.Equal("Write tests", tasks[0].Title);
        Assert.Equal("todo", tasks[0].Status);
    }
}

