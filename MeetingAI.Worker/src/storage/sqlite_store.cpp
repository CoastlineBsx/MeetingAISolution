#include "pch.h"
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"
#include <iostream>
#include <string>

static void DumpDbPath(sqlite3* db) {
    sqlite3_stmt* st = nullptr;
    if (sqlite3_prepare_v2(db, "PRAGMA database_list;", -1, &st, nullptr) == SQLITE_OK) {
        while (sqlite3_step(st) == SQLITE_ROW) {
            // 列 1: name, 列 2: file
            const char* name = (const char*)sqlite3_column_text(st, 1);
            const char* file = (const char*)sqlite3_column_text(st, 2);
            std::cerr << "[DB] attached '" << (name ? name : "") << "' -> " << (file ? file : "") << "\n";
        }
    }
    sqlite3_finalize(st);
}

static void DumpSchema(sqlite3* db) {
    sqlite3_stmt* st = nullptr;
    const char* q = "SELECT type,name FROM sqlite_master ORDER BY type,name;";
    if (sqlite3_prepare_v2(db, q, -1, &st, nullptr) == SQLITE_OK) {
        while (sqlite3_step(st) == SQLITE_ROW) {
            const char* t = (const char*)sqlite3_column_text(st, 0);
            const char* n = (const char*)sqlite3_column_text(st, 1);
            std::cerr << "[DB] " << (t ? t : "") << "  " << (n ? n : "") << "\n";
        }
    }
    sqlite3_finalize(st);
}

static bool ExecSQL(sqlite3* db, const char* sql) {
    char* err = nullptr;
    int rc = sqlite3_exec(db, sql, nullptr, nullptr, &err);
    if (rc != SQLITE_OK) {
        std::cerr << "[DB] SQL error: " << (err ? err : "") << "\n";
        sqlite3_free(err);
        return false;
    }
    else {
        std::cerr << "[DB] open path = " << meetingai::util::getDatabasePath() << "\n";
        DumpDbPath(db);  // 打印数据库文件路径信息
    }
    return true;
}

