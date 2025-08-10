using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class ResourcesTests
{
    private static byte[] Frame(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/json\r\n\r\n");
        var buf = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        Buffer.BlockCopy(body, 0, buf, header.Length, body.Length);
        return buf;
    }

    private static JsonElement[] RunBatch(MemoryApi memory, TaskApi tasks, params object[] messages)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        foreach (var m in messages) input.Write(Frame(m));
        input.Position = 0;
        ToolHost.RunAsync(memory, tasks, input, output, CancellationToken.None).GetAwaiter().GetResult();
        output.Position = 0;
        var results = new List<JsonElement>();
        using var br = new BinaryReader(output, Encoding.UTF8, leaveOpen: true);
        while (output.Position < output.Length)
        {
            var headers = new List<byte>();
            int matched = 0;
            while (output.Position < output.Length && matched < 4)
            {
                var b = br.ReadByte();
                headers.Add(b);
                if ((matched == 0 || matched == 2) && b == (byte)'\r') matched++;
                else if ((matched == 1 || matched == 3) && b == (byte)'\n') matched++;
                else matched = b == (byte)'\r' ? 1 : 0;
            }
            var headerText = Encoding.ASCII.GetString(headers.ToArray());
            var lenLine = headerText.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
                                    .First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var len = int.Parse(lenLine.Substring("Content-Length:".Length).Trim());
            var body = br.ReadBytes(len);
            using var doc = JsonDocument.Parse(body);
            results.Add(doc.RootElement.Clone());
        }
        return results.ToArray();
    }

    [Fact]
    public async Task Resources_list_and_read()
    {
        await using var ts = await TestStore.CreateAsync();
        var memory = new MemoryApi(ts.Store);
        var tasks = new TaskApi(ts.Store);

        var id = await memory.CreateAsync("resource-content", ns: "R", type: "note");

        var init = new { jsonrpc = "2.0", id = 1, method = "resources/list", @params = new { } };
        var res = RunBatch(memory, tasks, init);
        var arr = res[0].GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
        Assert.NotEmpty(arr);
        var any = arr.First(r => r.GetProperty("uri").GetString()!.EndsWith(id));
        var uri = any.GetProperty("uri").GetString();
        var read = new { jsonrpc = "2.0", id = 2, method = "resources/read", @params = new { uri } };
        var res2 = RunBatch(memory, tasks, read);
        var text = res2[0].GetProperty("result").GetProperty("contents").EnumerateArray().First().GetProperty("text").GetString();
        Assert.Equal("resource-content", text);
    }
}
