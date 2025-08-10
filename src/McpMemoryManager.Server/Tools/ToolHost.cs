using System.Text;
using System.Text.Json;
using McpMemoryManager.Server.Models;

namespace McpMemoryManager.Server.Tools;

public static class ToolHost
{
    public static Task RunAsync(MemoryApi memory, TaskApi tasks)
        => RunAsync(memory, tasks, Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);

    public static async Task RunAsync(MemoryApi memory, TaskApi tasks, Stream input, Stream output, CancellationToken cancel)
    {
        var tools = GetTools();
        while (!cancel.IsCancellationRequested)
        {
            var reqBytes = await ReadFramedAsync(input, cancel);
            if (reqBytes is null) break; // EOF
            try
            {
                using var doc = JsonDocument.Parse(reqBytes);
                var root = doc.RootElement;
                var hasId = root.TryGetProperty("id", out var idEl);
                var id = hasId ? idEl.Clone() : default;
                var method = root.GetProperty("method").GetString();

                switch (method)
                {
                    case "initialize":
                        await WriteFramedAsync(output, new
                        {
                            jsonrpc = "2.0",
                            id,
                            result = new
                            {
                                protocolVersion = "2024-11-05",
                                server = new { name = "mcp-memory-manager", version = "0.1.0" },
                                capabilities = new { tools = new { } }
                            }
                        }, cancel);
                        break;

                    case "tools/list":
                        await WriteFramedAsync(output, new { jsonrpc = "2.0", id, result = new { tools } }, cancel);
                        break;

                    case "resources/list":
                    {
                        var (items, _) = await memory.ListAdvancedAsync(limit: 50);
                        var resources = items.Select(i => new {
                            uri = $"mem://{i.Namespace}/{i.Id}",
                            name = i.Title ?? (i.Content.Length > 30 ? i.Content[..30] + "…" : i.Content),
                            description = i.Type,
                            mimeType = "text/plain"
                        }).ToArray();
                        await WriteFramedAsync(output, new { jsonrpc = "2.0", id, result = new { resources } }, cancel);
                        break;
                    }

                    case "resources/read":
                    {
                        var @params = root.GetProperty("params");
                        var uri = @params.GetProperty("uri").GetString()!;
                        if (!uri.StartsWith("mem://"))
                        {
                            await WriteFramedAsync(output, new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Unsupported URI" } }, cancel);
                            break;
                        }
                        var path = uri.Substring("mem://".Length);
                        var idx = path.IndexOf('/');
                        if (idx <= 0)
                        {
                            await WriteFramedAsync(output, new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Invalid URI" } }, cancel);
                            break;
                        }
                        var ns = path.Substring(0, idx);
                        var idPart = path.Substring(idx + 1);
                        var item = await memory.GetAsync(idPart);
                        if (item is null || item.Namespace != ns)
                        {
                            await WriteFramedAsync(output, new { jsonrpc = "2.0", id, error = new { code = -32004, message = "Not found" } }, cancel);
                            break;
                        }
                        var contents = new [] { new { uri, mimeType = "text/plain", text = item.Content } };
                        await WriteFramedAsync(output, new { jsonrpc = "2.0", id, result = new { contents } }, cancel);
                        break;
                    }

                    case "tools/call":
                    {
                        var @params = root.GetProperty("params");
                        var name = @params.GetProperty("name").GetString()!;
                        var args = @params.TryGetProperty("arguments", out var a) ? a : default;
                        var result = await CallToolAsync(name, args, memory, tasks);
                        await WriteFramedAsync(output, new { jsonrpc = "2.0", id, result = new { content = result } }, cancel);
                        break;
                    }

                    default:
                        await WriteFramedAsync(output, new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } }, cancel);
                        break;
                }
            }
            catch (Exception ex)
            {
                await WriteFramedAsync(output, new { jsonrpc = "2.0", id = (JsonElement?)null, error = new { code = -32603, message = ex.Message } }, cancel);
            }
        }
    }

