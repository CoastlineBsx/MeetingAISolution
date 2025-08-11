#pragma once
#include <string>

// 初始化数据库（创建 meeting.db 和 transcripts 表）
bool InitDatabaseOnce();

// 插入一条转录记录：speaker 说了 text，发生在 timestamp 秒
bool InsertTranscript(const std::string& speaker,
    const std::string& text,
    double timestamp);
