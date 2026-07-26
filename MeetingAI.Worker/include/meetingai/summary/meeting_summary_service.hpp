#pragma once

#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>

namespace meetingai::summary {

class MeetingSummaryService {
public:
    using PartialCallback = std::function<void(const std::string&)>;
    using PrepareCallback = std::function<bool(std::string&)>;
    using GenerateCallback = std::function<std::string(
        const std::string& prompt,
        const std::string& jsonSchema,
        const PartialCallback& onPartial)>;
    using EventCallback = std::function<void(
        const std::string& state,
        const std::string& text,
        bool isFinal,
        std::int64_t coveredThroughSegmentId,
        const std::string& summaryKind)>;

    MeetingSummaryService() = default;
    ~MeetingSummaryService();

    MeetingSummaryService(const MeetingSummaryService&) = delete;
    MeetingSummaryService& operator=(const MeetingSummaryService&) = delete;

    void Start(
        std::int64_t meetingId,
        PrepareCallback prepare,
        GenerateCallback generate,
        EventCallback onEvent);

    // partial 不写数据库，但摘要需要看到用户此刻在字幕区看到的内容。
    // 每个来源只保留当前 utterance 的最新快照，不累计历史 partial。
    void UpdateLiveTranscript(
        const std::string& source,
        std::int64_t utteranceId,
        const std::string& text);

    // final 已经成功写入数据库时，原子地移除同一条 live 快照并登记新增内容。
    // 这样生成摘要时不会同时读到同一句的 final 和 partial。
    void NotifyFinalTranscript(
        const std::string& source,
        std::int64_t utteranceId,
        std::size_t newTextBytes);

    void RequestNow();
    void Stop(bool generateFinal);
    bool IsRunning() const;
    bool IsGenerating() const;

private:
    struct LiveTranscript {
        std::int64_t utteranceId = 0;
        std::string text;
    };

    void ThreadMain();
    bool GenerateSummary(bool isFinal, bool isDetailed);
    void Emit(
        const std::string& state,
        const std::string& text = "",
        bool isFinal = false,
        std::int64_t coveredThroughSegmentId = 0,
        const std::string& summaryKind = "") const;

    mutable std::mutex mutex_;
    std::condition_variable condition_;
    std::thread worker_;
    std::int64_t meetingId_ = 0;
    std::int64_t coveredThroughSegmentId_ = 0;
    std::size_t pendingTextBytes_ = 0;
    std::size_t liveTextBytes_ = 0;
    std::uint64_t contentVersion_ = 0;
    std::uint64_t summarizedContentVersion_ = 0;
    std::unordered_map<std::string, LiveTranscript> liveTranscripts_;
    std::string latestSummary_;
    PrepareCallback prepare_;
    GenerateCallback generate_;
    EventCallback onEvent_;
    std::chrono::steady_clock::time_point meetingStartedAt_{};
    std::chrono::steady_clock::time_point lastSummaryAt_{};
    bool running_ = false;
    bool modelReady_ = false;
    bool forceRequested_ = false;
    bool stopRequested_ = false;
    bool finalRequested_ = false;
    bool generating_ = false;
};

} // namespace meetingai::summary
