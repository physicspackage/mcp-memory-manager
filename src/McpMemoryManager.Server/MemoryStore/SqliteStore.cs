using System.Data;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using McpMemoryManager.Server.Models;

namespace McpMemoryManager.Server.MemoryStore;

public sealed class SqliteStore
{
    private readonly string _dbPath;
    private readonly string _connStr;

    private SqliteStore(string dbPath)
    {
        _dbPath = dbPath;
        _connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public static async Task<SqliteStore> CreateOrOpenAsync(string dbPath)
    {
        var store = new SqliteStore(dbPath);
        await store.InitAsync();
        return store;
    }

    private async Task InitAsync()
    {
        using var conn = Open();
        // Try both likely locations for Schema.sql depending on how you run the app
        var path1 = Path.Combine(AppContext.BaseDirectory, "MemoryStore", "Schema.sql");
        var path2 = Path.Combine(AppContext.BaseDirectory, "src", "McpMemoryManager.Server", "MemoryStore", "Schema.sql");
        var schemaPath = File.Exists(path1) ? path1 : path2;
        var schemaSql = await File.ReadAllTextAsync(schemaPath);
        await conn.ExecuteAsync(schemaSql);
    }

    private IDbConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    #region Memories
    public async Task<string> CreateMemoryAsync(
        string content,
        string type = "note",
        string? title = null,
        string agentId = "",
        string ns = "default",
        Dictionary<string, object>? metadata = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? refs = null,
        double importance = 0.3,
        bool pin = false,
        DateTimeOffset? expiresAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var metaJson = metadata is null ? null : JsonSerializer.Serialize(metadata);
        var tagsJson = JsonSerializer.Serialize(tags?.ToList() ?? new List<string>());
        var refsJson = JsonSerializer.Serialize(refs?.ToList() ?? new List<string>());

        using var conn = Open();
        var sql = @"INSERT INTO memories
                    (id, agent_id, namespace, type, title, content, metadata, tags, refs,
                     importance, pin, archived, created_at, updated_at, expires_at)
                    VALUES
                    (@id, @agent_id, @ns, @type, @title, @content, @metadata, @tags, @refs,
                     @importance, @pin, 0, @created_at, @updated_at, @expires_at)";
        await conn.ExecuteAsync(sql, new
        {
            id,
            agent_id = agentId,
            ns,
            type,
            title,
            content,
            metadata = metaJson,
            tags = tagsJson,
            refs = refsJson,
            importance,
            pin = pin ? 1 : 0,
            created_at = now.ToString("O"),
            updated_at = now.ToString("O"),
            expires_at = expiresAt?.ToString("O")
        });
        return id;
    }

    public async Task<bool> UpdateMemoryAsync(
        string id,
        string? content = null,
        string? title = null,
        Dictionary<string, object>? metadata = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? refs = null,
        double? importance = null,
        bool? pin = null,
        bool? archived = null,
        DateTimeOffset? expiresAt = null)
    {
        var sets = new List<string>();
        var p = new DynamicParameters(new { id });
        if (content != null) { sets.Add("content=@content"); p.Add("content", content); }
        if (title != null) { sets.Add("title=@title"); p.Add("title", title); }
        if (metadata != null) { sets.Add("metadata=@metadata"); p.Add("metadata", JsonSerializer.Serialize(metadata)); }
        if (tags != null) { sets.Add("tags=@tags"); p.Add("tags", JsonSerializer.Serialize(tags.ToList())); }
        if (refs != null) { sets.Add("refs=@refs"); p.Add("refs", JsonSerializer.Serialize(refs.ToList())); }
        if (importance.HasValue) { sets.Add("importance=@importance"); p.Add("importance", importance.Value); }
        if (pin.HasValue) { sets.Add("pin=@pin"); p.Add("pin", pin.Value ? 1 : 0); }
        if (archived.HasValue) { sets.Add("archived=@archived"); p.Add("archived", archived.Value ? 1 : 0); }
        if (expiresAt.HasValue) { sets.Add("expires_at=@expires_at"); p.Add("expires_at", expiresAt.Value.ToString("O")); }
        sets.Add("updated_at=@updated_at"); p.Add("updated_at", DateTimeOffset.UtcNow.ToString("O"));
        if (sets.Count == 1) return false; // only updated_at would change

        using var conn = Open();
        var sql = $"UPDATE memories SET {string.Join(",", sets)} WHERE id=@id";
        var rows = await conn.ExecuteAsync(sql, p);
        return rows > 0;
    }

    public async Task<int> DeleteMemoryAsync(string id, bool hard = false)
    {
        using var conn = Open();
        if (hard)
        {
            return await conn.ExecuteAsync("DELETE FROM memories WHERE id=@id", new { id });
        }
        else
        {
            return await conn.ExecuteAsync(
                "UPDATE memories SET archived=1, updated_at=@now WHERE id=@id",
                new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        }
    }

    public async Task<MemoryItem?> GetMemoryAsync(string id)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>("SELECT * FROM memories WHERE id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<MemoryItem>> ListMemoriesAsync(
        string? ns = null,
        string? type = null,
        int limit = 50)
    {
        using var conn = Open();
        var sql = "SELECT * FROM memories WHERE 1=1" +
                  (ns != null ? " AND namespace=@ns" : "") +
                  (type != null ? " AND type=@type" : "") +
                  " AND archived=0" +
                  " ORDER BY updated_at DESC, id DESC LIMIT @limit";
        var rows = await conn.QueryAsync<dynamic>(sql, new { ns, type, limit });
        return rows.Select(Map).ToList();
    }

    public async Task<(IReadOnlyList<MemoryItem> Items, string? NextCursor)> ListMemoriesAdvancedAsync(
        string? agentId = null,
        string? ns = null,
        IEnumerable<string>? types = null,
        IEnumerable<string>? tags = null,
        bool? pinned = null,
        bool? archived = null,
        DateTimeOffset? before = null,
        DateTimeOffset? after = null,
        int limit = 50,
        string? cursor = null)
    {
        using var conn = Open();
        var where = new List<string> { "1=1" };
        var p = new DynamicParameters();
        if (agentId != null) { where.Add("agent_id=@agentId"); p.Add("agentId", agentId); }
        if (ns != null) { where.Add("namespace=@ns"); p.Add("ns", ns); }
        if (types != null && types.Any()) { where.Add($"type IN ({string.Join(",", types.Select((t,i)=>"@t"+i))})"); int i=0; foreach (var t in types) p.Add("t"+i++, t); }
        if (pinned.HasValue) { where.Add("pin=@pin"); p.Add("pin", pinned.Value ? 1 : 0); }
        if (archived.HasValue) { where.Add("archived=@archived"); p.Add("archived", archived.Value ? 1 : 0); } else { where.Add("archived=0"); }
        if (before.HasValue) { where.Add("updated_at < @before"); p.Add("before", before.Value.ToString("O")); }
        if (after.HasValue) { where.Add("updated_at > @after"); p.Add("after", after.Value.ToString("O")); }
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2)
            {
                where.Add("(memories.updated_at < @c_updated OR (memories.updated_at = @c_updated AND memories.id < @c_id))");
                p.Add("c_updated", parts[0]);
                p.Add("c_id", parts[1]);
            }
        }

        var tagJoin = string.Empty;
        if (tags != null && tags.Any())
        {
            tagJoin = " JOIN json_each(memories.tags) jt ON jt.value IN (" + string.Join(",", tags.Select((t,i)=>"@tag"+i)) + ")";
            int i=0; foreach (var t in tags) p.Add("tag"+i++, t);
        }

        p.Add("limit", limit);
        var sql = $"SELECT memories.* FROM memories{tagJoin} WHERE {string.Join(" AND ", where)} ORDER BY memories.updated_at DESC, memories.id DESC LIMIT @limit";
        var rows = await conn.QueryAsync<dynamic>(sql, p);
        var items = rows.Select(Map).ToList();
        string? next = null;
        if (items.Count == limit)
        {
            var last = items[^1];
            var token = $"{last.UpdatedAt:O}|{last.Id}";
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        }
        return (items, next);
    }

