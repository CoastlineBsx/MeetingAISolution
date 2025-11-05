#pragma once
#include <string>
#include <vector>

// 初始化数据库（创建 meeting.db 和 transcripts 表）
bool InitDatabaseOnce();

// 插入一条转录记录：speaker 说了 text，发生在 timestamp 秒
bool InsertTranscript(const std::string& speaker,
    const std::string& text,
    double timestamp);

// ===== RAG 相关函数 =====
struct RetrievalResult {
    int chunk_id;
    std::string text;
    float similarity;
};

// 插入文档并生成分块向量
int InsertDocument(const std::string& title,
                   const std::string& source_type,
                   const std::string& file_path,
                   const std::vector<std::string>& chunk_texts,
                   const std::vector<std::vector<float>>& embeddings);

// 检索最相关的 Top-K 文档片段
std::vector<RetrievalResult> RetrieveTopK(const std::vector<float>& query_embedding, int top_k = 3);