    private static object[] GetTools() => new object[]
    {
        new {
            name = "memory.create",
            description = "Create a memory item",
            inputSchema = new {
                type = "object",
                properties = new {
                    content = new { type = "string", description = "Primary content of the memory." },
                    type = new { type = "string",  description = "Type/category of memory.", @default = "note" },
                    title = new { type = "string",  description = "Optional title.", @nullable = true },
                    agentId = new { type = "string",  description = "Originating agent id.", @default = "" },
                    ns = new { type = "string",  description = "Namespace bucket.", @default = "default" },
                    metadata = new { type = "object", description = "Arbitrary key/value metadata.", additionalProperties = true, @nullable = true },
                    tags = new { type = "array", description = "List of tags.", items = new { type = "string" }, @nullable = true },
                    refs = new { type = "array", description = "List of references (ids/urls).", items = new { type = "string" }, @nullable = true },
                    importance = new { type = "number", description = "Importance score 0-1.", @default = 0.3 },
                    pin = new { type = "boolean", description = "Pinned flag.", @default = false },
                    expiresAt = new { type = "string",  description = "ISO-8601 expiry timestamp.", @nullable = true }
                },
                required = new [] { "content" }
            },
            examples = new [] { new { arguments = new { content = "Buy milk", type = "note", tags = new [] { "groceries" } } } }
        },
        new {
            name = "memory.get",
            description = "Get a memory item by id",
            inputSchema = new { type = "object", properties = new { id = new { type = "string", description = "Memory id" } }, required = new [] { "id" } }
        },
        new {
            name = "memory.update",
            description = "Update fields of a memory (partial)",
            inputSchema = new {
                type = "object",
                required = new [] { "id" },
                properties = new {
                    id = new { type = "string" },
                    content = new { type = "string", @nullable = true },
                    title = new { type = "string", @nullable = true },
                    metadata = new { type = "object", additionalProperties = true, @nullable = true },
                    tags = new { type = "array", items = new { type = "string" }, @nullable = true },
                    refs = new { type = "array", items = new { type = "string" }, @nullable = true },
                    importance = new { type = "number", @nullable = true },
                    pin = new { type = "boolean", @nullable = true },
                    archived = new { type = "boolean", @nullable = true },
                    expiresAt = new { type = "string", @nullable = true }
                }
            }
        },
        new {
            name = "memory.delete",
            description = "Delete a memory (soft by default)",
            inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" }, hard = new { type = "boolean", @default = false } } }
        },
        new { name = "memory.archive", description = "Archive a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.unarchive", description = "Unarchive a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.pin", description = "Pin a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.unpin", description = "Unpin a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new {
            name = "memory.list",
            description = "List recent memories",
            inputSchema = new {
                type = "object",
                properties = new {
                    agentId = new { type = "string", @nullable = true },
                    ns = new { type = "string", @nullable = true },
                    types = new { type = "array", items = new { type = "string" }, @nullable = true },
                    tags = new { type = "array", items = new { type = "string" }, @nullable = true },
                    pinned = new { type = "boolean", @nullable = true },
                    archived = new { type = "boolean", @nullable = true },
                    before = new { type = "string", @nullable = true },
                    after = new { type = "string", @nullable = true },
                    limit = new { type = "integer", @default = 50 },
                    cursor = new { type = "string", @nullable = true }
                }
            }
        },
        new {
            name = "memory.search",
            description = "Search memories via FTS5",
            inputSchema = new { type = "object", properties = new { query = new { type = "string", description = "Search query" }, ns = new { type = "string", description = "Namespace filter", @nullable = true }, limit = new { type = "integer", description = "Max items", @default = 20 } }, required = new [] { "query" } },
            examples = new [] { new { arguments = new { query = "zebra" } } }
        },
        new {
            name = "memory.cleanup",
            description = "Delete expired memories",
            inputSchema = new { type = "object", properties = new { ns = new { type = "string", description = "Namespace filter", @nullable = true } } }
        },
        new { name = "memory.tags.add", description = "Add tags to a memory", inputSchema = new { type = "object", required = new [] { "id", "tags" }, properties = new { id = new { type = "string" }, tags = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.tags.remove", description = "Remove tags from a memory", inputSchema = new { type = "object", required = new [] { "id", "tags" }, properties = new { id = new { type = "string" }, tags = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.refs.add", description = "Add refs to a memory", inputSchema = new { type = "object", required = new [] { "id", "refs" }, properties = new { id = new { type = "string" }, refs = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.refs.remove", description = "Remove refs from a memory", inputSchema = new { type = "object", required = new [] { "id", "refs" }, properties = new { id = new { type = "string" }, refs = new { type = "array", items = new { type = "string" } } } } },
        new {
            name = "task.create",
            description = "Create a task (stored as memory of type 'task')",
            inputSchema = new { type = "object", properties = new { title = new { type = "string", description = "Task title" }, ns = new { type = "string", description = "Namespace", @default = "default" } }, required = new [] { "title" } },
            examples = new [] { new { arguments = new { title = "Write tests", ns = "proj" } } }
        },
        new { name = "task.update_status", description = "Update task status", inputSchema = new { type = "object", required = new [] { "id", "status" }, properties = new { id = new { type = "string" }, status = new { type = "string" }, note = new { type = "string", @nullable = true } } } },
        new { name = "task.add_note", description = "Attach a note to a task", inputSchema = new { type = "object", required = new [] { "id", "note" }, properties = new { id = new { type = "string" }, note = new { type = "string" } } } },
        new {
            name = "task.list",
            description = "List tasks",
            inputSchema = new { type = "object", properties = new { limit = new { type = "integer", description = "Max items", @default = 50 } } }
        },
        new {
            name = "export.dump",
            description = "Export memories as NDJSON",
            inputSchema = new { type = "object", properties = new { ns = new { type = "string", @nullable = true } } }
        },
        new {
            name = "export.import",
            description = "Import NDJSON memories (upsert by id)",
            inputSchema = new { type = "object", required = new [] { "ndjson" }, properties = new { ndjson = new { type = "string" } } }
        }
    };

    private static async Task<object> CallToolAsync(string name, JsonElement args, MemoryApi memory, TaskApi tasks)
    {
        switch (name)
        {
            case "memory.create":
                return new { id = await memory.CreateAsync(
                    content: GetString(args, "content")!,
                    type: GetString(args, "type") ?? "note",
                    title: GetString(args, "title"),
                    agentId: GetString(args, "agentId") ?? string.Empty,
                    ns: GetString(args, "ns") ?? "default",
                    metadata: GetDict(args, "metadata"),
                    tags: GetStringArray(args, "tags"),
                    refs: GetStringArray(args, "refs"),
                    importance: GetDouble(args, "importance") ?? 0.3,
                    pin: GetBool(args, "pin") ?? false,
                    expiresAt: GetDateTimeOffset(args, "expiresAt")
                ) };

            case "memory.get":
            {
                var id = GetString(args, "id")!;
                var m = await memory.GetAsync(id);
                return new { item = m };
            }

            case "memory.update":
                return new { ok = await memory.UpdateAsync(
                    id: GetString(args, "id")!,
                    content: GetString(args, "content"),
                    title: GetString(args, "title"),
                    metadata: GetDict(args, "metadata"),
                    tags: GetStringArray(args, "tags"),
                    refs: GetStringArray(args, "refs"),
                    importance: GetDouble(args, "importance"),
                    pin: GetBool(args, "pin"),
                    archived: GetBool(args, "archived"),
                    expiresAt: GetDateTimeOffset(args, "expiresAt")
                ) };

            case "memory.delete":
                return new { removed = await memory.DeleteAsync(GetString(args, "id")!, GetBool(args, "hard") ?? false) };

            case "memory.archive":
                return new { ok = await memory.UpdateAsync(GetString(args, "id")!, archived: true) };
            case "memory.unarchive":
                return new { ok = await memory.UpdateAsync(GetString(args, "id")!, archived: false) };
            case "memory.pin":
                return new { ok = await memory.UpdateAsync(GetString(args, "id")!, pin: true) };
            case "memory.unpin":
                return new { ok = await memory.UpdateAsync(GetString(args, "id")!, pin: false) };

            case "memory.list":
            {
                var (items, next) = await memory.ListAdvancedAsync(
                    agentId: GetString(args, "agentId"),
                    ns: GetString(args, "ns"),
                    types: GetStringArray(args, "types"),
                    tags: GetStringArray(args, "tags"),
                    pinned: GetBool(args, "pinned"),
                    archived: GetBool(args, "archived"),
                    before: GetDateTimeOffset(args, "before"),
                    after: GetDateTimeOffset(args, "after"),
                    limit: GetInt(args, "limit") ?? 50,
                    cursor: GetString(args, "cursor")
                );
                return new { items, nextCursor = next };
            }

            case "memory.search":
                return new { items = await memory.SearchAsync(
                    query: GetString(args, "query")!,
                    ns: GetString(args, "ns"),
                    limit: GetInt(args, "limit") ?? 20
                ) };

            case "memory.cleanup":
                return new { removed = await memory.CleanupExpireAsync(GetString(args, "ns")) };

            case "memory.tags.add":
                return new { ok = await memory.AddTagsAsync(GetString(args, "id")!, GetStringArray(args, "tags") ?? new List<string>()) };
            case "memory.tags.remove":
                return new { ok = await memory.RemoveTagsAsync(GetString(args, "id")!, GetStringArray(args, "tags") ?? new List<string>()) };
            case "memory.refs.add":
                return new { ok = await memory.AddRefsAsync(GetString(args, "id")!, GetStringArray(args, "refs") ?? new List<string>()) };
                
            case "memory.refs.remove":
                return new { ok = await memory.RemoveRefsAsync(GetString(args, "id")!, GetStringArray(args, "refs") ?? new List<string>()) };

            case "task.create":
                return new { id = await tasks.CreateTaskAsync(
                    title: GetString(args, "title")!,
                    ns: GetString(args, "ns") ?? "default"
                ) };

            case "task.list":
                return new { items = await tasks.ListTasksAsync(GetInt(args, "limit") ?? 50) };

            case "task.update_status":
                return new { ok = await tasks.UpdateStatusAsync(GetString(args, "id")!, GetString(args, "status")!, GetString(args, "note")) };
            case "task.add_note":
                return new { id = await tasks.AddNoteAsync(GetString(args, "id")!, GetString(args, "note")!) };

            case "export.dump":
            {
                var ns = GetString(args, "ns");
                var items = await memory.ExportAsync(ns);
                var sb = new StringBuilder();
                foreach (var it in items)
                {
                    sb.AppendLine(JsonSerializer.Serialize(it));
                }
                return new { ndjson = sb.ToString(), count = items.Count };
            }

            case "export.import":
            {
                var s = GetString(args, "ndjson") ?? string.Empty;
                var lines = s.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int upserted = 0;
                foreach (var line in lines)
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<MemoryItem>(line);
                        if (item is not null)
                        {
                            await memory.UpsertAsync(item);
                            upserted++;
                        }
                    }
                    catch { }
                }
                return new { upserted };
            }

            case "resources/list":
            {
                // Minimal resources exposure: list latest memories as mem://<ns>/<id>
                var (items, _) = await memory.ListAdvancedAsync(limit: 50);
                var resources = items.Select(i => new {
                    uri = $"mem://{i.Namespace}/{i.Id}",
                    name = i.Title ?? (i.Content.Length > 30 ? i.Content[..30] + "…" : i.Content),
                    description = i.Type,
                    mimeType = "text/plain"
                }).ToArray();
                return new { resources };
            }

            case "resources/read":
            {
                var uri = GetString(args, "uri")!;
                // parse mem://ns/id
                if (!uri.StartsWith("mem://")) throw new InvalidOperationException("Unsupported URI");
                var path = uri.Substring("mem://".Length);
                var idx = path.IndexOf('/');
                if (idx <= 0) throw new InvalidOperationException("Invalid URI");
                var ns = path.Substring(0, idx);
                var id = path.Substring(idx + 1);
                var item = await memory.GetAsync(id);
                if (item is null || item.Namespace != ns) throw new InvalidOperationException("Not found");
                var contents = new [] { new { uri, mimeType = "text/plain", text = item.Content } };
                return new { contents };
            }

            default:
                throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    private static async Task<(byte[] Buffer, int Length)?> ReadHeadersAsync(Stream input, CancellationToken cancel)
    {
        var headerBuffer = new List<byte>(256);
        var span = new byte[1];
        int matched = 0;
        while (true)
        {
            var read = await input.ReadAsync(span, 0, 1, cancel);
            if (read == 0)
            {
                if (headerBuffer.Count == 0) return null; // EOF
                break;
            }
            headerBuffer.Add(span[0]);
            if ((matched == 0 || matched == 2) && span[0] == (byte)'\r') matched++;
            else if ((matched == 1 || matched == 3) && span[0] == (byte)'\n') matched++;
            else matched = span[0] == (byte)'\r' ? 1 : 0;
            if (matched == 4) break; // \r\n\r\n
        }
        return (headerBuffer.ToArray(), headerBuffer.Count);
    }

    private static async Task<byte[]?> ReadFramedAsync(Stream input, CancellationToken cancel)
    {
        var headers = await ReadHeadersAsync(input, cancel);
        if (headers is null) return null;
        var text = Encoding.ASCII.GetString(headers.Value.Buffer, 0, headers.Value.Length);
        int contentLength = 0;
        foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line.Substring("Content-Length:".Length).Trim();
                contentLength = int.Parse(val);
            }
        }
        var body = new byte[contentLength];
        var readTotal = 0;
        while (readTotal < contentLength)
        {
            var n = await input.ReadAsync(body, readTotal, contentLength - readTotal, cancel);
            if (n == 0) break;
            readTotal += n;
        }
        if (readTotal != contentLength) return null;
        return body;
    }

    private static async Task WriteFramedAsync(Stream output, object payload, CancellationToken cancel)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\nContent-Type: application/json\r\n\r\n");
        await output.WriteAsync(header, 0, header.Length, cancel);
        await output.WriteAsync(bytes, 0, bytes.Length, cancel);
        await output.FlushAsync(cancel);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Undefined || obj.ValueKind == JsonValueKind.Null || !obj.TryGetProperty(name, out var v)
            ? null
            : v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    private static int? GetInt(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static double? GetDouble(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;
    private static bool? GetBool(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True ? true : obj.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.False ? false : (bool?)null;
    private static DateTimeOffset? GetDateTimeOffset(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dto)) return dto;
        return null;
    }
    private static Dictionary<string, object>? GetDict(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object>();
        foreach (var p in v.EnumerateObject()) dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : JsonSerializer.Deserialize<object>(p.Value.GetRawText())!;
        return dict;
    }
    private static List<string>? GetStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in v.EnumerateArray()) list.Add(el.ValueKind == JsonValueKind.String ? el.GetString()! : el.ToString());
        return list;
    }
}
