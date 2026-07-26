#include "summary/meeting_summary_service.hpp"
#include "summary/meeting_summary_policy.hpp"

#include "database.hpp"

#include <algorithm>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

namespace meetingai::summary {
namespace {

constexpr auto kFirstSummaryDelay = std::chrono::minutes(1);
constexpr auto kRollingSummaryDelay = std::chrono::minutes(1);
constexpr std::size_t kRollingSummaryMinimumBytes = 200;
constexpr const char* kModelName = "IBM Granite 3.3 2B Instruct";
constexpr const char* kPromptVersionQuick =
    "adaptive-grounded-quick-v4";
constexpr const char* kPromptVersionDetailed =
    "adaptive-grounded-detailed-v4";

std::string DisplaySource(const std::string& source) {
    return source == "system" ? "对方" : "我方";
}

std::int64_t LiveEvidenceId(const std::string& source) {
    if (source == "microphone") {
        return 1000000001;
    }
    if (source == "system") {
        return 1000000002;
    }
    return 1000000003;
}

std::string LiveCitationLabel(const std::string& source) {
    return "实时·" + DisplaySource(source);
}

} // namespace

MeetingSummaryService::~MeetingSummaryService() {
    Stop(false);
}

void MeetingSummaryService::Start(
    std::int64_t meetingId,
    PrepareCallback prepare,
    GenerateCallback generate,
    EventCallback onEvent) {
    Stop(false);

    {
        std::lock_guard<std::mutex> lock(mutex_);
        meetingId_ = meetingId;
        coveredThroughSegmentId_ = 0;
        pendingTextBytes_ = 0;
        liveTextBytes_ = 0;
        contentVersion_ = 0;
        summarizedContentVersion_ = 0;
        liveTranscripts_.clear();
        latestSummary_.clear();
        prepare_ = std::move(prepare);
        generate_ = std::move(generate);
        onEvent_ = std::move(onEvent);
        meetingStartedAt_ = std::chrono::steady_clock::now();
        lastSummaryAt_ = {};
        running_ = true;
        modelReady_ = false;
        forceRequested_ = false;
        stopRequested_ = false;
        finalRequested_ = false;
        generating_ = false;
    }

    worker_ = std::thread(&MeetingSummaryService::ThreadMain, this);
}

void MeetingSummaryService::UpdateLiveTranscript(
    const std::string& source,
    std::int64_t utteranceId,
    const std::string& text) {
    if (source.empty() || utteranceId <= 0 || text.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    if (!running_) {
        return;
    }

    LiveTranscript& current = liveTranscripts_[source];
    if (current.utteranceId == utteranceId && current.text == text) {
        return;
    }
    liveTextBytes_ -= std::min(liveTextBytes_, current.text.size());
    current.utteranceId = utteranceId;
    current.text = text;
    liveTextBytes_ += current.text.size();
    ++contentVersion_;
}

void MeetingSummaryService::NotifyFinalTranscript(
    const std::string& source,
    std::int64_t utteranceId,
    std::size_t newTextBytes) {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!running_) {
            return;
        }

        const auto live = liveTranscripts_.find(source);
        if (live != liveTranscripts_.end() &&
            live->second.utteranceId == utteranceId) {
            liveTextBytes_ -=
                std::min(liveTextBytes_, live->second.text.size());
            liveTranscripts_.erase(live);
        }
        pendingTextBytes_ += newTextBytes;
        ++contentVersion_;
    }
    condition_.notify_all();
}

void MeetingSummaryService::RequestNow() {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!running_) {
            return;
        }
        forceRequested_ = true;
    }
    condition_.notify_all();
}

void MeetingSummaryService::Stop(bool generateFinal) {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!running_ && !worker_.joinable()) {
            return;
        }
        stopRequested_ = true;
        finalRequested_ = generateFinal;
    }
    condition_.notify_all();
    if (worker_.joinable() &&
        worker_.get_id() != std::this_thread::get_id()) {
        worker_.join();
    }

    std::lock_guard<std::mutex> lock(mutex_);
    running_ = false;
    prepare_ = {};
    generate_ = {};
    onEvent_ = {};
}

bool MeetingSummaryService::IsRunning() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return running_;
}

bool MeetingSummaryService::IsGenerating() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return generating_;
}

