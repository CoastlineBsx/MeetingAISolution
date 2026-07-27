#include "pch.h"
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"
#include <algorithm>
#include <iostream>
#include <string>
#include <cmath>
#include <cstdint>
#include <mutex>

namespace {

std::mutex g_meetingWriteMutex;

bool OpenMeetingDatabase(sqlite3** db) {
    *db = nullptr;
    const std::string path = meetingai::util::getDatabasePath();
    if (sqlite3_open(path.c_str(), db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: "
                  << (*db ? sqlite3_errmsg(*db) : "unknown") << "\n";
        if (*db) {
            sqlite3_close(*db);
            *db = nullptr;
        }
        return false;
    }

    sqlite3_busy_timeout(*db, 5000);
    if (sqlite3_exec(*db, "PRAGMA foreign_keys=ON;", nullptr, nullptr, nullptr)
        != SQLITE_OK) {
        std::cerr << "[DB] enabling foreign keys failed: "
                  << sqlite3_errmsg(*db) << "\n";
        sqlite3_close(*db);
        *db = nullptr;
        return false;
    }
    return true;
}

bool BeginTransaction(sqlite3* db) {
    return sqlite3_exec(db, "BEGIN IMMEDIATE;", nullptr, nullptr, nullptr)
        == SQLITE_OK;
}

bool CommitTransaction(sqlite3* db) {
    return sqlite3_exec(db, "COMMIT;", nullptr, nullptr, nullptr) == SQLITE_OK;
}

void RollbackTransaction(sqlite3* db) {
    sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
}

int EnsureModel(
    sqlite3* db,
    const char* name,
    const char* version,
    const char* type,
    const char* runtime) {
    sqlite3_stmt* select = nullptr;
    const char* query =
        "SELECT id FROM model_registry "
        "WHERE name=? AND version=? AND type=? AND runtime=? LIMIT 1;";
    if (sqlite3_prepare_v2(db, query, -1, &select, nullptr) != SQLITE_OK) {
        return 0;
    }
    sqlite3_bind_text(select, 1, name, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(select, 2, version, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(select, 3, type, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(select, 4, runtime, -1, SQLITE_TRANSIENT);

    int modelId = 0;
    if (sqlite3_step(select) == SQLITE_ROW) {
        modelId = sqlite3_column_int(select, 0);
    }
    sqlite3_finalize(select);
    if (modelId != 0) {
        return modelId;
    }

    sqlite3_stmt* insert = nullptr;
    const char* sql =
        "INSERT INTO model_registry(name,version,type,runtime) "
        "VALUES(?,?,?,?);";
    if (sqlite3_prepare_v2(db, sql, -1, &insert, nullptr) != SQLITE_OK) {
        return 0;
    }
    sqlite3_bind_text(insert, 1, name, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(insert, 2, version, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(insert, 3, type, -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(insert, 4, runtime, -1, SQLITE_TRANSIENT);
    if (sqlite3_step(insert) == SQLITE_DONE) {
        modelId = static_cast<int>(sqlite3_last_insert_rowid(db));
    }
    sqlite3_finalize(insert);
    return modelId;
}

} // namespace

// ===== 余弦相似度计算函数 =====
static void cosine_similarity_func(sqlite3_context* ctx, int argc, sqlite3_value** argv) {
    if (argc != 2) {
        sqlite3_result_error(ctx, "cosine_similarity requires 2 BLOB arguments", -1);
        return;
    }

    const void* blob1 = sqlite3_value_blob(argv[0]);
    const void* blob2 = sqlite3_value_blob(argv[1]);
    int size1 = sqlite3_value_bytes(argv[0]);
    int size2 = sqlite3_value_bytes(argv[1]);

    if (!blob1 || !blob2 || size1 != size2 || size1 != 1024 * sizeof(float)) {
        sqlite3_result_null(ctx);
        return;
    }

    const float* v1 = (const float*)blob1;
    const float* v2 = (const float*)blob2;

    float dot = 0.0f, norm1 = 0.0f, norm2 = 0.0f;
    for (int i = 0; i < 1024; i++) {
        dot += v1[i] * v2[i];
        norm1 += v1[i] * v1[i];
        norm2 += v2[i] * v2[i];
    }

    if (norm1 == 0.0f || norm2 == 0.0f) {
        sqlite3_result_null(ctx);
        return;
    }

    float cosine = dot / (sqrtf(norm1) * sqrtf(norm2));
    sqlite3_result_double(ctx, cosine);
}

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

static bool EnsureTableColumn(
    sqlite3* db,
    const char* tableName,
    const char* columnName,
    const char* declaration) {
    const std::string pragma = "PRAGMA table_info(" +
        std::string(tableName) + ");";
    sqlite3_stmt* statement = nullptr;
    if (sqlite3_prepare_v2(
        db,
        pragma.c_str(),
        -1,
        &statement,
        nullptr) != SQLITE_OK) {
        return false;
    }

    bool found = false;
    while (sqlite3_step(statement) == SQLITE_ROW) {
        const char* existing = reinterpret_cast<const char*>(
            sqlite3_column_text(statement, 1));
        if (existing && std::string(existing) == columnName) {
            found = true;
            break;
        }
    }
    sqlite3_finalize(statement);
    if (found) {
        return true;
    }

    const std::string sql =
        "ALTER TABLE " + std::string(tableName) + " ADD COLUMN " +
        std::string(columnName) + " " + declaration + ";";
    return ExecSQL(db, sql.c_str());
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

    // 注册余弦相似度函数
    if (sqlite3_create_function(db, "cosine_similarity", 2, SQLITE_UTF8, nullptr,
                                 cosine_similarity_func, nullptr, nullptr) != SQLITE_OK) {
        std::cerr << "[DB] Failed to register cosine_similarity function\n";
        sqlite3_close(db);
        return false;
    }
    std::cerr << "[DB] ✅ cosine_similarity function registered\n";

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
  ended_at_utc     DATETIME,
  preparation_id   INTEGER,
  context_title    TEXT,
  context_snapshot_json TEXT,
  hotword_count    INTEGER NOT NULL DEFAULT 0,
  rag_enabled      INTEGER NOT NULL DEFAULT 0
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

CREATE TABLE IF NOT EXISTS transcription_run (
  id               INTEGER PRIMARY KEY,
  meeting_id       INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  engine           TEXT NOT NULL,
  model_name       TEXT NOT NULL,
  runtime          TEXT NOT NULL,
  status           TEXT NOT NULL DEFAULT 'queued',
  progress         INTEGER NOT NULL DEFAULT 0,
  translation_mode TEXT,
  hotwords_text    TEXT,
  created_at_utc   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  started_at_utc   DATETIME,
  completed_at_utc DATETIME,
  error_text       TEXT,
  is_canonical     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_transcription_run_meeting
ON transcription_run(meeting_id, id);

CREATE TABLE IF NOT EXISTS segment (
  id               INTEGER PRIMARY KEY,
  meeting_id       INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  stream_id        INTEGER REFERENCES stream(id) ON DELETE SET NULL,
  transcription_run_id INTEGER REFERENCES transcription_run(id) ON DELETE CASCADE,
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
CREATE UNIQUE INDEX IF NOT EXISTS uniq_model_registry
ON model_registry(name, version, type, runtime);

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

CREATE TABLE IF NOT EXISTS documents (
  id INTEGER PRIMARY KEY,
  title TEXT NOT NULL,
  source_type TEXT,
  file_path TEXT,
  upload_time DATETIME DEFAULT CURRENT_TIMESTAMP,
  content_preview TEXT
);

CREATE TABLE IF NOT EXISTS document_chunks (
  id INTEGER PRIMARY KEY,
  doc_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  chunk_index INTEGER NOT NULL,
  text TEXT NOT NULL,
  embedding BLOB NOT NULL,
  token_count INTEGER,
  UNIQUE(doc_id, chunk_index)
);
CREATE INDEX IF NOT EXISTS idx_chunk_doc ON document_chunks(doc_id);

CREATE TABLE IF NOT EXISTS meeting_summary (
  id                         INTEGER PRIMARY KEY,
  meeting_id                 INTEGER NOT NULL REFERENCES meeting(id) ON DELETE CASCADE,
  revision_no                INTEGER NOT NULL,
  covered_through_segment_id INTEGER REFERENCES segment(id) ON DELETE SET NULL,
  created_at_utc             DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  model_name                 TEXT NOT NULL,
  prompt_version             TEXT NOT NULL,
  summary_text               TEXT NOT NULL,
  is_final                   INTEGER NOT NULL DEFAULT 0,
  transcription_run_id       INTEGER REFERENCES transcription_run(id) ON DELETE SET NULL,
  summary_kind               TEXT NOT NULL DEFAULT 'rolling_quick',
  UNIQUE(meeting_id, revision_no)
);
CREATE INDEX IF NOT EXISTS idx_summary_meeting
ON meeting_summary(meeting_id, revision_no);

-- 清理由早期 Worker 启动自检写入的测试字幕。条件刻意收紧，
-- 不会删除用户转录出来的正常内容。
DELETE FROM segment
WHERE speaker_hint='system'
  AND text_raw='worker started'
  AND start_ms=0
  AND end_ms=0;

PRAGMA user_version=4;

COMMIT;
)SQL";

    if (!ExecSQL(db, ddl)) { sqlite3_close(db); return false; }
    if (!EnsureTableColumn(db, "meeting", "preparation_id", "INTEGER") ||
        !EnsureTableColumn(db, "meeting", "context_title", "TEXT") ||
        !EnsureTableColumn(
            db, "meeting", "context_snapshot_json", "TEXT") ||
        !EnsureTableColumn(
            db,
            "meeting",
            "hotword_count",
            "INTEGER NOT NULL DEFAULT 0") ||
        !EnsureTableColumn(
            db,
            "meeting",
            "rag_enabled",
            "INTEGER NOT NULL DEFAULT 0") ||
        !EnsureTableColumn(
            db,
            "segment",
            "transcription_run_id",
            "INTEGER REFERENCES transcription_run(id) ON DELETE CASCADE") ||
        !EnsureTableColumn(
            db,
            "meeting_summary",
            "transcription_run_id",
            "INTEGER REFERENCES transcription_run(id) ON DELETE SET NULL") ||
        !EnsureTableColumn(
            db,
            "meeting_summary",
            "summary_kind",
            "TEXT NOT NULL DEFAULT 'rolling_quick'") ||
        !ExecSQL(db, "PRAGMA user_version=4;")) {
        sqlite3_close(db);
        return false;
    }
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
        const char* q =
            "SELECT id FROM meeting "
            "WHERE ended_at_utc IS NULL AND ext_source='local' "
            "ORDER BY id DESC LIMIT 1;";
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

std::int64_t BeginStreamingMeeting(
    const std::vector<std::string>& sources,
    int sampleRateHz,
    const std::string& contextTitle,
    std::int64_t preparationId,
    const std::string& contextSnapshotJson,
    int hotwordCount,
    bool ragEnabled) {
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db) || !BeginTransaction(db)) {
        if (db) sqlite3_close(db);
        return 0;
    }

    // Worker/Host 异常退出可能留下 ended_at_utc=NULL 的旧会话。
    // 新会议开始时先封口旧会话，确保每次 Start 都有独立 meeting。
    if (sqlite3_exec(
        db,
        "UPDATE meeting SET ended_at_utc=datetime('now') "
        "WHERE ended_at_utc IS NULL;",
        nullptr,
        nullptr,
        nullptr) != SQLITE_OK) {
        std::cerr << "[DB] closing stale meetings failed: "
                  << sqlite3_errmsg(db) << "\n";
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    const std::string meetingTitle = contextTitle.empty()
        ? "Streaming Meeting"
        : contextTitle;
    const char* insertMeeting =
        "INSERT INTO meeting("
        "ext_source,title,tz,started_at_utc,preparation_id,context_title,"
        "context_snapshot_json,hotword_count,rag_enabled) "
        "VALUES('streaming',?,'Europe/London',datetime('now'),?,?,?,?,?);";
    sqlite3_stmt* meetingStatement = nullptr;
    if (sqlite3_prepare_v2(
        db,
        insertMeeting,
        -1,
        &meetingStatement,
        nullptr) != SQLITE_OK) {
        std::cerr << "[DB] insert streaming meeting failed: "
                  << sqlite3_errmsg(db) << "\n";
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_bind_text(
        meetingStatement,
        1,
        meetingTitle.c_str(),
        -1,
        SQLITE_TRANSIENT);
    if (preparationId > 0) {
        sqlite3_bind_int64(meetingStatement, 2, preparationId);
    }
    else {
        sqlite3_bind_null(meetingStatement, 2);
    }
    if (!contextTitle.empty()) {
        sqlite3_bind_text(
            meetingStatement,
            3,
            contextTitle.c_str(),
            -1,
            SQLITE_TRANSIENT);
    }
    else {
        sqlite3_bind_null(meetingStatement, 3);
    }
    if (!contextSnapshotJson.empty()) {
        sqlite3_bind_text(
            meetingStatement,
            4,
            contextSnapshotJson.c_str(),
            -1,
            SQLITE_TRANSIENT);
    }
    else {
        sqlite3_bind_null(meetingStatement, 4);
    }
    sqlite3_bind_int(
        meetingStatement,
        5,
        std::max(0, hotwordCount));
    sqlite3_bind_int(meetingStatement, 6, ragEnabled ? 1 : 0);
    const int meetingInsertResult = sqlite3_step(meetingStatement);
    sqlite3_finalize(meetingStatement);
    if (meetingInsertResult != SQLITE_DONE) {
        std::cerr << "[DB] insert streaming meeting failed: "
                  << sqlite3_errmsg(db) << "\n";
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    const std::int64_t meetingId = sqlite3_last_insert_rowid(db);

    const char* insertStream =
        "INSERT INTO stream(meeting_id,type,channel,sample_rate_hz) "
        "VALUES(?,?,'mono',?);";
    for (const std::string& source : sources) {
        sqlite3_stmt* statement = nullptr;
        if (sqlite3_prepare_v2(
            db,
            insertStream,
            -1,
            &statement,
            nullptr) != SQLITE_OK) {
            RollbackTransaction(db);
            sqlite3_close(db);
            return 0;
        }
        sqlite3_bind_int64(statement, 1, meetingId);
        sqlite3_bind_text(
            statement,
            2,
            source.c_str(),
            -1,
            SQLITE_TRANSIENT);
        sqlite3_bind_int(statement, 3, sampleRateHz);
        const int rc = sqlite3_step(statement);
        sqlite3_finalize(statement);
        if (rc != SQLITE_DONE) {
            std::cerr << "[DB] insert streaming source failed: "
                      << sqlite3_errmsg(db) << "\n";
            RollbackTransaction(db);
            sqlite3_close(db);
            return 0;
        }
    }

    if (!CommitTransaction(db)) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_close(db);
    std::cout << "[DB] streaming meeting started, id="
              << meetingId << "\n";
    return meetingId;
}

bool EndStreamingMeeting(std::int64_t meetingId) {
    if (meetingId <= 0) {
        return false;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return false;
    }
    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "UPDATE meeting SET ended_at_utc=datetime('now') "
        "WHERE id=? AND ended_at_utc IS NULL;";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        sqlite3_close(db);
        return false;
    }
    sqlite3_bind_int64(statement, 1, meetingId);
    const bool ok = sqlite3_step(statement) == SQLITE_DONE;
    sqlite3_finalize(statement);
    sqlite3_close(db);
    return ok;
}

bool UpdateStreamingMediaPath(
    std::int64_t meetingId,
    const std::string& source,
    const std::string& mediaPath) {
    if (meetingId <= 0 || source.empty() || mediaPath.empty()) {
        return false;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return false;
    }
    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "UPDATE stream SET media_path=? WHERE meeting_id=? AND type=?;";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        sqlite3_close(db);
        return false;
    }
    sqlite3_bind_text(
        statement, 1, mediaPath.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(statement, 2, meetingId);
    sqlite3_bind_text(
        statement, 3, source.c_str(), -1, SQLITE_TRANSIENT);
    const bool ok =
        sqlite3_step(statement) == SQLITE_DONE &&
        sqlite3_changes(db) > 0;
    sqlite3_finalize(statement);
    sqlite3_close(db);
    return ok;
}

std::int64_t InsertStreamingFinal(
    std::int64_t meetingId,
    const std::string& source,
    std::int64_t utteranceId,
    std::int64_t startMs,
    std::int64_t endMs,
    const std::string& rawText,
    const std::string& normalizedText) {
    if (meetingId <= 0 || normalizedText.empty()) {
        return 0;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db) || !BeginTransaction(db)) {
        if (db) sqlite3_close(db);
        return 0;
    }

    std::int64_t streamId = 0;
    {
        sqlite3_stmt* statement = nullptr;
        const char* sql =
            "SELECT id FROM stream WHERE meeting_id=? AND type=? LIMIT 1;";
        if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) == SQLITE_OK) {
            sqlite3_bind_int64(statement, 1, meetingId);
            sqlite3_bind_text(
                statement,
                2,
                source.c_str(),
                -1,
                SQLITE_TRANSIENT);
            if (sqlite3_step(statement) == SQLITE_ROW) {
                streamId = sqlite3_column_int64(statement, 0);
            }
        }
        sqlite3_finalize(statement);
    }
    if (streamId == 0) {
        std::cerr << "[DB] no stream for meeting=" << meetingId
                  << " source=" << source << "\n";
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    std::int64_t segmentId = 0;
    {
        sqlite3_stmt* statement = nullptr;
        const char* sql =
            "INSERT INTO segment("
            "meeting_id,stream_id,seq,start_ms,end_ms,speaker_hint,origin,"
            "source_meta,text_raw,confidence_avg,is_final"
            ") VALUES(?,?,?,?,?,?,?,?,?,NULL,1);";
        if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
            RollbackTransaction(db);
            sqlite3_close(db);
            return 0;
        }
        const std::string speaker =
            source == "system" ? "对方" : "我方";
        const std::string origin = "asr_" + source;
        const std::string sourceMeta =
            "{\"source\":\"" + source + "\",\"utterance_id\":"
            + std::to_string(utteranceId) + "}";
        sqlite3_bind_int64(statement, 1, meetingId);
        sqlite3_bind_int64(statement, 2, streamId);
        sqlite3_bind_int64(statement, 3, utteranceId);
        sqlite3_bind_int64(statement, 4, startMs);
        sqlite3_bind_int64(statement, 5, endMs);
        sqlite3_bind_text(
            statement, 6, speaker.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(
            statement, 7, origin.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(
            statement, 8, sourceMeta.c_str(), -1, SQLITE_TRANSIENT);
        sqlite3_bind_text(
            statement, 9, rawText.c_str(), -1, SQLITE_TRANSIENT);
        if (sqlite3_step(statement) == SQLITE_DONE) {
            segmentId = sqlite3_last_insert_rowid(db);
        }
        else {
            std::cerr << "[DB] insert streaming segment failed: "
                      << sqlite3_errmsg(db) << "\n";
        }
        sqlite3_finalize(statement);
    }
    if (segmentId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    const int modelId = EnsureModel(
        db,
        "Sherpa-ONNX Zipformer",
        "2023-02-20",
        "asr",
        "onnxruntime");
    if (modelId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    std::int64_t rawRevisionId = 0;
    {
        sqlite3_stmt* statement = nullptr;
        const char* sql =
            "INSERT INTO revision("
            "segment_id,stage,model_id,text_final,confidence_avg"
            ") VALUES(?,'asr_raw',?,?,NULL);";
        if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) == SQLITE_OK) {
            sqlite3_bind_int64(statement, 1, segmentId);
            sqlite3_bind_int(statement, 2, modelId);
            sqlite3_bind_text(
                statement,
                3,
                rawText.c_str(),
                -1,
                SQLITE_TRANSIENT);
            if (sqlite3_step(statement) == SQLITE_DONE) {
                rawRevisionId = sqlite3_last_insert_rowid(db);
            }
        }
        sqlite3_finalize(statement);
    }
    if (rawRevisionId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    {
        sqlite3_stmt* statement = nullptr;
        const char* sql =
            "INSERT INTO revision("
            "segment_id,parent_rev_id,stage,model_id,text_final,confidence_avg"
            ") VALUES(?,?,'asr_normalized',?,?,NULL);";
        if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
            RollbackTransaction(db);
            sqlite3_close(db);
            return 0;
        }
        sqlite3_bind_int64(statement, 1, segmentId);
        sqlite3_bind_int64(statement, 2, rawRevisionId);
        sqlite3_bind_int(statement, 3, modelId);
        sqlite3_bind_text(
            statement,
            4,
            normalizedText.c_str(),
            -1,
            SQLITE_TRANSIENT);
        const int rc = sqlite3_step(statement);
        sqlite3_finalize(statement);
        if (rc != SQLITE_DONE) {
            std::cerr << "[DB] insert normalized revision failed: "
                      << sqlite3_errmsg(db) << "\n";
            RollbackTransaction(db);
            sqlite3_close(db);
            return 0;
        }
    }

    if (!CommitTransaction(db)) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_close(db);
    return segmentId;
}

bool InsertStreamingTranslation(
    std::int64_t segmentId,
    const std::string& targetLanguage,
    const std::string& translatedText) {
    if (segmentId <= 0 || translatedText.empty()) {
        return false;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db) || !BeginTransaction(db)) {
        if (db) sqlite3_close(db);
        return false;
    }

    const int modelId = EnsureModel(
        db,
        "OPUS-MT",
        "local",
        "translation",
        "CTranslate2");
    if (modelId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return false;
    }

    std::string language = targetLanguage;
    if (language != "zh" && language != "en") {
        language = "unknown";
    }
    const std::string stage = "translation_" + language;
    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "INSERT INTO revision("
        "segment_id,parent_rev_id,stage,model_id,text_final"
        ") VALUES("
        "?,(SELECT id FROM revision "
        "   WHERE segment_id=? "
        "     AND stage IN ('asr_whisper_final','asr_normalized') "
        "   ORDER BY CASE stage "
        "     WHEN 'asr_whisper_final' THEN 0 ELSE 1 END LIMIT 1),?,?,?"
        ") ON CONFLICT(segment_id,stage) DO UPDATE SET "
        "model_id=excluded.model_id,"
        "text_final=excluded.text_final,"
        "created_at_utc=CURRENT_TIMESTAMP;";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return false;
    }
    sqlite3_bind_int64(statement, 1, segmentId);
    sqlite3_bind_int64(statement, 2, segmentId);
    sqlite3_bind_text(
        statement, 3, stage.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement, 4, modelId);
    sqlite3_bind_text(
        statement,
        5,
        translatedText.c_str(),
        -1,
        SQLITE_TRANSIENT);
    const bool ok = sqlite3_step(statement) == SQLITE_DONE;
    if (!ok) {
        std::cerr << "[DB] insert translation revision failed: "
                  << sqlite3_errmsg(db) << "\n";
    }
    sqlite3_finalize(statement);

    if (!ok || !CommitTransaction(db)) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return false;
    }
    sqlite3_close(db);
    return true;
}

std::int64_t BeginTranscriptionRun(
    std::int64_t meetingId,
    const std::string& engine,
    const std::string& modelName,
    const std::string& runtime,
    const std::string& translationMode,
    const std::string& hotwordsText) {
    if (meetingId <= 0 || engine.empty() || modelName.empty()) {
        return 0;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return 0;
    }
    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "INSERT INTO transcription_run("
        "meeting_id,engine,model_name,runtime,status,progress,"
        "translation_mode,hotwords_text,started_at_utc"
        ") VALUES(?,?,?,?,'transcribing',0,?,?,datetime('now'));";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        sqlite3_close(db);
        return 0;
    }
    sqlite3_bind_int64(statement, 1, meetingId);
    sqlite3_bind_text(
        statement, 2, engine.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(
        statement, 3, modelName.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(
        statement, 4, runtime.c_str(), -1, SQLITE_TRANSIENT);
    if (translationMode.empty()) {
        sqlite3_bind_null(statement, 5);
    }
    else {
        sqlite3_bind_text(
            statement, 5, translationMode.c_str(), -1, SQLITE_TRANSIENT);
    }
    if (hotwordsText.empty()) {
        sqlite3_bind_null(statement, 6);
    }
    else {
        sqlite3_bind_text(
            statement, 6, hotwordsText.c_str(), -1, SQLITE_TRANSIENT);
    }
    const bool ok = sqlite3_step(statement) == SQLITE_DONE;
    sqlite3_finalize(statement);
    const std::int64_t runId =
        ok ? sqlite3_last_insert_rowid(db) : 0;
    sqlite3_close(db);
    return runId;
}

bool UpdateTranscriptionRun(
    std::int64_t runId,
    const std::string& status,
    int progress,
    const std::string& errorText,
    bool makeCanonical) {
    if (runId <= 0 || status.empty()) {
        return false;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db) || !BeginTransaction(db)) {
        if (db) sqlite3_close(db);
        return false;
    }

    if (makeCanonical) {
        sqlite3_stmt* clear = nullptr;
        const char* clearSql =
            "UPDATE transcription_run SET is_canonical=0 "
            "WHERE meeting_id=("
            "SELECT meeting_id FROM transcription_run WHERE id=?);";
        if (sqlite3_prepare_v2(
            db, clearSql, -1, &clear, nullptr) != SQLITE_OK) {
            RollbackTransaction(db);
            sqlite3_close(db);
            return false;
        }
        sqlite3_bind_int64(clear, 1, runId);
        const bool cleared = sqlite3_step(clear) == SQLITE_DONE;
        sqlite3_finalize(clear);
        if (!cleared) {
            RollbackTransaction(db);
            sqlite3_close(db);
            return false;
        }
    }

    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "UPDATE transcription_run SET "
        "status=?,progress=?,error_text=?,"
        "is_canonical=CASE WHEN ?=1 THEN 1 ELSE is_canonical END,"
        "completed_at_utc=CASE "
        "  WHEN ? IN ('complete','failed') THEN datetime('now') "
        "  ELSE completed_at_utc END "
        "WHERE id=?;";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return false;
    }
    sqlite3_bind_text(
        statement, 1, status.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement, 2, std::clamp(progress, 0, 100));
    if (errorText.empty()) {
        sqlite3_bind_null(statement, 3);
    }
    else {
        sqlite3_bind_text(
            statement, 3, errorText.c_str(), -1, SQLITE_TRANSIENT);
    }
    sqlite3_bind_int(statement, 4, makeCanonical ? 1 : 0);
    sqlite3_bind_text(
        statement, 5, status.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(statement, 6, runId);
    const bool ok =
        sqlite3_step(statement) == SQLITE_DONE &&
        sqlite3_changes(db) > 0;
    sqlite3_finalize(statement);

    if (!ok || !CommitTransaction(db)) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return false;
    }
    sqlite3_close(db);
    return true;
}

bool LoadMeetingPostProcessInput(
    std::int64_t meetingId,
    MeetingPostProcessInput& input) {
    input = {};
    if (meetingId <= 0) {
        return false;
    }

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return false;
    }

    sqlite3_stmt* streamStatement = nullptr;
    const char* streamSql =
        "SELECT type,media_path FROM stream "
        "WHERE meeting_id=? AND media_path IS NOT NULL "
        "  AND length(trim(media_path))>0;";
    if (sqlite3_prepare_v2(
        db, streamSql, -1, &streamStatement, nullptr) == SQLITE_OK) {
        sqlite3_bind_int64(streamStatement, 1, meetingId);
        while (sqlite3_step(streamStatement) == SQLITE_ROW) {
            const char* source = reinterpret_cast<const char*>(
                sqlite3_column_text(streamStatement, 0));
            const char* path = reinterpret_cast<const char*>(
                sqlite3_column_text(streamStatement, 1));
            if (source && path) {
                input.audioPaths[source] = path;
            }
        }
    }
    sqlite3_finalize(streamStatement);

    sqlite3_stmt* runStatement = nullptr;
    const char* runSql =
        "SELECT translation_mode,hotwords_text "
        "FROM transcription_run WHERE meeting_id=? "
        "ORDER BY id DESC LIMIT 1;";
    if (sqlite3_prepare_v2(
        db, runSql, -1, &runStatement, nullptr) == SQLITE_OK) {
        sqlite3_bind_int64(runStatement, 1, meetingId);
        if (sqlite3_step(runStatement) == SQLITE_ROW) {
            const char* mode = reinterpret_cast<const char*>(
                sqlite3_column_text(runStatement, 0));
            const char* hotwords = reinterpret_cast<const char*>(
                sqlite3_column_text(runStatement, 1));
            input.translationMode = mode ? mode : "off";
            input.hotwordsText = hotwords ? hotwords : "";
        }
    }
    sqlite3_finalize(runStatement);
    sqlite3_close(db);
    return !input.audioPaths.empty();
}

std::int64_t InsertWhisperFinalSegment(
    std::int64_t runId,
    std::int64_t meetingId,
    const std::string& source,
    std::int64_t sequence,
    std::int64_t startMs,
    std::int64_t endMs,
    const std::string& rawText,
    const std::string& finalText) {
    if (runId <= 0 || meetingId <= 0 || finalText.empty()) {
        return 0;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db) || !BeginTransaction(db)) {
        if (db) sqlite3_close(db);
        return 0;
    }

    std::int64_t streamId = 0;
    sqlite3_stmt* streamStatement = nullptr;
    const char* streamSql =
        "SELECT id FROM stream WHERE meeting_id=? AND type=? LIMIT 1;";
    if (sqlite3_prepare_v2(
        db, streamSql, -1, &streamStatement, nullptr) == SQLITE_OK) {
        sqlite3_bind_int64(streamStatement, 1, meetingId);
        sqlite3_bind_text(
            streamStatement, 2, source.c_str(), -1, SQLITE_TRANSIENT);
        if (sqlite3_step(streamStatement) == SQLITE_ROW) {
            streamId = sqlite3_column_int64(streamStatement, 0);
        }
    }
    sqlite3_finalize(streamStatement);
    if (streamId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    sqlite3_stmt* segmentStatement = nullptr;
    const char* segmentSql =
        "INSERT INTO segment("
        "meeting_id,stream_id,transcription_run_id,seq,start_ms,end_ms,"
        "speaker_hint,origin,source_meta,text_raw,is_final"
        ") VALUES(?,?,?,?,?,?,?,?,?,?,1);";
    if (sqlite3_prepare_v2(
        db, segmentSql, -1, &segmentStatement, nullptr) != SQLITE_OK) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    const std::string speaker = source == "system" ? "对方" : "我方";
    const std::string sourceMeta =
        "{\"source\":\"" + source + "\",\"transcription_run_id\":"
        + std::to_string(runId) + "}";
    sqlite3_bind_int64(segmentStatement, 1, meetingId);
    sqlite3_bind_int64(segmentStatement, 2, streamId);
    sqlite3_bind_int64(segmentStatement, 3, runId);
    sqlite3_bind_int64(segmentStatement, 4, sequence);
    sqlite3_bind_int64(segmentStatement, 5, startMs);
    sqlite3_bind_int64(segmentStatement, 6, endMs);
    sqlite3_bind_text(
        segmentStatement, 7, speaker.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(
        segmentStatement,
        8,
        "asr_whisper_post",
        -1,
        SQLITE_STATIC);
    sqlite3_bind_text(
        segmentStatement,
        9,
        sourceMeta.c_str(),
        -1,
        SQLITE_TRANSIENT);
    sqlite3_bind_text(
        segmentStatement, 10, rawText.c_str(), -1, SQLITE_TRANSIENT);
    const bool insertedSegment =
        sqlite3_step(segmentStatement) == SQLITE_DONE;
    sqlite3_finalize(segmentStatement);
    if (!insertedSegment) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    const std::int64_t segmentId = sqlite3_last_insert_rowid(db);

    const int modelId = EnsureModel(
        db,
        "Whisper large-v3",
        "OpenVINO IR",
        "asr",
        "OpenVINO GenAI");
    if (modelId == 0) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    sqlite3_stmt* rawStatement = nullptr;
    const char* rawSql =
        "INSERT INTO revision("
        "segment_id,stage,model_id,text_final"
        ") VALUES(?,'asr_whisper_raw',?,?);";
    if (sqlite3_prepare_v2(
        db, rawSql, -1, &rawStatement, nullptr) != SQLITE_OK) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_bind_int64(rawStatement, 1, segmentId);
    sqlite3_bind_int(rawStatement, 2, modelId);
    sqlite3_bind_text(
        rawStatement, 3, rawText.c_str(), -1, SQLITE_TRANSIENT);
    const bool insertedRaw = sqlite3_step(rawStatement) == SQLITE_DONE;
    const std::int64_t rawRevisionId =
        insertedRaw ? sqlite3_last_insert_rowid(db) : 0;
    sqlite3_finalize(rawStatement);
    if (!insertedRaw) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }

    sqlite3_stmt* finalStatement = nullptr;
    const char* finalSql =
        "INSERT INTO revision("
        "segment_id,parent_rev_id,stage,model_id,text_final"
        ") VALUES(?,?,'asr_whisper_final',?,?);";
    if (sqlite3_prepare_v2(
        db, finalSql, -1, &finalStatement, nullptr) != SQLITE_OK) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_bind_int64(finalStatement, 1, segmentId);
    sqlite3_bind_int64(finalStatement, 2, rawRevisionId);
    sqlite3_bind_int(finalStatement, 3, modelId);
    sqlite3_bind_text(
        finalStatement, 4, finalText.c_str(), -1, SQLITE_TRANSIENT);
    const bool insertedFinal =
        sqlite3_step(finalStatement) == SQLITE_DONE;
    sqlite3_finalize(finalStatement);

    if (!insertedFinal || !CommitTransaction(db)) {
        RollbackTransaction(db);
        sqlite3_close(db);
        return 0;
    }
    sqlite3_close(db);
    return segmentId;
}

std::vector<MeetingTranscriptEntry> LoadMeetingTranscriptSince(
    std::int64_t meetingId,
    std::int64_t afterSegmentId) {
    std::vector<MeetingTranscriptEntry> entries;
    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return entries;
    }

    std::int64_t canonicalRunId = 0;
    sqlite3_stmt* canonicalStatement = nullptr;
    const char* canonicalSql =
        "SELECT id FROM transcription_run "
        "WHERE meeting_id=? AND is_canonical=1 "
        "ORDER BY id DESC LIMIT 1;";
    if (sqlite3_prepare_v2(
        db,
        canonicalSql,
        -1,
        &canonicalStatement,
        nullptr) == SQLITE_OK) {
        sqlite3_bind_int64(canonicalStatement, 1, meetingId);
        if (sqlite3_step(canonicalStatement) == SQLITE_ROW) {
            canonicalRunId = sqlite3_column_int64(canonicalStatement, 0);
        }
    }
    sqlite3_finalize(canonicalStatement);

    sqlite3_stmt* statement = nullptr;
    const char* finalSql =
        "SELECT s.id,st.type,r.text_final,s.start_ms,s.end_ms "
        "FROM segment s "
        "JOIN stream st ON st.id=s.stream_id "
        "JOIN revision r ON r.segment_id=s.id "
        "WHERE s.meeting_id=? AND s.id>? "
        "  AND s.transcription_run_id=? "
        "  AND s.is_final=1 AND r.stage='asr_whisper_final' "
        "ORDER BY s.start_ms,s.id;";
    const char* liveSql =
        "SELECT s.id,st.type,r.text_final,s.start_ms,s.end_ms "
        "FROM segment s "
        "JOIN stream st ON st.id=s.stream_id "
        "JOIN revision r ON r.segment_id=s.id "
        "WHERE s.meeting_id=? AND s.id>? "
        "  AND s.transcription_run_id IS NULL "
        "  AND s.is_final=1 AND r.stage='asr_normalized' "
        "ORDER BY s.start_ms,s.id;";
    const char* sql = canonicalRunId > 0 ? finalSql : liveSql;
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) == SQLITE_OK) {
        sqlite3_bind_int64(statement, 1, meetingId);
        sqlite3_bind_int64(statement, 2, afterSegmentId);
        if (canonicalRunId > 0) {
            sqlite3_bind_int64(statement, 3, canonicalRunId);
        }
        while (sqlite3_step(statement) == SQLITE_ROW) {
            MeetingTranscriptEntry entry;
            entry.segmentId = sqlite3_column_int64(statement, 0);
            const char* source = reinterpret_cast<const char*>(
                sqlite3_column_text(statement, 1));
            const char* text = reinterpret_cast<const char*>(
                sqlite3_column_text(statement, 2));
            entry.source = source ? source : "";
            entry.text = text ? text : "";
            entry.startMs = sqlite3_column_int64(statement, 3);
            entry.endMs = sqlite3_column_int64(statement, 4);
            entries.push_back(std::move(entry));
        }
    }
    else {
        std::cerr << "[DB] load transcript failed: "
                  << sqlite3_errmsg(db) << "\n";
    }
    sqlite3_finalize(statement);
    sqlite3_close(db);
    return entries;
}

