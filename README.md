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
