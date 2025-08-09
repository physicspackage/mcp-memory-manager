-- Main table
CREATE TABLE IF NOT EXISTS memories (
  id TEXT PRIMARY KEY,
  agent_id TEXT NOT NULL DEFAULT '',
  namespace TEXT NOT NULL DEFAULT 'default',
  type TEXT NOT NULL DEFAULT 'note',
  title TEXT,
  content TEXT NOT NULL,
  metadata TEXT,
  tags TEXT NOT NULL DEFAULT '[]', -- JSON array
  refs TEXT NOT NULL DEFAULT '[]', -- JSON array
  importance REAL NOT NULL DEFAULT 0.3,
  pin INTEGER NOT NULL DEFAULT 0,
  archived INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  expires_at TEXT
);

-- Contentless FTS5 index referencing memories by rowid mapping table
CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
  content,
  tags,
  content=''
);

-- Mapping from memory id to FTS rowid (since we use contentless FTS)
CREATE TABLE IF NOT EXISTS memories_fts_map(
  mem_id TEXT PRIMARY KEY,
  fts_rowid INTEGER
);

-- Triggers to keep FTS in sync
CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
  INSERT INTO memories_fts(rowid, content, tags)
  VALUES(NULL, NEW.content, NEW.tags);
  INSERT INTO memories_fts_map(mem_id, fts_rowid)
  VALUES(NEW.id, last_insert_rowid());
END;

CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
  UPDATE memories_fts
     SET content = NEW.content,
         tags = NEW.tags
   WHERE rowid = (SELECT fts_rowid FROM memories_fts_map WHERE mem_id = NEW.id);
END;

CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
  DELETE FROM memories_fts WHERE rowid = (SELECT fts_rowid FROM memories_fts_map WHERE mem_id = OLD.id);
  DELETE FROM memories_fts_map WHERE mem_id = OLD.id;
END;