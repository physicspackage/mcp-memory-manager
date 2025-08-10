using McpMemoryManager.Server.MemoryStore;

namespace McpMemoryManager.Server.Tests;

internal sealed class TestStore : IAsyncDisposable
{
    public string RootDir { get; }
    public string DbPath { get; }
    public SqliteStore Store { get; }

    private TestStore(string rootDir, string dbPath, SqliteStore store)
    {
        RootDir = rootDir;
        DbPath = dbPath;
        Store = store;
    }

    public static async Task<TestStore> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-mm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "test.db");
        var store = await SqliteStore.CreateOrOpenAsync(db);
        return new TestStore(root, db, store);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Task.Yield();
            if (File.Exists(DbPath))
            {
                try { File.Delete(DbPath); } catch { /* ignore */ }
                var wal = DbPath + "-wal";
                var shm = DbPath + "-shm";
                if (File.Exists(wal)) try { File.Delete(wal); } catch { }
                if (File.Exists(shm)) try { File.Delete(shm); } catch { }
            }
            if (Directory.Exists(RootDir))
            {
                try { Directory.Delete(RootDir, recursive: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }
}

