# MCP Memory Manager

An MCP (Model Context Protocol) server for AI agents to store and manage memories — tasks, notes, results, and documents — with lifecycle controls like cleanup and summarization.

## Features

- CRUD for memories: `memory.create/get/list/update/delete`
- Full-text search via SQLite FTS5
- Task management: `task.create/update_status/add_note/list`
- Namespaces, tags, refs; pin/archive; TTL + cleanup
- Export/import NDJSON: `export.dump` / `export.import`
- Transports: stdio, TCP, WebSocket, HTTP+SSE

## Prerequisites

- .NET SDK `9.0` or later
- Windows, macOS, or Linux (x64/ARM64)

## Install & Build

- Restore/build: `dotnet build`
- Run tests (optional): `dotnet test`

## Run Options

- CLI REPL (local smoke-test):
  - `dotnet run --project src/McpMemoryManager.Server`
  - Commands: `note <text>`, `list`, `search <query>`, `task <title>`, `tasks`

- MCP over stdio: `--mcp`
  - `dotnet run --project src/McpMemoryManager.Server -- --mcp`
  - JSON-RPC 2.0 over stdin/stdout with Content-Length framing

- MCP over TCP: `--tcp <HOST:PORT>` or `--tcp <PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --tcp 127.0.0.1:8765`

- MCP over WebSocket: `--ws <URL>` or `--ws <HOST:PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --ws 127.0.0.1:8080`
  - WS endpoint path: `/ws` (e.g., `ws://127.0.0.1:8080/ws`)

- MCP over HTTP + SSE: `--http <URL>` or `--http <HOST:PORT>`
  - `dotnet run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765`
  - POST JSON-RPC at `/mcp` (also accepts POST at `/` and any path)
  - SSE keep-alives at `/sse`

- Database location: `--db <PATH>`
  - Defaults to `memory.db` under the app base directory
  - Example: `--db ./data/memory.db`

Examples

- Stdio with custom DB: `dotnet run --project src/McpMemoryManager.Server -- --mcp --db ./.local/memory.db`
- TCP on localhost:8765: `dotnet run --project src/McpMemoryManager.Server -- --tcp 8765`
- WebSocket on 0.0.0.0:8080: `dotnet run --project src/McpMemoryManager.Server -- --ws 0.0.0.0:8080`

## Protocol Notes

- Top-level JSON-RPC methods: `initialize`, `tools/list`, `resources/list`, `resources/read`
- Tool calls go through `tools/call` with `{ name, arguments }`
- Stdio/TCP framing: `Content-Length`, `Content-Type`, blank line, then UTF-8 JSON body
- WebSocket framing: one JSON-RPC message per text frame

## Tools Reference

- memory.create: Create a memory (`content`, optional `type`, `title`, `agentId`, `ns`, `metadata`, `tags`, `refs`, `importance`, `pin`, `expiresAt`)
- memory.get: Get a memory by `id`
- memory.update: Partial update by `id` (any subset of fields)
- memory.delete: Soft delete by default; set `hard: true` to hard-delete
- memory.archive / memory.unarchive: Archive toggle
- memory.pin / memory.unpin: Pin toggle
- memory.list: Filter by `agentId`, `ns`, `types`, `tags`, `pinned`, `archived`, `before`, `after`, `limit`, `cursor`
- memory.search: FTS5 search `query` (optional `ns`, `limit`)
- memory.cleanup: Delete expired memories (`expiresAt` past)
- memory.tags.add / memory.tags.remove: Manage tag list
- memory.refs.add / memory.refs.remove: Manage refs list
- memory.link: Link two memories (`from_id`, `to_id`, optional `relation`)
- memory.summarize: Create a `summary` referencing a memory
- memory.summarize_thread: Summarize a set of memories (`source_ids`)
- memory.merge: Merge `source_ids` into a new note (optional `target_title`, `ns`)
- export.dump: Export memories to NDJSON (optional `ns`)
- export.import: Import NDJSON string; returns `upserted` count
- task.create: Create a task (`title`, optional `ns`)
- task.update_status: Update task (`id`, `status`, optional `note`)
- task.add_note: Attach a note to a task (`id`, `note`)
- task.list: List recent tasks (`limit`)

## Data Storage

- Backend: SQLite via `Microsoft.Data.Sqlite` with FTS5 for search
- File path: default `AppContext.BaseDirectory/memory.db` or override with `--db <PATH>`
- Tables: `memories` plus FTS virtual table `memories_fts` and mapping `memories_fts_map`

## MCP Client Config Examples

- stdio (Windows):
  - type: `stdio`
  - command: `dotnet`
  - args: `["run", "--project", "src/McpMemoryManager.Server", "--", "--mcp"]`

- websocket:
  - Start server: `dotnet run --project src/McpMemoryManager.Server -- --ws 127.0.0.1:8080`
  - Client URL: `ws://127.0.0.1:8080/ws`

- http:
  - Start server: `dotnet run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765`
  - Client URL: `http://127.0.0.1:8765/mcp`

Note: `--tcp` is useful for ad-hoc testing; many clients don’t support raw TCP.

## Quick Test

- Create a note via TCP
  - Start: `dotnet run --project src/McpMemoryManager.Server -- --tcp 127.0.0.1:8765`
  - Send:
    - `{ "jsonrpc":"2.0", "id":1, "method":"initialize" }`
    - `{ "jsonrpc":"2.0", "id":2, "method":"tools/list" }`
    - `{ "jsonrpc":"2.0", "id":3, "method":"tools/call", "params": { "name":"memory.create", "arguments": { "content":"hello", "ns":"default" } } }`

## Project Layout

- src/McpMemoryManager.Server: main server entrypoint (`Program.cs`)
- src/McpMemoryManager.Server/MemoryStore: SQLite store + schema (`SqliteStore.cs`, `Schema.sql`)
- src/McpMemoryManager.Server/Tools: MCP tool host and APIs (`ToolHost.cs`, `MemoryApi.cs`, `TaskApi.cs`)
- tests/McpMemoryManager.Server.Tests: unit tests

## Development

- Build: `dotnet build`
- Test: `dotnet test`
- Run with HTTP for local MCP: `dotnet run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765`