    public async Task<IReadOnlyList<ScoredMemoryItem>> SearchAsync(string query, string? ns = null, int limit = 20)
    {
        using var conn = Open();

        var sql = @"
    SELECT m.*, 0.0 AS score
    FROM memories_fts
    JOIN memories_fts_map AS map ON map.fts_rowid = memories_fts.rowid
    JOIN memories AS m ON m.id = map.mem_id
    WHERE memories_fts MATCH @q
    " + (ns != null ? " AND m.namespace = @ns" : string.Empty) + @"
    ORDER BY m.updated_at DESC
    LIMIT @limit;";

        var rows = await conn.QueryAsync<dynamic>(sql, new { q = query, ns, limit });
        return rows.Select(r => new ScoredMemoryItem(Map(r), (double)r.score)).ToList();
    }

    public async Task<int> CleanupExpireAsync(string? ns = null)
    {
        using var conn = Open();
        var sql = "DELETE FROM memories WHERE expires_at IS NOT NULL AND expires_at < @now" +
                  (ns != null ? " AND namespace=@ns" : "");
        return await conn.ExecuteAsync(sql, new { now = DateTimeOffset.UtcNow.ToString("O"), ns });
    }

    private static MemoryItem Map(dynamic r)
    {
        return new MemoryItem(
            Id: (string)r.id,
            AgentId: (string)r.agent_id,
            Namespace: (string)r.@namespace,
            Type: (string)r.type,
            Title: r.title as string,
            Content: (string)r.content,
            Metadata: r.metadata is null ? null : JsonSerializer.Deserialize<Dictionary<string, object>>((string)r.metadata),
            Tags: JsonSerializer.Deserialize<List<string>>((string)r.tags)!,
            Refs: JsonSerializer.Deserialize<List<string>>((string)r.refs)!,
            Importance: (double)r.importance,
            Pin: ((long)r.pin) == 1,
            Archived: ((long)r.archived) == 1,
            CreatedAt: DateTimeOffset.Parse((string)r.created_at),
            UpdatedAt: DateTimeOffset.Parse((string)r.updated_at),
            ExpiresAt: r.expires_at is null ? null : DateTimeOffset.Parse((string)r.expires_at)
        );
    }