bool InitDatabaseOnce() {
    // 用 UTF-8 打开（跨平台更稳）
    sqlite3* db = nullptr;
    const std::string db8 = meetingai::util::getDatabasePath();
    if (sqlite3_open(db8.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: " << sqlite3_errmsg(db) << "\n";
        return false;
    }

    // 基本设置
    sqlite3_busy_timeout(db, 5000);
    ExecSQL(db, "PRAGMA foreign_keys=ON;");
    if (!ExecSQL(db, "PRAGMA journal_mode=WAL;")) { sqlite3_close(db); return false; }
    if (!ExecSQL(db, "PRAGMA synchronous=NORMAL;")) { sqlite3_close(db); return false; }

    // 创建 transcripts 表，存转录（保持你的原样）
    const char* create_sql =
        "CREATE TABLE IF NOT EXISTS transcripts ("
        "  id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "  speaker TEXT,"
        "  text TEXT,"
        "  ts REAL,"
        "  created_at DATETIME DEFAULT CURRENT_TIMESTAMP"
        ");";
    if (!ExecSQL(db, create_sql)) { sqlite3_close(db); return false; }

    // 你的主 DDL（原样保留）
    const char* ddl = R"SQL(
BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS meeting (
  id               INTEGER PRIMARY KEY,
  ext_source       TEXT,
  title            TEXT,
  tz               TEXT DEFAULT 'Europe/London',
  started_at_utc   DATETIME,
  ended_at_utc     DATETIME
);

CREATE TABLE IF NOT EXISTS participant (
  id               INTEGER PRIMARY KEY,
  meeting_id       INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  display_name     TEXT,
  external_user_id TEXT,
  role             TEXT,
  UNIQUE(meeting_id, external_user_id)
);

CREATE TABLE IF NOT EXISTS stream (
  id               INTEGER PRIMARY KEY,
  meeting_id       INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  type             TEXT NOT NULL,
  channel          TEXT,
  sample_rate_hz   INTEGER,
  media_path       TEXT,
  device_info      TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS uniq_stream_per_meeting ON stream(meeting_id, type);
CREATE INDEX IF NOT EXISTS idx_stream_meeting ON stream(meeting_id);

CREATE TABLE IF NOT EXISTS segment (
  id               INTEGER PRIMARY KEY,
  meeting_id       INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  stream_id        INTEGER REFERENCES stream(id) ON DELETE SET NULL,
  seq              INTEGER NOT NULL,
  start_ms         INTEGER NOT NULL,
  end_ms           INTEGER NOT NULL,
  speaker_hint     TEXT,
  origin           TEXT,
  source_meta      TEXT,
  text_raw         TEXT,
  confidence_avg   REAL,
  is_final         INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_segment_meeting ON segment(meeting_id, start_ms);

CREATE TABLE IF NOT EXISTS revision (
  id               INTEGER PRIMARY KEY,
  segment_id       INTEGER NOT NULL REFERENCES segment(id) ON DELETE CASCADE,
  parent_rev_id    INTEGER REFERENCES revision(id) ON DELETE SET NULL,
  stage            TEXT NOT NULL,
  model_id         INTEGER REFERENCES model_registry(id),
  created_at_utc   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  text_final       TEXT NOT NULL,
  confidence_avg   REAL,
  speaker_id       INTEGER REFERENCES participant(id),
  UNIQUE(segment_id, stage)
);
CREATE INDEX IF NOT EXISTS idx_revision_segment ON revision(segment_id);

CREATE TABLE IF NOT EXISTS model_registry (
  id               INTEGER PRIMARY KEY,
  name             TEXT NOT NULL,
  version          TEXT NOT NULL,
  type             TEXT NOT NULL,
  params_json      TEXT,
  runtime          TEXT
);

CREATE VIRTUAL TABLE IF NOT EXISTS fts_revision USING fts5(
  text_final, content='revision', content_rowid='id'
);
CREATE TRIGGER IF NOT EXISTS trg_rev_ai AFTER INSERT ON revision BEGIN
  INSERT INTO fts_revision(rowid, text_final) VALUES (new.id, new.text_final);
END;
CREATE TRIGGER IF NOT EXISTS trg_rev_au AFTER UPDATE ON revision BEGIN
  INSERT INTO fts_revision(fts_revision, rowid, text_final) VALUES ('delete', old.id, old.text_final);
  INSERT INTO fts_revision(rowid, text_final) VALUES (new.id, new.text_final);
END;
CREATE TRIGGER IF NOT EXISTS trg_rev_ad AFTER DELETE ON revision BEGIN
  INSERT INTO fts_revision(fts_revision, rowid, text_final) VALUES ('delete', old.id, old.text_final);
END;

COMMIT;
)SQL";

    if (!ExecSQL(db, ddl)) { sqlite3_close(db); return false; }
    DumpSchema(db);
    sqlite3_close(db);
    return true;
}

bool InsertTranscript(const std::string& speaker,
    const std::string& text,
    double timestamp) {
    sqlite3* db = nullptr;
    const std::string db8 = meetingai::util::getDatabasePath();
    if (sqlite3_open(db8.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: " << sqlite3_errmsg(db) << "\n";
        return false;
    }

    sqlite3_busy_timeout(db, 5000);
    ExecSQL(db, "PRAGMA foreign_keys=ON;");
    sqlite3_exec(db, "BEGIN IMMEDIATE;", nullptr, nullptr, nullptr);

    // 1) 若无 meeting，建一条
    sqlite3_int64 mid = 0;
    {
        const char* q = "SELECT id FROM meeting ORDER BY id DESC LIMIT 1;";
        sqlite3_stmt* st = nullptr;
        if (sqlite3_prepare_v2(db, q, -1, &st, nullptr) == SQLITE_OK) {
            if (sqlite3_step(st) == SQLITE_ROW) mid = sqlite3_column_int64(st, 0);
        }
        sqlite3_finalize(st);
        if (mid == 0) {
            const char* ins = "INSERT INTO meeting(ext_source,title,tz,started_at_utc) "
                "VALUES('local','Ad-hoc','Europe/London',datetime('now'));";
            if (!ExecSQL(db, ins)) { sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr); sqlite3_close(db); return false; }
            mid = sqlite3_last_insert_rowid(db);
        }
    }

    // 2) 若无 mic 流，建一条
    sqlite3_int64 sid = 0;
    {
        const char* q = "SELECT id FROM stream WHERE meeting_id=? AND type='mic' LIMIT 1;";
        sqlite3_stmt* st = nullptr;
        sqlite3_prepare_v2(db, q, -1, &st, nullptr);
        sqlite3_bind_int64(st, 1, mid);
        if (sqlite3_step(st) == SQLITE_ROW) sid = sqlite3_column_int64(st, 0);
        sqlite3_finalize(st);

        if (sid == 0) {
            const char* ins = "INSERT INTO stream(meeting_id,type,channel) VALUES(?,'mic','mono');";
            sqlite3_stmt* st2 = nullptr;
            sqlite3_prepare_v2(db, ins, -1, &st2, nullptr);
            sqlite3_bind_int64(st2, 1, mid);
            if (sqlite3_step(st2) != SQLITE_DONE) {
                std::cerr << "[DB] insert stream failed: " << sqlite3_errmsg(db) << "\n";
                sqlite3_finalize(st2);
                sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
                sqlite3_close(db);
                return false;
            }
            sqlite3_finalize(st2);
            sid = sqlite3_last_insert_rowid(db);
        }
    }

    // 3) 插 segment（把 timestamp 秒 → 毫秒）
    sqlite3_int64 segId = 0;
    {
        const char* sql =
            "INSERT INTO segment(meeting_id,stream_id,seq,start_ms,end_ms,speaker_hint,origin,source_meta,text_raw,confidence_avg,is_final) "
            "VALUES(?,?,(SELECT IFNULL(MAX(seq),0)+1 FROM segment WHERE stream_id=?),?,?,?,?,?,?,?,1);";
        sqlite3_stmt* st = nullptr;
        sqlite3_prepare_v2(db, sql, -1, &st, nullptr);
        sqlite3_bind_int64(st, 1, mid);
        sqlite3_bind_int64(st, 2, sid);
        sqlite3_bind_int64(st, 3, sid);
        const sqlite3_int64 ms = (sqlite3_int64)(timestamp * 1000.0);
        sqlite3_bind_int64(st, 4, ms);
        sqlite3_bind_int64(st, 5, ms);
        sqlite3_bind_text(st, 6, speaker.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(st, 7, "asr_mic", -1, SQLITE_TRANSIENT);
        sqlite3_bind_null(st, 8); // source_meta=NULL
        sqlite3_bind_text(st, 9, text.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_double(st, 10, 1.0);
        if (sqlite3_step(st) != SQLITE_DONE) {
            std::cerr << "[DB] insert segment failed: " << sqlite3_errmsg(db) << "\n";
            sqlite3_finalize(st);
            sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
            sqlite3_close(db);
            return false;
        }
        sqlite3_finalize(st);
        segId = sqlite3_last_insert_rowid(db);
    }

    // 4) 确保 Whisper 已登记 → 插 revision(live_asr)
    int modelId = 0;
    {
        const char* q = "SELECT id FROM model_registry WHERE name='Whisper' AND type='asr' LIMIT 1;";
        sqlite3_stmt* st = nullptr;
        sqlite3_prepare_v2(db, q, -1, &st, nullptr);
        if (sqlite3_step(st) == SQLITE_ROW) modelId = sqlite3_column_int(st, 0);
        sqlite3_finalize(st);
        if (modelId == 0) {
            const char* ins = "INSERT INTO model_registry(name,version,type,runtime) VALUES('Whisper','local','asr','cpu');";
            if (!ExecSQL(db, ins)) {
                sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
                sqlite3_close(db);
                return false;
            }
            modelId = (int)sqlite3_last_insert_rowid(db);
        }
    }

    {
        const char* sql =
            "INSERT INTO revision(segment_id,stage,model_id,text_final,confidence_avg) "
            "VALUES(?, 'live_asr', ?, ?, 1.0);";
        sqlite3_stmt* st = nullptr;
        sqlite3_prepare_v2(db, sql, -1, &st, nullptr);
        sqlite3_bind_int64(st, 1, segId);
        sqlite3_bind_int(st, 2, modelId);
        sqlite3_bind_text(st, 3, text.c_str(), -1, SQLITE_TRANSIENT);
        if (sqlite3_step(st) != SQLITE_DONE) {
            std::cerr << "[DB] insert revision failed: " << sqlite3_errmsg(db) << "\n";
            sqlite3_finalize(st);
            sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
            sqlite3_close(db);
            return false;
        }
        sqlite3_finalize(st);
    }

    sqlite3_exec(db, "COMMIT;", nullptr, nullptr, nullptr);
    sqlite3_close(db);
    return true;
}
