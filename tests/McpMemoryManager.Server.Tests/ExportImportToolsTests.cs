using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class ExportImportToolsTests
{
    private static JsonElement ExtractJsonPart(JsonElement resultContent)
    {
        JsonElement part = resultContent;
        if (resultContent.ValueKind == JsonValueKind.Array)
            part = resultContent.EnumerateArray().First();
        if (part.ValueKind == JsonValueKind.Object)
        {
            if (part.TryGetProperty("json", out var jsonEl))
                return jsonEl;
            if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(textEl.GetString()!);
                return doc.RootElement.Clone();
            }
        }
        return part;
    }
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
            // Read headers until CRLFCRLF
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
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) results.Add(el.Clone());
            }
            else
            {
                results.Add(root.Clone());
            }
        }
        return results.ToArray();
    }

    [Fact]
    public async Task Export_dump_and_import_roundtrip()
    {
        await using var ts1 = await TestStore.CreateAsync();
        var memory1 = new MemoryApi(ts1.Store);
        var tasks1 = new TaskApi(ts1.Store);

        // Create a memory via tools
        var create = new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "memory.create", arguments = new { content = "exp", ns = "Z" } } };
        var dump = new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "export.dump", arguments = new { ns = "Z" } } };
        var res = RunBatch(memory1, tasks1, create, dump);
        var content0 = res[0].GetProperty("result").GetProperty("content");
        var createdId = ExtractJsonPart(content0).GetProperty("id").GetString();
        var content1 = res[1].GetProperty("result").GetProperty("content");
        var ndjson = ExtractJsonPart(content1).GetProperty("ndjson").GetString();
        Assert.False(string.IsNullOrEmpty(createdId));
        Assert.Contains(createdId, ndjson);

        // Import into a fresh store
        await using var ts2 = await TestStore.CreateAsync();
        var memory2 = new MemoryApi(ts2.Store);
        var tasks2 = new TaskApi(ts2.Store);
        var importMsg = new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "export.import", arguments = new { ndjson } } };
        var getMsg = new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "memory.get", arguments = new { id = createdId } } };
        var res2 = RunBatch(memory2, tasks2, importMsg, getMsg);
        var content2 = res2[0].GetProperty("result").GetProperty("content");
        var ok = ExtractJsonPart(content2).GetProperty("upserted").GetInt32();
        Assert.True(ok >= 1);
        var content3 = res2[1].GetProperty("result").GetProperty("content");
        var item = ExtractJsonPart(content3).GetProperty("item");
        Assert.Equal("exp", item.GetProperty("Content").GetString());
    }
}