bool InsertMeetingSummary(
    std::int64_t meetingId,
    std::int64_t coveredThroughSegmentId,
    const std::string& modelName,
    const std::string& promptVersion,
    const std::string& summaryText,
    bool isFinal) {
    if (meetingId <= 0 || summaryText.empty()) {
        return false;
    }
    std::lock_guard<std::mutex> lock(g_meetingWriteMutex);

    sqlite3* db = nullptr;
    if (!OpenMeetingDatabase(&db)) {
        return false;
    }
    sqlite3_stmt* statement = nullptr;
    const char* sql =
        "INSERT INTO meeting_summary("
        "meeting_id,revision_no,covered_through_segment_id,model_name,"
        "prompt_version,summary_text,is_final,transcription_run_id,summary_kind"
        ") VALUES("
        "?,(SELECT COALESCE(MAX(revision_no),0)+1 "
        "   FROM meeting_summary WHERE meeting_id=?),?,?,?,?,?,"
        "(SELECT id FROM transcription_run "
        " WHERE meeting_id=? AND is_canonical=1 "
        " ORDER BY id DESC LIMIT 1),?"
        ");";
    if (sqlite3_prepare_v2(db, sql, -1, &statement, nullptr) != SQLITE_OK) {
        sqlite3_close(db);
        return false;
    }
    sqlite3_bind_int64(statement, 1, meetingId);
    sqlite3_bind_int64(statement, 2, meetingId);
    if (coveredThroughSegmentId > 0) {
        sqlite3_bind_int64(statement, 3, coveredThroughSegmentId);
    }
    else {
        // 允许“只有实时 partial、尚无 final”的早期摘要落库。
        sqlite3_bind_null(statement, 3);
    }
    sqlite3_bind_text(
        statement, 4, modelName.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(
        statement, 5, promptVersion.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(
        statement, 6, summaryText.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int(statement, 7, isFinal ? 1 : 0);
    sqlite3_bind_int64(statement, 8, meetingId);
    std::string summaryKind = "rolling_quick";
    if (isFinal) {
        summaryKind = "final_minutes";
    }
    else if (promptVersion.find("detailed") != std::string::npos) {
        summaryKind = "detailed_draft";
    }
    sqlite3_bind_text(
        statement, 9, summaryKind.c_str(), -1, SQLITE_TRANSIENT);
    const bool ok = sqlite3_step(statement) == SQLITE_DONE;
    if (!ok) {
        std::cerr << "[DB] insert meeting summary failed: "
                  << sqlite3_errmsg(db) << "\n";
    }
    sqlite3_finalize(statement);
    sqlite3_close(db);
    return ok;
}

// ===== RAG 文档入库 =====
int InsertDocument(const std::string& title,
                   const std::string& source_type,
                   const std::string& file_path,
                   const std::vector<std::string>& chunk_texts,
                   const std::vector<std::vector<float>>& embeddings) {
    if (chunk_texts.size() != embeddings.size()) {
        std::cerr << "[DB] chunk_texts.size() != embeddings.size()\n";
        return -1;
    }

    sqlite3* db = nullptr;
    const std::string db8 = meetingai::util::getDatabasePath();
    if (sqlite3_open(db8.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: " << sqlite3_errmsg(db) << "\n";
        return -1;
    }

    sqlite3_busy_timeout(db, 5000);
    sqlite3_exec(db, "BEGIN IMMEDIATE;", nullptr, nullptr, nullptr);

    // 1. 插入文档
    const char* sql1 = "INSERT INTO documents(title, source_type, file_path, content_preview) VALUES(?,?,?,?);";
    sqlite3_stmt* st1 = nullptr;
    sqlite3_prepare_v2(db, sql1, -1, &st1, nullptr);
    sqlite3_bind_text(st1, 1, title.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(st1, 2, source_type.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(st1, 3, file_path.c_str(), -1, SQLITE_TRANSIENT);

    std::string preview = chunk_texts[0].substr(0, std::min((size_t)200, chunk_texts[0].size()));
    sqlite3_bind_text(st1, 4, preview.c_str(), -1, SQLITE_TRANSIENT);

    if (sqlite3_step(st1) != SQLITE_DONE) {
        std::cerr << "[DB] insert document failed: " << sqlite3_errmsg(db) << "\n";
        sqlite3_finalize(st1);
        sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
        sqlite3_close(db);
        return -1;
    }
    sqlite3_finalize(st1);
    int doc_id = (int)sqlite3_last_insert_rowid(db);

    // 2. 插入分块
    const char* sql2 = "INSERT INTO document_chunks(doc_id, chunk_index, text, embedding, token_count) VALUES(?,?,?,?,?);";
    for (size_t i = 0; i < chunk_texts.size(); i++) {
        sqlite3_stmt* st2 = nullptr;
        sqlite3_prepare_v2(db, sql2, -1, &st2, nullptr);
        sqlite3_bind_int(st2, 1, doc_id);
        sqlite3_bind_int(st2, 2, (int)i);
        sqlite3_bind_text(st2, 3, chunk_texts[i].c_str(), -1, SQLITE_TRANSIENT);

        // 转 embedding 为 BLOB
        const std::vector<float>& emb = embeddings[i];
        sqlite3_bind_blob(st2, 4, emb.data(), (int)(emb.size() * sizeof(float)), SQLITE_TRANSIENT);
        sqlite3_bind_int(st2, 5, (int)chunk_texts[i].size());  // 粗略估计

        if (sqlite3_step(st2) != SQLITE_DONE) {
            std::cerr << "[DB] insert chunk failed: " << sqlite3_errmsg(db) << "\n";
            sqlite3_finalize(st2);
            sqlite3_exec(db, "ROLLBACK;", nullptr, nullptr, nullptr);
            sqlite3_close(db);
            return -1;
        }
        sqlite3_finalize(st2);
    }

    sqlite3_exec(db, "COMMIT;", nullptr, nullptr, nullptr);
    sqlite3_close(db);

    std::cerr << "[DB] ✅ Inserted document id=" << doc_id << " with " << chunk_texts.size() << " chunks\n";
    return doc_id;
}

// ===== RAG 检索 Top-K =====
std::vector<RetrievalResult> RetrieveTopK(const std::vector<float>& query_embedding, int top_k) {
    std::vector<RetrievalResult> results;

    sqlite3* db = nullptr;
    const std::string db8 = meetingai::util::getDatabasePath();
    if (sqlite3_open(db8.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed\n";
        return results;
    }

    sqlite3_busy_timeout(db, 5000);

    // 注册余弦相似度函数（每次连接都要注册）
    sqlite3_create_function(db, "cosine_similarity", 2, SQLITE_UTF8, nullptr,
                            cosine_similarity_func, nullptr, nullptr);

    const char* sql =
        "SELECT id, text, cosine_similarity(embedding, ?) as score "
        "FROM document_chunks "
        "ORDER BY score DESC LIMIT ?;";

    sqlite3_stmt* st = nullptr;
    sqlite3_prepare_v2(db, sql, -1, &st, nullptr);

    // 绑定 query_embedding BLOB
    sqlite3_bind_blob(st, 1, query_embedding.data(),
                      (int)(query_embedding.size() * sizeof(float)), SQLITE_TRANSIENT);
    sqlite3_bind_int(st, 2, top_k);

    while (sqlite3_step(st) == SQLITE_ROW) {
        RetrievalResult r;
        r.chunk_id = sqlite3_column_int(st, 0);
        const char* txt = (const char*)sqlite3_column_text(st, 1);
        r.text = txt ? txt : "";
        r.similarity = (float)sqlite3_column_double(st, 2);
        results.push_back(r);
    }

    sqlite3_finalize(st);
    sqlite3_close(db);

    return results;
}
