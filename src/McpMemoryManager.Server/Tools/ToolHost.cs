using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpMemoryManager.Server.Tools;

public static class ToolHost
{
    public static async Task RunAsync(MemoryApi memory, TaskApi tasks)
    {
        var tools = GetTools();
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
                var method = root.GetProperty("method").GetString();

                switch (method)
                {
                    case "initialize":
                        await WriteResponseAsync(id, new
                        {
                            protocolVersion = "2024-11-05",
                            server = new { name = "mcp-memory-manager", version = "0.1.0" },
                            capabilities = new { tools = new { } }
                        });
                        break;

                    case "tools/list":
                        await WriteResponseAsync(id, new { tools });
                        break;

                    case "tools/call":
                    {
                        var @params = root.GetProperty("params");
                        var name = @params.GetProperty("name").GetString()!;
                        var args = @params.TryGetProperty("arguments", out var a) ? a : default;
                        var result = await CallToolAsync(name, args, memory, tasks);
                        await WriteResponseAsync(id, new { content = result });
                        break;
                    }

                    default:
                        await WriteErrorAsync(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(default, -32603, ex.Message);
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
                    content = new { type = "string" },
                    type = new { type = "string",  @default = "note" },
                    title = new { type = "string",  @nullable = true },
                    agentId = new { type = "string",  @default = "" },
                    ns = new { type = "string",  @default = "default" },
                    metadata = new { type = "object", additionalProperties = true, @nullable = true },
                    tags = new { type = "array", items = new { type = "string" }, @nullable = true },
                    refs = new { type = "array", items = new { type = "string" }, @nullable = true },
                    importance = new { type = "number", @default = 0.3 },
                    pin = new { type = "boolean", @default = false },
                    expiresAt = new { type = "string",  description = "ISO-8601 timestamp", @nullable = true }
                },
                required = new [] { "content" }
            }
        },
        new {
            name = "memory.get",
            description = "Get a memory item by id",
            inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new [] { "id" } }
        },
        new {
            name = "memory.list",
            description = "List recent memories",
            inputSchema = new { type = "object", properties = new { ns = new { type = "string", @nullable = true }, type = new { type = "string", @nullable = true }, limit = new { type = "integer", @default = 50 } } }
        },
        new {
            name = "memory.search",
            description = "Search memories via FTS5",
            inputSchema = new { type = "object", properties = new { query = new { type = "string" }, ns = new { type = "string", @nullable = true }, limit = new { type = "integer", @default = 20 } }, required = new [] { "query" } }
        },
        new {
            name = "memory.cleanup",
            description = "Delete expired memories",
            inputSchema = new { type = "object", properties = new { ns = new { type = "string", @nullable = true } } }
        },
        new {
            name = "task.create",
            description = "Create a task (stored as memory of type 'task')",
            inputSchema = new { type = "object", properties = new { title = new { type = "string" }, ns = new { type = "string", @default = "default" } }, required = new [] { "title" } }
        },
        new {
            name = "task.list",
            description = "List tasks",
            inputSchema = new { type = "object", properties = new { limit = new { type = "integer", @default = 50 } } }
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

            case "memory.list":
                return new { items = await memory.ListAsync(
                    ns: GetString(args, "ns"),
                    type: GetString(args, "type"),
                    limit: GetInt(args, "limit") ?? 50
                ) };

            case "memory.search":
                return new { items = await memory.SearchAsync(
                    query: GetString(args, "query")!,
                    ns: GetString(args, "ns"),
                    limit: GetInt(args, "limit") ?? 20
                ) };

            case "memory.cleanup":
                return new { removed = await memory.CleanupExpireAsync(GetString(args, "ns")) };

            case "task.create":
                return new { id = await tasks.CreateTaskAsync(
                    title: GetString(args, "title")!,
                    ns: GetString(args, "ns") ?? "default"
                ) };

            case "task.list":
                return new { items = await tasks.ListTasksAsync(GetInt(args, "limit") ?? 50) };

            default:
                throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    private static async Task WriteResponseAsync(JsonElement id, object result)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        await Console.Out.WriteLineAsync(payload);
        await Console.Out.FlushAsync();
    }

    private static async Task WriteErrorAsync(JsonElement id, int code, string message)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
        await Console.Out.WriteLineAsync(payload);
        await Console.Out.FlushAsync();
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
