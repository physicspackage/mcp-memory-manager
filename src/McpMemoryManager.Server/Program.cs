using McpMemoryManager.Server.MemoryStore;
using McpMemoryManager.Server.Tools;

// NOTE: This runs today without the MCP SDK installed. It initializes the DB and
// leaves a simple command loop to smoke‑test create/list/search. Wiring to MCP
// can be enabled with --mcp to run a minimal JSON-RPC stdio server.

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
var mcpMode = argv.Contains("--mcp", StringComparer.OrdinalIgnoreCase);
var tcpIdx = Array.FindIndex(argv, a => a.Equals("--tcp", StringComparison.OrdinalIgnoreCase));
string? tcpEndpoint = null;
if (tcpIdx >= 0)
{
    tcpEndpoint = tcpIdx + 1 < argv.Length ? argv[tcpIdx + 1] : "127.0.0.1:8765";
}

var dbIdx = Array.FindIndex(argv, a => a.Equals("--db", StringComparison.OrdinalIgnoreCase));
string dbPath = dbIdx >= 0 && dbIdx + 1 < argv.Length ? argv[dbIdx + 1] : Path.Combine(AppContext.BaseDirectory, "memory.db");
var store = await SqliteStore.CreateOrOpenAsync(dbPath);
Console.WriteLine($"[MCP Memory Manager] DB: {dbPath}");

var memory = new MemoryApi(store);
var tasks = new TaskApi(store);

var wsIdx = Array.FindIndex(argv, a => a.Equals("--ws", StringComparison.OrdinalIgnoreCase));
string? wsEndpoint = null;
if (wsIdx >= 0)
{
    wsEndpoint = wsIdx + 1 < argv.Length ? argv[wsIdx + 1] : "http://127.0.0.1:8080";
}

if (mcpMode || tcpEndpoint != null || wsEndpoint != null)
{
    if (tcpEndpoint != null)
    {
        Console.Error.WriteLine($"[MCP Memory Manager] Starting TCP server on {tcpEndpoint}");
        await ToolHost.RunTcpAsync(memory, tasks, tcpEndpoint);
    }
    else if (wsEndpoint != null)
    {
        Console.Error.WriteLine($"[MCP Memory Manager] Starting WebSocket server at {wsEndpoint}/ws");
        await ToolHost.RunWebSocketAsync(memory, tasks, wsEndpoint);
    }
    else
    {
        await ToolHost.RunAsync(memory, tasks);
    }
    return;
}

Console.WriteLine("Type 'help' for commands. Press Ctrl+C to quit.\n");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) continue;
    var cmd = line.Trim();

    if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(@"Commands:
  note <text>                - create a note memory
  list                       - list latest 10 memories
  search <query>             - FTS5 search in content/tags
  task <title>               - create a task
  tasks                      - list latest 10 tasks
  quit                       - exit");
        continue;
    }
    if (cmd.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    if (cmd.StartsWith("note ", StringComparison.OrdinalIgnoreCase))
    {
        var text = cmd[5..].Trim();
        var id = await memory.CreateAsync(content: text, type: "note");
        Console.WriteLine($"created note {id}");
        continue;
    }
    if (cmd.Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        var items = await memory.ListAsync(limit: 10);
        foreach (var m in items)
            Console.WriteLine($"{m.Id[..8]} | {m.Type} | {m.Namespace} | {m.CreatedAt:u} | {m.Content.Replace('\n',' ')}");
        continue;
    }
    if (cmd.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
    {
        var q = cmd[7..].Trim();
        var items = await memory.SearchAsync(q, limit: 10);
        foreach (var m in items)
            Console.WriteLine($"{m.Id[..8]} | score:{m.Score:F3} | {m.Content.Replace('\n',' ')}");
        continue;
    }
    if (cmd.StartsWith("task ", StringComparison.OrdinalIgnoreCase))
    {
        var title = cmd[5..].Trim();
        var id = await tasks.CreateTaskAsync(title);
        Console.WriteLine($"created task {id}");
        continue;
    }
    if (cmd.Equals("tasks", StringComparison.OrdinalIgnoreCase))
    {
        var items = await tasks.ListTasksAsync(limit: 10);
        foreach (var t in items)
            Console.WriteLine($"{t.Id[..8]} | {t.Status} | {t.Title}");
        continue;
    }

    Console.WriteLine("Unknown command. Type 'help'.");
}