    public async Task<int> UpsertMemoryAsync(MemoryItem item)
    {
        using var conn = Open();
        var sql = @"INSERT INTO memories (id, agent_id, namespace, type, title, content, metadata, tags, refs, importance, pin, archived, created_at, updated_at, expires_at)
                    VALUES (@id, @agent_id, @ns, @type, @title, @content, @metadata, @tags, @refs, @importance, @pin, @archived, @created_at, @updated_at, @expires_at)
                    ON CONFLICT(id) DO UPDATE SET
                        agent_id=excluded.agent_id,
                        namespace=excluded.namespace,
                        type=excluded.type,
                        title=excluded.title,
                        content=excluded.content,
                        metadata=excluded.metadata,
                        tags=excluded.tags,
                        refs=excluded.refs,
                        importance=excluded.importance,
                        pin=excluded.pin,
                        archived=excluded.archived,
                        created_at=excluded.created_at,
                        updated_at=excluded.updated_at,
                        expires_at=excluded.expires_at";
        return await conn.ExecuteAsync(sql, new
        {
            id = item.Id,
            agent_id = item.AgentId,
            ns = item.Namespace,
            type = item.Type,
            title = item.Title,
            content = item.Content,
            metadata = item.Metadata is null ? null : JsonSerializer.Serialize(item.Metadata),
            tags = JsonSerializer.Serialize(item.Tags ?? new List<string>()),
            refs = JsonSerializer.Serialize(item.Refs ?? new List<string>()),
            importance = item.Importance,
            pin = item.Pin ? 1 : 0,
            archived = item.Archived ? 1 : 0,
            created_at = item.CreatedAt.ToString("O"),
            updated_at = item.UpdatedAt.ToString("O"),
            expires_at = item.ExpiresAt?.ToString("O")
        });
    }

    public async Task<IReadOnlyList<MemoryItem>> ExportMemoriesAsync(string? ns = null)
    {
        using var conn = Open();
        var sql = "SELECT * FROM memories" + (ns != null ? " WHERE namespace=@ns" : "") + " ORDER BY created_at ASC";
        var rows = await conn.QueryAsync<dynamic>(sql, new { ns });
        return rows.Select(Map).ToList();
    }
    
    #endregion
}
