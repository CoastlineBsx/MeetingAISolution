#include "pch.h"
#include "db.h"
#include "paths.h"
#include "sqlite3.h"
#include <iostream>

static bool ExecSQL(sqlite3* db, const char* sql) {
    char* err = nullptr;
    int rc = sqlite3_exec(db, sql, nullptr, nullptr, &err);
    if (rc != SQLITE_OK) {
        std::cerr << "[DB] SQL error: " << (err ? err : "") << "\n";
        sqlite3_free(err);
        return false;
    }
    return true;
}

bool InitDatabaseOnce() {
    // 用 UTF-16 打开，支持路径里有非 ASCII 字符
    std::wstring dbw;
    {
        std::string db8 = GetDatabasePath();
        dbw.assign(db8.begin(), db8.end());
    }
    sqlite3* db = nullptr;
    if (sqlite3_open16(dbw.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: " << sqlite3_errmsg(db) << "\n";
        return false;
    }

    // 基本设置：WAL 日志 & 合理同步级别
    if (!ExecSQL(db, "PRAGMA journal_mode=WAL;")) { sqlite3_close(db); return false; }
    if (!ExecSQL(db, "PRAGMA synchronous=NORMAL;")) { sqlite3_close(db); return false; }

    // 创建 transcripts 表，存转录
    const char* create_sql =
        "CREATE TABLE IF NOT EXISTS transcripts ("
        "  id INTEGER PRIMARY KEY AUTOINCREMENT,"
        "  speaker TEXT,"
        "  text TEXT,"
        "  ts REAL,"
        "  created_at DATETIME DEFAULT CURRENT_TIMESTAMP"
        ");";
    if (!ExecSQL(db, create_sql)) { sqlite3_close(db); return false; }

    // 可选：全文检索表（以后搜索用）
    const char* fts_sql =
        "CREATE VIRTUAL TABLE IF NOT EXISTS transcripts_fts "
        "USING fts5(text, content='transcripts', content_rowid='id');";
    if (!ExecSQL(db, fts_sql)) { sqlite3_close(db); return false; }

    const char* trg_ai =
        "CREATE TRIGGER IF NOT EXISTS transcripts_ai AFTER INSERT ON transcripts "
        "BEGIN INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text); END;";
    const char* trg_ad =
        "CREATE TRIGGER IF NOT EXISTS transcripts_ad AFTER DELETE ON transcripts "
        "BEGIN INSERT INTO transcripts_fts(transcripts_fts, rowid, text) "
        "VALUES ('delete', old.id, old.text); END;";
    const char* trg_au =
        "CREATE TRIGGER IF NOT EXISTS transcripts_au AFTER UPDATE ON transcripts "
        "BEGIN "
        "  INSERT INTO transcripts_fts(transcripts_fts, rowid, text) VALUES ('delete', old.id, old.text); "
        "  INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text); "
        "END;";
    if (!ExecSQL(db, trg_ai) || !ExecSQL(db, trg_ad) || !ExecSQL(db, trg_au)) {
        sqlite3_close(db); return false;
    }

    sqlite3_close(db);
    return true;
}

bool InsertTranscript(const std::string& speaker,
    const std::string& text,
    double timestamp) {
    sqlite3* db = nullptr;
    // 同样用 UTF-16 路径打开
    std::wstring dbw;
    { std::string db8 = GetDatabasePath(); dbw.assign(db8.begin(), db8.end()); }
    if (sqlite3_open16(dbw.c_str(), &db) != SQLITE_OK) {
        std::cerr << "[DB] open failed: " << sqlite3_errmsg(db) << "\n";
        return false;
    }

    const char* sql = "INSERT INTO transcripts (speaker, text, ts) VALUES (?, ?, ?);";
    sqlite3_stmt* st = nullptr;
    if (sqlite3_prepare_v2(db, sql, -1, &st, nullptr) != SQLITE_OK) {
        std::cerr << "[DB] prepare failed: " << sqlite3_errmsg(db) << "\n";
        sqlite3_close(db);
        return false;
    }

    sqlite3_bind_text(st, 1, speaker.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_text(st, 2, text.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_double(st, 3, timestamp);

    int rc = sqlite3_step(st);
    if (rc != SQLITE_DONE) {
        std::cerr << "[DB] insert failed: " << sqlite3_errmsg(db) << "\n";
        sqlite3_finalize(st);
        sqlite3_close(db);
        return false;
    }
    sqlite3_finalize(st);
    sqlite3_close(db);
    return true;
}