void MeetingSummaryService::ThreadMain() {
    std::cout << "[Summary] background thread started\n";
    std::cout << "[Summary] preparing Granite\n";

    PrepareCallback prepare;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        prepare = prepare_;
    }

    std::string prepareError;
    bool ready = false;
    try {
        ready = prepare && prepare(prepareError);
    }
    catch (const std::exception& e) {
        prepareError = e.what();
    }

    {
        std::lock_guard<std::mutex> lock(mutex_);
        modelReady_ = ready;
    }
    std::cout << "[Summary] Granite prepare result="
              << (ready ? "ready" : "failed") << "\n";
    if (!ready) {
        Emit(
            "error",
            prepareError.empty()
                ? "Granite 会议摘要模型加载失败"
                : prepareError);
    }
    else {
        Emit("ready", "Granite 已就绪，积累会议内容后自动生成摘要");
    }

    while (true) {
        bool shouldGenerate = false;
        bool shouldFinalize = false;
        bool shouldGenerateDetailed = false;
        bool generatedFinal = false;

        {
            std::unique_lock<std::mutex> lock(mutex_);
            if (!stopRequested_) {
                condition_.wait_for(lock, std::chrono::seconds(1));
            }

            const auto now = std::chrono::steady_clock::now();
            const bool hasNewContent =
                contentVersion_ != summarizedContentVersion_;
            const std::size_t availableTextBytes =
                pendingTextBytes_ + liveTextBytes_;
            const bool firstDue =
                lastSummaryAt_.time_since_epoch().count() == 0 &&
                now - meetingStartedAt_ >= kFirstSummaryDelay &&
                hasNewContent &&
                availableTextBytes >= kMinimumGroundedTranscriptBytes;
            const bool rollingDue =
                lastSummaryAt_.time_since_epoch().count() != 0 &&
                now - lastSummaryAt_ >= kRollingSummaryDelay &&
                hasNewContent &&
                availableTextBytes >= kRollingSummaryMinimumBytes;

            shouldFinalize = stopRequested_ && finalRequested_;
            shouldGenerateDetailed = forceRequested_ || shouldFinalize;
            shouldGenerate =
                modelReady_ &&
                (forceRequested_ || firstDue || rollingDue || shouldFinalize);
            forceRequested_ = false;

            if (stopRequested_ && !shouldGenerate) {
                break;
            }
        }

        if (shouldGenerate) {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                generating_ = true;
            }
            GenerateSummary(shouldFinalize, shouldGenerateDetailed);
            generatedFinal = shouldFinalize;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                generating_ = false;
            }
        }

        std::lock_guard<std::mutex> lock(mutex_);
        if (stopRequested_ &&
            (!finalRequested_ || generatedFinal)) {
            break;
        }
    }

    std::lock_guard<std::mutex> lock(mutex_);
    running_ = false;
}

