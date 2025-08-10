using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class ToolHostTests
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

    private static IEnumerable<JsonElement> ReadResponses(MemoryStream output)
    {
        output.Position = 0;
        using var reader = new BinaryReader(output, Encoding.UTF8, leaveOpen: true);
        var list = new List<JsonElement>();
        while (output.Position < output.Length)
        {
            // Read headers
            var headers = new List<byte>();
            int matched = 0;
            while (output.Position < output.Length && matched < 4)
            {
                var b = reader.ReadByte();
                headers.Add(b);
                if ((matched == 0 || matched == 2) && b == (byte) '\r') matched++;
                else if ((matched == 1 || matched == 3) && b == (byte) '\n') matched++;
                else matched = b == (byte) '\r' ? 1 : 0;
            }
            var headerText = Encoding.ASCII.GetString(headers.ToArray());
            var lenLine = headerText.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
                                    .First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var len = int.Parse(lenLine.Substring("Content-Length:".Length).Trim());
            var body = reader.ReadBytes(len);
            using var doc = JsonDocument.Parse(body);
            list.Add(doc.RootElement.Clone());
        }
        return list;
    }

    [Fact]
    public async Task Initialize_list_and_create_roundtrip()
    {
        await using var ts = await TestStore.CreateAsync();
        var memory = new MemoryApi(ts.Store);
        var tasks = new TaskApi(ts.Store);

        using var input = new MemoryStream();
        using var output = new MemoryStream();

        // Queue initialize, tools/list, and a create call
        var init = Frame(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
        var list = Frame(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var create = Frame(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "memory.create", arguments = new { content = "hello" } } });
        input.Write(init);
        input.Write(list);
        input.Write(create);
        input.Position = 0;

        await ToolHost.RunAsync(memory, tasks, input, output, CancellationToken.None);

        var responses = ReadResponses(output).ToArray();
        Assert.Equal(3, responses.Length);

        // Check initialize
        Assert.Equal("2.0", responses[0].GetProperty("jsonrpc").GetString());
        Assert.Equal(1, responses[0].GetProperty("id").GetInt32());
        Assert.Equal("mcp-memory-manager", responses[0].GetProperty("result").GetProperty("server").GetProperty("name").GetString());

        // Check tool listing has at least memory.create
        var tools = responses[1].GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Contains("memory.create", tools);

        // Check create result returns id
        var id = responses[2].GetProperty("result").GetProperty("content").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        var item = await memory.GetAsync(id!);
        Assert.NotNull(item);
        Assert.Equal("hello", item!.Content);
    }
}

