using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Tools;
using Xunit;

namespace McpMemoryManager.Server.Tests;

public class MergeSummarizeTests
{
    [Fact]
    public async Task Merge_and_summarize_thread_via_tools()
    {
        await using var ts = await TestStore.CreateAsync();
        var memory = new MemoryApi(ts.Store);
        var tasks = new TaskApi(ts.Store);

        var a = await memory.CreateAsync("first", ns: "M");
        var b = await memory.CreateAsync("second", ns: "M");

        byte[] Frame(object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/json\r\n\r\n");
            var buf = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, buf, 0, header.Length);
            Buffer.BlockCopy(body, 0, buf, header.Length, body.Length);
            return buf;
        }

        JsonElement[] RunBatch(params object[] messages)
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
                    var bb = br.ReadByte();
                    headers.Add(bb);
                    if ((matched == 0 || matched == 2) && bb == (byte)'\r') matched++;
                    else if ((matched == 1 || matched == 3) && bb == (byte)'\n') matched++;
                    else matched = bb == (byte)'\r' ? 1 : 0;
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

        JsonElement ExtractJsonPart(JsonElement resultContent)
        {
            JsonElement part = resultContent;
            if (resultContent.ValueKind == JsonValueKind.Array)
            {
                try { part = resultContent.EnumerateArray().First(); }
                catch { /* fallback to original element */ }
            }
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

        var mergeMsg = new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "memory.merge", arguments = new { source_ids = new[] { a, b }, target_title = "merged" } } };
        var sumMsg = new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "memory.summarize_thread", arguments = new { source_ids = new[] { a, b }, style = "brief" } } };
        var res = RunBatch(mergeMsg, sumMsg);
        var mergedId = ExtractJsonPart(res[0].GetProperty("result").GetProperty("content")).GetProperty("id").GetString();
        var sumId = ExtractJsonPart(res[1].GetProperty("result").GetProperty("content")).GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(mergedId));
        Assert.False(string.IsNullOrWhiteSpace(sumId));
        var merged = await memory.GetAsync(mergedId!);
        var summary = await memory.GetAsync(sumId!);
        Assert.Contains(a, merged!.Refs);
        Assert.Contains(b, merged!.Refs);
        Assert.Equal("summary", summary!.Type);
    }
}
