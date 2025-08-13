# MCP Memory Manager

An **MCP (Model Context Protocol) server** for AI agents to store and manage **memories** — tasks, notes, results, documents, and blobs — with lifecycle controls like cleanup, deduplication, and summarization.

## Features

- **CRUD for memories** (`memory.create/get/list/update/delete`)
- **Full-text search** (SQLite FTS5)
- **Task management** (`task.create/update/list`)
- **Namespace & tagging** for isolation and filtering
- **TTL & cleanup strategies**
- **Blob storage** for attachments (planned)
- **Export/import** for migration & backups (planned)

## Project Layout


## Run Options

- Build once to restore and compile: `dotnet build`

- CLI mode (default):
  - `dotnet run --project src/McpMemoryManager.Server`
  - Starts an interactive REPL to smoke-test create/list/search.

- MCP over stdio: `--mcp`
  - `dotnet run --project src/McpMemoryManager.Server -- --mcp`
  - Runs a minimal JSON-RPC 2.0 server over stdin/stdout using Content-Length framing.

- MCP over TCP: `--tcp <HOST:PORT>` or `--tcp <PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --tcp 127.0.0.1:8765`
  - Frames: HTTP-like headers + JSON body (see Protocol Notes below).

- MCP over WebSocket: `--ws <URL>` or `--ws <HOST:PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --ws 127.0.0.1:8080`
  - Binds HTTP server at the URL (defaults to `http://<HOST:PORT>` if scheme omitted).
  - WebSocket endpoint path: `/ws` (connect to `ws://127.0.0.1:8080/ws`).

- MCP over HTTP/SSE: `--http <URL>` or `--http <HOST:PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765`
  - POST endpoint: `/mcp` (JSON-RPC request/response)
  - SSE endpoint: `/sse` (keep-alives / future events)

- Database location: `--db <PATH>`
  - Stores the SQLite database at the given path.
  - Example: `--db C:\\data\\mcp-memory.db`
  - If omitted, defaults to `memory.db` under the app base directory.

Examples

- Stdio MCP with custom DB path:
  - `dotnet run --project src/McpMemoryManager.Server -- --mcp --db ./.local/memory.db`

- TCP on localhost:8765:
  - `dotnet run --project src/McpMemoryManager.Server -- --tcp 8765`

- WebSocket on 0.0.0.0:8080:
  - `dotnet run --project src/McpMemoryManager.Server -- --ws 0.0.0.0:8080`

## Protocol Notes

- Top-level JSON-RPC methods (no `tools/call`):
  - `initialize`
  - `tools/list`
  - `resources/list`
  - `resources/read`

- Tools are invoked via `tools/call` with `{ name, arguments }`.

- Stdio/TCP framing (requests and responses):
  - `Content-Length: <bytes>`
  - `Content-Type: application/json`
  - blank line
  - JSON body (UTF-8)

- WebSocket framing:
  - One JSON-RPC message per text frame.

## MCP Client Config Examples

Most MCP clients support `stdio`, `websocket`, or `http` transports. This server supports all three. Use one of the following:

- Stdio (recommended for local):
  - Start command is launched by the client; example config snippet:
    - For Windows:
      - `type`: `"stdio"`
      - `command`: `"dotnet"`
      - `args`: `[ "run", "--project", "src/McpMemoryManager.Server", "--", "--mcp" ]`

- WebSocket:
  - Start the server yourself:
    - `dotnet run --project src/McpMemoryManager.Server -- --ws 127.0.0.1:8080`
  - Point the client to the WS endpoint:
    - `type`: `"websocket"`
    - `url`: `"ws://127.0.0.1:8080/ws"`

- HTTP + SSE:
  - Start the server:
    - `dotnet run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765`
  - Endpoints:
    - POST `http://127.0.0.1:8765/mcp` — JSON-RPC request/response
    - GET  `http://127.0.0.1:8765/sse` — Server-Sent Events (keep-alives)
  - Client config:
    - `type`: `"http"`
    - `url`: `"http://127.0.0.1:8765/mcp"`

Note: The `--tcp` option exists for ad‑hoc testing, but many MCP clients do not support raw TCP.

## Quick Test

- Create a note via TCP:

  1) Start: `dotnet run --project src/McpMemoryManager.Server -- --tcp 127.0.0.1:8765`
  2) Connect and send frames (example payloads):
     - `{"jsonrpc":"2.0","id":1,"method":"initialize"}`
     - `{"jsonrpc":"2.0","id":2,"method":"tools/list"}`
     - `{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"memory.create","arguments":{"content":"hello","ns":"default"}}}`

## Notes

- The server exposes tools for memory and task management (create/get/list/search/update/delete, tags/refs, archive/pin, summarize/merge, export/import) and basic resources endpoints for MCP.
- For WebSocket clients, connect to `/ws`; for stdio/TCP clients use JSON-RPC framing described above.
