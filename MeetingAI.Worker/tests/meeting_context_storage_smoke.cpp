#include "command_parser.h"
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"

#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <string>

namespace {

void Require(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAILED: " << message << "\n";
        std::exit(1);
    }
}

} // namespace

int main(int argc, char** argv) {
    Require(argc == 2, "temporary data directory argument");
    Require(
        _putenv_s("MEETINGAI_DATA_DIR", argv[1]) == 0,
        "set isolated database directory");
    std::filesystem::create_directories(argv[1]);

    const std::string command = R"JSON({
        "type":"start_streaming",
        "preparation_id":42,
        "context_title":"Project Phoenix",
        "context_document_ids":[1,2,3,4,5,6],
        "hotwords":[
            {"text":"OpenVINO","score":3.5},
            {"text":"Sherpa-ONNX","score":2.75},
            {"text":"Application: useful","score":1.0},
            {"text":"COMP0197： Applied Deep Learning","score":1.25}
        ]
    })JSON";
    const auto context =
        meetingai::proto::extractMeetingContext(command);
    Require(context.preparationId == 42, "parse preparation id");
    Require(context.documentIds.size() == 5, "cap document snapshot at five");
    Require(context.hotwords.size() == 4, "parse meeting hotwords");

    const auto hotwords =
        meetingai::proto::buildSherpaHotwordsBuffer(context);
    Require(
        hotwords.find("OpenVINO :3.50") != std::string::npos,
        "build sherpa hotword buffer");
    Require(
        hotwords.find("Application useful :1.00") != std::string::npos,
        "sanitize ASCII colon in sherpa hotword text");
    Require(
        hotwords.find("COMP0197 Applied Deep Learning :1.25") !=
            std::string::npos,
        "sanitize full-width colon in sherpa hotword text");
    Require(
        hotwords.find("Application: useful") == std::string::npos,
        "reserve colon for sherpa score separator");
    const auto snapshot =
        meetingai::proto::buildMeetingContextSnapshotJson(context);
    Require(
        snapshot.find("\"preparation_id\":42") != std::string::npos,
        "build immutable context snapshot");

    // 模拟用户已有的 v2 meeting 表，验证启动升级不会丢旧表。
    sqlite3* database = nullptr;
    const auto databasePath = meetingai::util::getDatabasePath();
    Require(
        sqlite3_open(databasePath.c_str(), &database) == SQLITE_OK,
        "create legacy database");
    Require(
        sqlite3_exec(
            database,
            "CREATE TABLE meeting("
            "id INTEGER PRIMARY KEY,"
            "ext_source TEXT,title TEXT,tz TEXT,"
            "started_at_utc DATETIME,ended_at_utc DATETIME);"
            "PRAGMA user_version=2;",
            nullptr,
            nullptr,
            nullptr) == SQLITE_OK,
        "create legacy meeting schema");
    sqlite3_close(database);

    Require(InitDatabaseOnce(), "migrate meeting database");
    const auto meetingId = BeginStreamingMeeting(
        { "microphone", "system" },
        16000,
        context.title,
        context.preparationId,
        snapshot,
        static_cast<int>(context.hotwords.size()),
        true);
    Require(meetingId > 0, "insert context-bound meeting");

    Require(
        sqlite3_open(databasePath.c_str(), &database) == SQLITE_OK,
        "reopen migrated database");
    sqlite3_stmt* statement = nullptr;
    Require(
        sqlite3_prepare_v2(
            database,
            "SELECT preparation_id,context_title,context_snapshot_json,"
            "hotword_count,rag_enabled FROM meeting WHERE id=?;",
            -1,
            &statement,
            nullptr) == SQLITE_OK,
        "prepare meeting snapshot query");
    sqlite3_bind_int64(statement, 1, meetingId);
    Require(sqlite3_step(statement) == SQLITE_ROW, "read meeting snapshot");
    Require(sqlite3_column_int64(statement, 0) == 42, "persist preparation id");
    Require(
        std::string(reinterpret_cast<const char*>(
            sqlite3_column_text(statement, 1))) == "Project Phoenix",
        "persist context title");
    Require(
        sqlite3_column_bytes(statement, 2) > 0,
        "persist immutable context JSON");
    Require(sqlite3_column_int(statement, 3) == 4, "persist hotword count");
    Require(sqlite3_column_int(statement, 4) == 1, "persist scoped RAG flag");
    sqlite3_finalize(statement);
    sqlite3_close(database);

    std::cout
        << "PASS: meeting context parser, v2-to-v3 migration, and snapshot storage\n";
    return 0;
}
