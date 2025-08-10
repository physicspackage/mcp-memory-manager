using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class ExportImportTests
{
    [Fact]
    public async Task Export_and_import_ndjson()
    {
        await using var ts = await TestStore.CreateAsync();
        var api = new MemoryApi(ts.Store);
        var id = await api.CreateAsync("alpha", type: "note", ns: "E");

        var items = await api.ExportAsync("E");
        Assert.NotEmpty(items);
        var sb = new StringBuilder();
        foreach (var it in items)
        {
            var json = JsonSerializer.Serialize(it);
            sb.AppendLine(json);
        }
        var ndjson = sb.ToString();

        await using var ts2 = await TestStore.CreateAsync();
        var api2 = new MemoryApi(ts2.Store);
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = JsonSerializer.Deserialize<McpMemoryManager.Server.Models.MemoryItem>(line)!;
            await api2.UpsertAsync(item);
        }
        var got = await api2.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal("alpha", got!.Content);
    }
}

