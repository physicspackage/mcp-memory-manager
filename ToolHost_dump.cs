using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using McpMemoryManager.Server.Models;

namespace McpMemoryManager.Server.Tools;

public static class ToolHost
{
    public static Task RunAsync(MemoryApi memory, TaskApi tasks)
        => RunAsync(memory, tasks, Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);

    public static async Task RunTcpAsync(MemoryApi memory, TaskApi tasks, string endpoint, CancellationToken cancel = default)
    {
        string host = "127.0.0.1";
        int port;
        var parts = endpoint.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            if (!int.TryParse(parts[0], out port)) throw new ArgumentException("--tcp expects PORT or HOST:PORT");
        }
        else if (parts.Length == 2)
        {
            host = parts[0];
            if (!int.TryParse(parts[1], out port)) throw new ArgumentException("Invalid port in --tcp");
        }
        else throw new ArgumentException("--tcp expects PORT or HOST:PORT");

        var ip = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(host);
        var listener = new TcpListener(ip, port);
        listener.Start();
        await Console.Error.WriteLineAsync($"[MCP] TCP listening on {ip}:{port}");

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var s = c.GetStream();
                    try { await RunAsync(memory, tasks, s, s, cancel); }
                    catch { /* ignore per-connection errors */ }
                });
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task RunWebSocketAsync(MemoryApi memory, TaskApi tasks, string endpoint, CancellationToken cancel = default)
    {
        string url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"http://{endpoint}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ApplicationName = typeof(ToolHost).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, WebRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production, }
        );
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.UseWebSockets();

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Expected WebSocket request");
                return;
            }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            while (!cancel.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var receive = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                if (receive.MessageType == WebSocketMessageType.Close) break;
                int count = receive.Count;
                while (!receive.EndOfMessage)
                {
                    if (count >= buffer.Length)
                    {
                        context.Abort();
                        return;
                    }
                    receive = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), cancel);
                    count += receive.Count;
                }
                var json = Encoding.UTF8.GetString(buffer, 0, count);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var payload = await BuildResponseAsync(doc.RootElement, memory, tasks);
                    var outJson = JsonSerializer.Serialize(payload);
                    var outBytes = Encoding.UTF8.GetBytes(outJson);
                    await ws.SendAsync(new ArraySegment<byte>(outBytes), WebSocketMessageType.Text, true, cancel);
                }
                catch (Exception ex)
                {
                    var err = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (JsonElement?)null, error = new { code = -32603, message = ex.Message } });
                    var outBytes = Encoding.UTF8.GetBytes(err);
                    await ws.SendAsync(new ArraySegment<byte>(outBytes), WebSocketMessageType.Text, true, cancel);
                }
            }
        });

        app.MapGet("/", () => Results.Ok("mcp-memory-manager ws endpoint at /ws"));
        await app.RunAsync(cancel);
    }

    public static async Task RunAsync(MemoryApi memory, TaskApi tasks, Stream input, Stream output, CancellationToken cancel)
    {
        var tools = GetTools();
        while (!cancel.IsCancellationRequested)
        {
            var reqBytes = await ReadFramedAsync(input, cancel);
            if (reqBytes is null) break;
            try
            {
                using var doc = JsonDocument.Parse(reqBytes);
                var payload = await BuildResponseAsync(doc.RootElement, memory, tasks);
                await WriteFramedAsync(output, payload, cancel);
            }
            catch (Exception ex)
            {
                await WriteFramedAsync(output, new { jsonrpc = "2.0", id = (JsonElement?)null, error = new { code = -32603, message = ex.Message } }, cancel);
            }
        }
    }

    public static async Task RunHttpAsync(MemoryApi memory, TaskApi tasks, string endpoint, CancellationToken cancel = default)
    {
        string url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"http://{endpoint}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(ToolHost).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls(url);
        var app = builder.Build();

        // Simple SSE endpoint with keep-alives
        app.MapGet("/sse", async context =>
        {
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["Content-Type"] = "text/event-stream";
            await context.Response.Body.FlushAsync();
            var aborted = context.RequestAborted;
            // Periodic keep-alive comments
            while (!aborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n");
                await context.Response.Body.FlushAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(15), aborted); }
                catch { break; }
            }
        });

        // JSON-RPC over HTTP POST
        var handlePost = async (HttpContext ctx) =>
        {
            try
            {
                var method0 = ctx.Request.Method;
                var path0 = ctx.Request.Path.ToString();
                var ct0 = ctx.Request.ContentType ?? string.Empty;
                var enc0 = ctx.Request.Headers.TryGetValue("Content-Encoding", out var encHdr) ? encHdr.ToString() : string.Empty;
                var cl0 = ctx.Request.Headers.TryGetValue("Content-Length", out var clHdr) ? clHdr.ToString() : string.Empty;
                await Console.Error.WriteLineAsync($"[HTTP] {method0} {path0} CT={ct0} CE={enc0} CL={cl0}");

                string body;
                // Handle optional compression
                if (ctx.Request.Headers.TryGetValue("Content-Encoding", out var enc) && enc.Count > 0)
                {
                    var val = enc.ToString().ToLowerInvariant();
                    if (val.Contains("gzip"))
                    {
                        using var gz = new System.IO.Compression.GZipStream(ctx.Request.Body, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
                        using var sr = new StreamReader(gz, Encoding.UTF8);
                        body = await sr.ReadToEndAsync();
                    }
                    else if (val.Contains("deflate"))
                    {
                        using var df = new System.IO.Compression.DeflateStream(ctx.Request.Body, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
                        using var sr = new StreamReader(df, Encoding.UTF8);
                        body = await sr.ReadToEndAsync();
                    }
                    else
                    {
                        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                        body = await sr.ReadToEndAsync();
                    }
                }
                else
                {
                    using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    body = await sr.ReadToEndAsync();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    var bad = new { jsonrpc = "2.0", id = (JsonElement?)null, error = new { code = -32600, message = "Empty request body" } };
                    await Console.Error.WriteLineAsync("[HTTP] Empty body");
                    return Results.Json(bad, statusCode: 200);
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? rpcMethod = null;
                try { rpcMethod = root.GetProperty("method").GetString(); } catch { }
                string? rpcId = null;
                if (root.TryGetProperty("id", out var idEl)) rpcId = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
                await Console.Error.WriteLineAsync($"[MCP] method={rpcMethod ?? "<none>"} id={rpcId ?? "<none>"} bodyChars={body.Length}");

                var payload = await BuildResponseAsync(root, memory, tasks);
                return Results.Json(payload);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[HTTP ERROR] {ex.Message}");
                var err = new { jsonrpc = "2.0", id = (JsonElement?)null, error = new { code = -32603, message = ex.Message } };
                return Results.Json(err, statusCode: 200);
            }
        };
        app.MapPost("/mcp", handlePost);
        app.MapPost("/", handlePost);
        app.MapPost("{*path}", handlePost); // Fallback: accept POST at any path for JSON-RPC

        app.MapGet("/", () => Results.Ok("mcp-memory-manager http endpoint: POST /mcp, SSE /sse"));
        await app.RunAsync(cancel);
    }

    private static async Task<object> BuildResponseAsync(JsonElement root, MemoryApi memory, TaskApi tasks)
    {
        var hasId = root.TryGetProperty("id", out var idEl);
        object? id = hasId ? DecodeId(idEl) : null;
        var method = root.GetProperty("method").GetString();

        switch (method)
        {
            case "initialize":
                return new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = "2024-11-05",
                        serverInfo = new { name = "mcp-memory-manager", version = "0.1.0" },
                        capabilities = new { tools = new { } }
                    }
                };

            case "tools/list":
                return new { jsonrpc = "2.0", id, result = new { tools = GetTools() } };

            case "resources/list":
            {
                var p = root.TryGetProperty("params", out var p2) ? p2 : default;
                var (items, next) = await memory.ListAdvancedAsync(
                    ns: GetString(p, "ns"),
                    types: GetStringArray(p, "types"),
                    tags: GetStringArray(p, "tags"),
                    pinned: GetBool(p, "pinned"),
                    archived: GetBool(p, "archived"),
                    before: GetDateTimeOffset(p, "before"),
                    after: GetDateTimeOffset(p, "after"),
                    limit: GetInt(p, "limit") ?? 50,
                    cursor: GetString(p, "cursor")
                );
                var resources = items.Select(i => new
                {
                    uri = $"mem://{i.Namespace}/{i.Id}",
                    name = i.Title ?? (i.Content.Length > 30 ? i.Content[..30] + "…" : i.Content),
                    description = i.Type,
                    mimeType = "text/plain"
                }).ToArray();
                return new { jsonrpc = "2.0", id, result = new { resources, nextCursor = next } };
            }

            case "resources/read":
            {
                var p = root.GetProperty("params");
                var uri = p.GetProperty("uri").GetString()!;
                var format = GetString(p, "format") ?? "text";
                if (!uri.StartsWith("mem://")) return new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Unsupported URI" } };
                var path = uri.Substring("mem://".Length);
                var idx = path.IndexOf('/');
                if (idx <= 0) return new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Invalid URI" } };
                var ns = path.Substring(0, idx);
                var idPart = path.Substring(idx + 1);
                var item = await memory.GetAsync(idPart);
                if (item is null || item.Namespace != ns) return new { jsonrpc = "2.0", id, error = new { code = -32004, message = "Not found" } };
                object contentObj = format.Equals("json", StringComparison.OrdinalIgnoreCase)
                    ? new { uri, mimeType = "application/json", text = JsonSerializer.Serialize(item) }
                    : new { uri, mimeType = "text/plain", text = item.Content };
                return new { jsonrpc = "2.0", id, result = new { contents = new[] { contentObj } } };
            }

            case "tools/call":
            {
                var p = root.GetProperty("params");
                var name = p.GetProperty("name").GetString()!;
                var a = p.TryGetProperty("arguments", out var a2) ? a2 : default;
                var result = await CallToolAsync(name, a, memory, tasks);
                return new { jsonrpc = "2.0", id, result = new { content = result } };
            }

            default:
                return new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } };
        }
    }

    private static object? DecodeId(JsonElement idEl)
    {
        switch (idEl.ValueKind)
        {
            case JsonValueKind.String:
                return idEl.GetString();
            case JsonValueKind.Number:
                if (idEl.TryGetInt64(out var l)) return l;
                if (idEl.TryGetDouble(out var d)) return d;
                return JsonSerializer.Deserialize<object>(idEl.GetRawText());
            case JsonValueKind.Null:
                return null;
            default:
                // JSON-RPC IDs should not be arrays/objects/bools; return raw text for visibility
                return JsonSerializer.Deserialize<object>(idEl.GetRawText());
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
            }
        },
        new { name = "memory.get", description = "Get a memory item by id", inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new [] { "id" } } },
        new { name = "memory.update", description = "Update fields of a memory (partial)", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" }, content = new { type = "string", @nullable = true }, title = new { type = "string", @nullable = true }, metadata = new { type = "object", additionalProperties = true, @nullable = true }, tags = new { type = "array", items = new { type = "string" }, @nullable = true }, refs = new { type = "array", items = new { type = "string" }, @nullable = true }, importance = new { type = "number", @nullable = true }, pin = new { type = "boolean", @nullable = true }, archived = new { type = "boolean", @nullable = true }, expiresAt = new { type = "string", @nullable = true } } } },
        new { name = "memory.delete", description = "Delete a memory (soft by default)", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" }, hard = new { type = "boolean", @default = false } } } },
        new { name = "memory.archive", description = "Archive a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.unarchive", description = "Unarchive a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.pin", description = "Pin a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.unpin", description = "Unpin a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" } } } },
        new { name = "memory.list", description = "List recent memories", inputSchema = new { type = "object", properties = new { agentId = new { type = "string", @nullable = true }, ns = new { type = "string", @nullable = true }, types = new { type = "array", items = new { type = "string" }, @nullable = true }, tags = new { type = "array", items = new { type = "string" }, @nullable = true }, pinned = new { type = "boolean", @nullable = true }, archived = new { type = "boolean", @nullable = true }, before = new { type = "string", @nullable = true }, after = new { type = "string", @nullable = true }, limit = new { type = "integer", @default = 50 }, cursor = new { type = "string", @nullable = true } } } },
        new { name = "memory.search", description = "Search memories via FTS5", inputSchema = new { type = "object", properties = new { query = new { type = "string" }, ns = new { type = "string", @nullable = true }, limit = new { type = "integer", @default = 20 } }, required = new [] { "query" } } },
        new { name = "memory.cleanup", description = "Delete expired memories", inputSchema = new { type = "object", properties = new { ns = new { type = "string", @nullable = true } } } },
        new { name = "memory.tags.add", description = "Add tags to a memory", inputSchema = new { type = "object", required = new [] { "id", "tags" }, properties = new { id = new { type = "string" }, tags = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.tags.remove", description = "Remove tags from a memory", inputSchema = new { type = "object", required = new [] { "id", "tags" }, properties = new { id = new { type = "string" }, tags = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.refs.add", description = "Add refs to a memory", inputSchema = new { type = "object", required = new [] { "id", "refs" }, properties = new { id = new { type = "string" }, refs = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.refs.remove", description = "Remove refs from a memory", inputSchema = new { type = "object", required = new [] { "id", "refs" }, properties = new { id = new { type = "string" }, refs = new { type = "array", items = new { type = "string" } } } } },
        new { name = "memory.link", description = "Link two memories", inputSchema = new { type = "object", required = new [] { "from_id", "to_id" }, properties = new { from_id = new { type = "string" }, to_id = new { type = "string" }, relation = new { type = "string", @nullable = true } } } },
        new { name = "memory.summarize", description = "Summarize a memory", inputSchema = new { type = "object", required = new [] { "id" }, properties = new { id = new { type = "string" }, style = new { type = "string", @nullable = true } } } },
        new { name = "memory.summarize_thread", description = "Summarize a set of memories", inputSchema = new { type = "object", required = new [] { "source_ids" }, properties = new { source_ids = new { type = "array", items = new { type = "string" } }, style = new { type = "string", @nullable = true } } } },
        new { name = "memory.merge", description = "Merge memories", inputSchema = new { type = "object", required = new [] { "source_ids" }, properties = new { source_ids = new { type = "array", items = new { type = "string" } }, target_title = new { type = "string", @nullable = true }, ns = new { type = "string", @nullable = true } } } },
        new { name = "task.create", description = "Create a task", inputSchema = new { type = "object", properties = new { title = new { type = "string" }, ns = new { type = "string", @default = "default" } }, required = new [] { "title" } } },
        new { name = "task.update_status", description = "Update task status", inputSchema = new { type = "object", required = new [] { "id", "status" }, properties = new { id = new { type = "string" }, status = new { type = "string" }, note = new { type = "string", @nullable = true } } } },
        new { name = "task.add_note", description = "Attach a note to a task", inputSchema = new { type = "object", required = new [] { "id", "note" }, properties = new { id = new { type = "string" }, note = new { type = "string" } } } },
        new { name = "task.list", description = "List tasks", inputSchema = new { type = "object", properties = new { limit = new { type = "integer", @default = 50 } } } },
        new { name = "export.dump", description = "Export NDJSON", inputSchema = new { type = "object", properties = new { ns = new { type = "string", @nullable = true } } } },
        new { name = "export.import", description = "Import NDJSON", inputSchema = new { type = "object", required = new [] { "ndjson" }, properties = new { ndjson = new { type = "string" } } } }
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

            case "memory.link":
                return new { ok = await memory.LinkAsync(GetString(args, "from_id")!, GetString(args, "to_id")!, GetString(args, "relation")) };

            case "memory.summarize":
                return new { id = await memory.SummarizeAsync(GetString(args, "id")!, GetString(args, "style")) };

            case "memory.summarize_thread":
                return new { id = await memory.SummarizeThreadAsync(GetStringArray(args, "source_ids") ?? new List<string>(), GetString(args, "style")) };

            case "memory.merge":
                return new { id = await memory.MergeAsync(GetStringArray(args, "source_ids") ?? new List<string>(), GetString(args, "target_title"), GetString(args, "ns")) };

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
                if (headerBuffer.Count == 0) return null;
                break;
            }
            headerBuffer.Add(span[0]);
            if ((matched == 0 || matched == 2) && span[0] == (byte)'\r') matched++;
            else if ((matched == 1 || matched == 3) && span[0] == (byte)'\n') matched++;
            else matched = span[0] == (byte)'\r' ? 1 : 0;
            if (matched == 4) break;
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
