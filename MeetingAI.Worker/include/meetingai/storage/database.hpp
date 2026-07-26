#pragma once
#include <cstdint>
#include <string>
#include <vector>

// 初始化数据库（创建 meeting.db 和 transcripts 表）
bool InitDatabaseOnce();

// 插入一条转录记录：speaker 说了 text，发生在 timestamp 秒
bool InsertTranscript(const std::string& speaker,
    const std::string& text,
    double timestamp);

// ===== Streaming Meeting 持久化 =====
struct MeetingTranscriptEntry {
    std::int64_t segmentId = 0;
    std::string source;
    std::string text;
};

// 每次点击 Start 都创建一场全新的会议，并为所选音频来源创建独立 stream。
// 返回 meeting.id；失败返回 0。
std::int64_t BeginStreamingMeeting(
    const std::vector<std::string>& sources,
    int sampleRateHz,
    const std::string& contextTitle = {},
    std::int64_t preparationId = 0,
    const std::string& contextSnapshotJson = {},
    int hotwordCount = 0,
    bool ragEnabled = false);

// 封口当前会议，记录 ended_at_utc。
bool EndStreamingMeeting(std::int64_t meetingId);

// 保存一条 Sherpa final。partial 结果不应调用本函数。
// 同一事务中写入 segment、asr_raw 和 asr_normalized revision。
// 返回 segment.id；失败返回 0。
std::int64_t InsertStreamingFinal(
    std::int64_t meetingId,
    const std::string& source,
    std::int64_t utteranceId,
    std::int64_t startMs,
    std::int64_t endMs,
    const std::string& rawText,
    const std::string& normalizedText);

// 将 final 译文作为同一 segment 的后续 revision 保存。
bool InsertStreamingTranslation(
    std::int64_t segmentId,
    const std::string& targetLanguage,
    const std::string& translatedText);

// 给滚动摘要读取“上次覆盖位置之后”的最终规范化字幕。
std::vector<MeetingTranscriptEntry> LoadMeetingTranscriptSince(
    std::int64_t meetingId,
    std::int64_t afterSegmentId);

// 保存一版不可变的滚动/最终摘要 revision。
bool InsertMeetingSummary(
    std::int64_t meetingId,
    std::int64_t coveredThroughSegmentId,
    const std::string& modelName,
    const std::string& promptVersion,
    const std::string& summaryText,
    bool isFinal);

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
