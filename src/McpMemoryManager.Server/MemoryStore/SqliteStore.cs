using System.Data;
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
                  " ORDER BY updated_at DESC LIMIT @limit";
        var rows = await conn.QueryAsync<dynamic>(sql, new { ns, type, limit });
        return rows.Select(Map).ToList();
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

    #endregion
}