bool MeetingSummaryService::GenerateSummary(
    bool isFinal,
    bool isDetailed) {
    const std::string summaryKind =
        isDetailed ? "detailed" : "quick";
    std::int64_t meetingId = 0;
    std::size_t pendingBytesAtStart = 0;
    std::uint64_t contentVersionAtStart = 0;
    std::unordered_map<std::string, LiveTranscript> liveTranscripts;
    GenerateCallback generate;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        meetingId = meetingId_;
        pendingBytesAtStart = pendingTextBytes_;
        contentVersionAtStart = contentVersion_;
        liveTranscripts = liveTranscripts_;
        generate = generate_;
    }

    // 每一版都从本场会议的全部最终原文重建，再附加生成瞬间捕获的
    // 每一路最新 partial。旧摘要不是事实来源；partial 也不写 segment，
    // 因而既能覆盖屏幕上的最新内容，又不会制造重复的正式字幕。
    const std::vector<MeetingTranscriptEntry> entries =
        LoadMeetingTranscriptSince(meetingId, 0);
    if (entries.empty() && liveTranscripts.empty()) {
        Emit(
            "waiting",
            isFinal
                ? "没有可总结的会议字幕"
                : "还没有捕获到可总结的字幕",
            isFinal,
            0,
            summaryKind);
        return false;
    }

    std::ostringstream transcript;
    std::vector<std::int64_t> allowedSegmentIds;
    std::vector<SummaryEvidence> summaryEvidence;
    allowedSegmentIds.reserve(entries.size());
    summaryEvidence.reserve(entries.size());
    std::int64_t newestSegmentId = 0;
    std::size_t meaningfulTextBytes = 0;
    for (const MeetingTranscriptEntry& entry : entries) {
        newestSegmentId = std::max(newestSegmentId, entry.segmentId);
        allowedSegmentIds.push_back(entry.segmentId);
        summaryEvidence.push_back({entry.segmentId, entry.text});
        meaningfulTextBytes += CountMeaningfulTranscriptBytes(entry.text);
        transcript << "[S" << entry.segmentId << "]["
                   << DisplaySource(entry.source) << "] "
                   << entry.text << "\n";
    }

    for (const auto& [source, live] : liveTranscripts) {
        if (live.text.empty()) {
            continue;
        }
        const std::int64_t evidenceId = LiveEvidenceId(source);
        allowedSegmentIds.push_back(evidenceId);
        summaryEvidence.push_back({
            evidenceId,
            live.text,
            LiveCitationLabel(source)});
        meaningfulTextBytes += CountMeaningfulTranscriptBytes(live.text);
        transcript << "[S" << evidenceId << "]["
                   << DisplaySource(source)
                   << "][当前实时字幕快照] "
                   << live.text << "\n";
    }

    const std::string transcriptText = transcript.str();
    if (meaningfulTextBytes < kMinimumGroundedTranscriptBytes) {
        Emit(
            "waiting",
            isFinal
                ? "会议有效字幕不足，未生成最终摘要"
                : "当前会议内容还太少，继续说一会儿再总结",
            isFinal,
            newestSegmentId,
            summaryKind);
        return false;
    }

    Emit(
        "generating",
        isFinal
            ? "正在生成最终会议摘要…"
            : (isDetailed
                ? "正在生成自适应详细摘要…"
                : "正在更新实时速览…"),
        isFinal,
        newestSegmentId,
        summaryKind);

    std::string summary;
    SummaryValidationResult validation;
    try {
        if (!generate) {
            throw std::runtime_error("Granite 生成回调不可用");
        }

        // 未通过事实校验前不把模型的 partial 文本展示给 UI，避免用户看到
        // 随后会被丢弃的幻觉内容。
        const std::string jsonSchema =
            BuildGroundedSummaryJsonSchema(
                allowedSegmentIds,
                isDetailed);
        const auto generateOnce =
            [&generate,
             &transcriptText,
             &jsonSchema,
             isFinal,
             isDetailed](
                bool isRetry) {
                return generate(
                    BuildGroundedSummaryPrompt(
                        transcriptText,
                        isFinal,
                        isDetailed,
                        isRetry),
                    jsonSchema,
                    [](const std::string&) {});
            };

        std::string jsonOutput = generateOnce(false);
        validation = FormatGroundedSummaryJson(
            jsonOutput,
            summaryEvidence,
            isDetailed,
            summary);
        if (!validation.accepted) {
            std::cout << "[Summary] validation retry: "
                      << validation.reason << "\n";
            Emit(
                "retrying",
                "摘要未通过事实证据校验，正在自动重新生成…",
                isFinal,
                newestSegmentId,
                summaryKind);
            jsonOutput = generateOnce(true);
            validation = FormatGroundedSummaryJson(
                jsonOutput,
                summaryEvidence,
                isDetailed,
                summary);
        }
    }
    catch (const std::exception& e) {
        Emit(
            "error",
            std::string("会议摘要生成失败: ") + e.what(),
            isFinal,
            newestSegmentId,
            summaryKind);
        return false;
    }

    if (!validation.accepted) {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            pendingTextBytes_ =
                pendingTextBytes_ > pendingBytesAtStart
                    ? pendingTextBytes_ - pendingBytesAtStart
                    : 0;
            summarizedContentVersion_ = contentVersionAtStart;
            lastSummaryAt_ = std::chrono::steady_clock::now();
        }
        std::cout << "[Summary] rejected: "
                  << validation.reason << "\n";
        Emit(
            "error",
            "摘要未通过事实证据校验，已丢弃且不会写入数据库",
            isFinal,
            newestSegmentId,
            summaryKind);
        return false;
    }
    if (!InsertMeetingSummary(
        meetingId,
        newestSegmentId,
        kModelName,
        isDetailed ? kPromptVersionDetailed : kPromptVersionQuick,
        summary,
        isFinal)) {
        Emit(
            "error",
            "摘要已生成，但写入数据库失败",
            isFinal,
            newestSegmentId,
            summaryKind);
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(mutex_);
        latestSummary_ = summary;
        coveredThroughSegmentId_ = newestSegmentId;
        pendingTextBytes_ =
            pendingTextBytes_ > pendingBytesAtStart
                ? pendingTextBytes_ - pendingBytesAtStart
                : 0;
        summarizedContentVersion_ = contentVersionAtStart;
        lastSummaryAt_ = std::chrono::steady_clock::now();
    }
    Emit(
        "final",
        summary,
        isFinal,
        newestSegmentId,
        summaryKind);
    return true;
}

void MeetingSummaryService::Emit(
    const std::string& state,
    const std::string& text,
    bool isFinal,
    std::int64_t coveredThroughSegmentId,
    const std::string& summaryKind) const {
    EventCallback callback;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        callback = onEvent_;
    }
    if (callback) {
        callback(
            state,
            text,
            isFinal,
            coveredThroughSegmentId,
            summaryKind);
    }
}

} // namespace meetingai::summary
