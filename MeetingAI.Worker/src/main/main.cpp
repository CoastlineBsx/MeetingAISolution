
#include <windows.h>
#include <sddl.h>
#include <atomic>
#include <iostream>
#include <string>
#include <memory>      // ← 新增：unique_ptr
#include <functional>  // ← 新增：function
#include <mutex>
#include <thread>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <future>
#include <stdexcept>
#include <unordered_map>
#include <vector>
#include <shlobj.h>
#include <codecvt>

// 然后包含项目头文件
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"
#include "whisper_openvino_transcriber.hpp"  // ← 新增：OpenVINO Whisper
#include "granite/granite_genai.hpp"  // ← OpenVINO 头文件
#include "embedding/embedding_genai.hpp"  // ← 新增：Embedding GenAI
#include "llava/llava_genai.h"  // ← 新增：LLaVA GenAI
#include "sd/sd_engine.hpp"  // ← 新增：Stable Diffusion
#include "sherpa_streaming_transcriber.h"  // ← 新增：Sherpa 流式转录
#include "punctuator.hpp"                  // ← 新增：中英标点恢复
#include "transcript_text_normalizer.hpp"
#include "translation/offline_translator.hpp"
#include "summary/meeting_summary_service.hpp"
#include "audio/meeting_audio_recorder.hpp"
#include "base64.h"  // ← 新增：Base64 解码
#include "command_parser.h"
#include "logging.h"
#include "pipe_security.h"

// OpenVINO Core for device enumeration
#include <openvino/openvino.hpp>

// ========== 热拔插支持：使用 mutex + bool 替代 once_flag ==========
static std::mutex g_granite_mutex;
static bool g_granite_loaded = false;

static std::mutex g_embedding_mutex;
static bool g_embedding_loaded = false;

static std::mutex g_llava_mutex;
static bool g_llava_loaded = false;

static std::mutex g_sd_mutex;
static bool g_sd_loaded = false;

static std::mutex g_sherpa_mutex;
static bool g_sherpa_loaded = false;
static std::string g_sherpa_recognizer_signature;

// ========== Granite GenAI 全局实例 ==========
static std::unique_ptr<meetingai::granite::GraniteGenAI> g_granite;
static std::string g_system_prompt = "你是一个专业、简洁的中文助手。请用简体中文回答问题，注重逻辑性和条理性。";
static int g_max_tokens = 256;
static float g_temperature = 0.7f;

// ========== Embedding GenAI 全局实例 ==========
static std::unique_ptr<meetingai::embedding::EmbeddingGenAI> g_embedding;

// ========== LLaVA GenAI 全局实例 ==========
static std::unique_ptr<llava::LLaVAGenAI> g_llava;

// ========== Stable Diffusion 全局实例 ==========
static std::unique_ptr<meetingai::sd::SDEngine> g_sd;

// ========== Sherpa-ONNX 流式转录全局实例 ==========
static std::unique_ptr<meetingai::transcribe::SherpaStreamingTranscriber> g_sherpa;
// 标点模型：可选，缺失时转录照常工作，只是不加标点（受 g_sherpa_mutex 保护）
static std::unique_ptr<meetingai::transcribe::Punctuator> g_punct;
static bool g_punct_attempted = false;
// Sherpa endpoint 只是声学分段，不等于一句话。保留尚未获得足够语义前瞻的
// 原始文本，直到标点模型确认句界或检测到长静音/停止。
static std::unordered_map<std::string, std::string> g_streaming_pending_raw;
static std::vector<std::string> g_streaming_active_sources;
static std::unordered_map<std::string, long long> g_streaming_utterance_ids;
static std::unordered_map<std::string, long long> g_streaming_last_end_ms;
static std::chrono::steady_clock::time_point g_streaming_started_at;
static std::int64_t g_streaming_meeting_id = 0;
static std::string g_streaming_translation_mode = "off";
static std::string g_streaming_whisper_hotwords;
static bool g_streaming_summary_enabled = false;
static bool g_streaming_recording_failed = false;

// final 原文先写 segment，译文回调再按 source + utterance_id 找回同一条
// segment，作为后续 revision 保存。
static std::mutex g_streaming_segment_mutex;
static std::unordered_map<
    std::string,
    std::unordered_map<long long, std::int64_t>>
    g_streaming_segment_ids;

// Granite 摘要运行在独立线程，绝不能占用命名管道的音频读取循环。
static meetingai::summary::MeetingSummaryService g_meeting_summary;
static meetingai::audio::MeetingAudioRecorder g_meeting_audio_recorder;

// 会后精修在后台完成。录音停止后 UI 立即进入“生成最终稿”状态，
// Worker 仍可持续回传 Whisper、翻译和最终纪要的进度。
static std::mutex g_postprocess_mutex;
static std::thread g_postprocess_thread;
static std::atomic<bool> g_postprocess_running{ false };
static std::atomic<bool> g_postprocess_pipe_available{ true };

// CTranslate2 翻译和 Sherpa 转录会从不同线程回写同一根命名管道。
// WriteFile 本身不能保证两个 JSON 消息不会交叉，因此统一串行化流式页面的回包。
static std::mutex g_streaming_pipe_write_mutex;
static meetingai::translation::OfflineTranslator g_offline_translator;

static void WriteStreamingMessage(
    HANDLE hPipe,
    const std::string& message) {
    std::lock_guard<std::mutex> lock(g_streaming_pipe_write_mutex);
    DWORD written = 0;
    WriteFile(
        hPipe,
        message.data(),
        static_cast<DWORD>(message.size()),
        &written,
        nullptr);
}

// 会后 Whisper/翻译/摘要的真实结果保存在 SQLite，管道这里只负责推送 UI
// 进度。若 Host 的读取循环意外停止，阻塞模式命名管道的同步 WriteFile 会
// 无限等待，过去因此把整个会后推理线程卡在 0%。给会后消息设置硬超时：
// 一次超时后本次任务不再推送消息，但数据库计算必须继续完成。
static bool WritePostProcessMessage(
    HANDLE hPipe,
    const std::string& message) {
    if (!g_postprocess_pipe_available.load()) {
        return false;
    }

    std::lock_guard<std::mutex> lock(g_streaming_pipe_write_mutex);
    if (!g_postprocess_pipe_available.load()) {
        return false;
    }

    HANDLE writerThread = nullptr;
    HANDLE completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!completed ||
        !DuplicateHandle(
            GetCurrentProcess(),
            GetCurrentThread(),
            GetCurrentProcess(),
            &writerThread,
            0,
            FALSE,
            DUPLICATE_SAME_ACCESS)) {
        if (completed) {
            CloseHandle(completed);
        }
        g_postprocess_pipe_available.store(false);
        return false;
    }

    std::thread cancelWrite([completed, writerThread] {
        if (WaitForSingleObject(completed, 1500) == WAIT_TIMEOUT) {
            CancelSynchronousIo(writerThread);
        }
        CloseHandle(writerThread);
    });

    DWORD written = 0;
    const BOOL succeeded = WriteFile(
        hPipe,
        message.data(),
        static_cast<DWORD>(message.size()),
        &written,
        nullptr);
    SetEvent(completed);
    cancelWrite.join();
    CloseHandle(completed);

    const bool completeWrite =
        succeeded && written == static_cast<DWORD>(message.size());
    if (!completeWrite) {
        g_postprocess_pipe_available.store(false);
        std::cerr
            << "[IPC] Host stopped reading post-process updates; "
               "continuing with database-only progress.\n";
    }
    return completeWrite;
}

// ========== 设备配置 ==========
static std::string g_granite_device = "GPU";   // Granite LLM 使用的设备
static std::string g_embedding_device = "GPU"; // Embedding 使用的设备
static std::string g_llava_device = "NPU";     // LLaVA 使用的设备
static std::string g_sd_device = "NPU";        // Stable Diffusion 使用的设备

static std::string GetEnvOrDefault(
    const char* key,
    const std::string& fallback);

static void StartMeetingSummaryService(
    HANDLE hPipe,
    std::int64_t meetingId,
    bool postMeeting = false) {
    if (meetingId <= 0) {
        return;
    }

    // 模型生命周期由 Startup 页面统一管理。摘要服务只检查状态，
    // 绝不在会议页面隐式加载 Granite。
    const std::string checkingMessage =
        "{\"type\":\"streaming_summary_status\","
        "\"state\":\"checking\","
        "\"message\":\""
        + std::string(
            postMeeting
                ? "正在检查会后 Granite 摘要服务…"
                : "正在检查 Granite 会议摘要模型…")
        + "\"}\n";
    if (postMeeting) {
        WritePostProcessMessage(hPipe, checkingMessage);
    }
    else {
        WriteStreamingMessage(hPipe, checkingMessage);
    }

    g_meeting_summary.Start(
        meetingId,
        [](std::string& error) -> bool {
            std::lock_guard<std::mutex> lock(g_granite_mutex);
            if (g_granite) {
                g_granite_loaded = true;
                return true;
            }
            g_granite_loaded = false;
            error =
                "Granite 未加载，请先到 Startup 页面手动加载";
            return false;
        },
        [](const std::string& prompt,
           const std::string& jsonSchema,
           const meetingai::summary::MeetingSummaryService::PartialCallback&
               onPartial) -> std::string {
            std::lock_guard<std::mutex> lock(g_granite_mutex);
            if (!g_granite) {
                throw std::runtime_error("Granite 模型未加载");
            }

            const std::string output =
                g_granite->generateStructuredInstruct(
                "你是完全离线运行的专业会议纪要助手。"
                "你必须只依据用户提供的带编号会议原文回答。"
                "原文没有提供某类信息时，将对应数组留空。"
                "绝不编造公司、产品、人物、金额、日期、决定和行动项。",
                prompt,
                jsonSchema,
                1024,
                0.0f);
            onPartial(output);
            return output;
        },
        [hPipe, postMeeting](
            const std::string& state,
            const std::string& text,
            bool isFinal,
            std::int64_t coveredThroughSegmentId,
            const std::string& summaryKind) {
            std::string message;
            if (state == "partial" || state == "final") {
                const char* type = state == "partial"
                    ? "streaming_summary_partial"
                    : "streaming_summary_final";
                message =
                    "{\"type\":\"" + std::string(type)
                    + "\",\"text\":\""
                    + meetingai::proto::jsonEscape(text)
                    + "\",\"is_final\":"
                    + (isFinal ? "true" : "false")
                    + ",\"summary_kind\":\""
                    + meetingai::proto::jsonEscape(summaryKind)
                    + "\""
                    + ",\"covered_through_segment_id\":"
                    + std::to_string(coveredThroughSegmentId)
                    + "}\n";
            }
            else {
                message =
                    "{\"type\":\"streaming_summary_status\",\"state\":\""
                    + meetingai::proto::jsonEscape(state)
                    + "\",\"message\":\""
                    + meetingai::proto::jsonEscape(text)
                    + "\"}\n";
            }
            if (postMeeting) {
                WritePostProcessMessage(hPipe, message);
            }
            else {
                WriteStreamingMessage(hPipe, message);
            }
        });
}

static void CloseStreamingMeetingRecord() {
    if (g_streaming_meeting_id > 0) {
        if (!EndStreamingMeeting(g_streaming_meeting_id)) {
            std::cerr << "[DB] failed to close streaming meeting id="
                      << g_streaming_meeting_id << "\n";
        }
        g_streaming_meeting_id = 0;
    }
}

static void ResetStreamingPersistenceState() {
    std::lock_guard<std::mutex> lock(g_streaming_segment_mutex);
    g_streaming_segment_ids.clear();
    g_streaming_last_end_ms.clear();
}

struct RefinedMeetingSegment {
    std::int64_t segmentId = 0;
    std::string source;
    std::int64_t sequence = 0;
    std::int64_t startMs = 0;
    std::int64_t endMs = 0;
    std::string text;
};

// Whisper DTW 时间块约 10 秒一切，经常拦腰截断句子。判断累计文本是否
// 已经到句尾（中英文终止标点），作为合并块的封口信号。
static bool EndsWithSentenceTerminator(const std::string& text) {
    static const char* kTerminators[] = {
        ".", "!", "?", "\xE2\x80\xA6",          // … U+2026
        "\xE3\x80\x82",                          // 。 U+3002
        "\xEF\xBC\x81",                          // ！ U+FF01
        "\xEF\xBC\x9F"                           // ？ U+FF1F
    };
    for (const char* terminator : kTerminators) {
        const std::size_t length = std::strlen(terminator);
        if (text.size() >= length &&
            text.compare(text.size() - length, length, terminator) == 0) {
            return true;
        }
    }
    return false;
}

// 把 Whisper 的时间块合并为句子级片段：句尾标点封口；块间静音超过
// 3 秒或累计过长也封口。时间戳取首块起点、末块终点。
static std::vector<meetingai::transcribe::WhisperOpenVINOSegment>
MergeWhisperChunksIntoSentences(
    const std::vector<meetingai::transcribe::WhisperOpenVINOSegment>&
        chunks) {
    constexpr float kMaxGapSeconds = 3.0f;
    constexpr std::size_t kMaxSegmentBytes = 400;

    std::vector<meetingai::transcribe::WhisperOpenVINOSegment> merged;
    meetingai::transcribe::WhisperOpenVINOSegment current{};
    bool hasCurrent = false;

    auto flush = [&merged, &current, &hasCurrent]() {
        if (hasCurrent && !current.text.empty()) {
            merged.push_back(current);
        }
        hasCurrent = false;
    };

    for (const auto& chunk : chunks) {
        const std::string chunkText =
            meetingai::proto::trim(chunk.text);
        if (chunkText.empty()) {
            continue;
        }

        if (hasCurrent &&
            chunk.start_ts - current.end_ts > kMaxGapSeconds) {
            flush();
        }

        if (!hasCurrent) {
            current = chunk;
            current.text = chunkText;
            hasCurrent = true;
        }
        else {
            current.text =
                meetingai::transcribe::JoinTranscriptFragments(
                    current.text,
                    chunkText);
            current.end_ts = chunk.end_ts;
        }

        if (EndsWithSentenceTerminator(current.text) ||
            current.text.size() >= kMaxSegmentBytes) {
            flush();
        }
    }
    flush();
    return merged;
}

static void SendPostProcessStatus(
    HANDLE hPipe,
    std::int64_t meetingId,
    const std::string& state,
    int progress,
    const std::string& message) {
    WritePostProcessMessage(
        hPipe,
        "{\"type\":\"streaming_postprocess_status\","
        "\"meeting_id\":" + std::to_string(meetingId) +
        ",\"state\":\"" + meetingai::proto::jsonEscape(state) +
        "\",\"progress\":" +
        std::to_string(std::clamp(progress, 0, 100)) +
        ",\"message\":\"" +
        meetingai::proto::jsonEscape(message) + "\"}\n");
}

static void RunMeetingPostProcess(
    HANDLE hPipe,
    std::int64_t meetingId,
    std::unordered_map<std::string, std::string> audioPaths,
    std::string translationMode,
    std::string whisperHotwords,
    bool summaryEnabled) {
    g_postprocess_pipe_available.store(true);
    std::int64_t runId = 0;
    try {
        if (meetingId <= 0 || audioPaths.empty()) {
            throw std::runtime_error("没有可用于最终精修的会议录音");
        }

        runId = BeginTranscriptionRun(
            meetingId,
            "OpenVINO Whisper",
            "Whisper large-v3",
            "OpenVINO GenAI",
            translationMode,
            whisperHotwords);
        if (runId <= 0) {
            throw std::runtime_error("无法创建会后精修任务");
        }

        // 管道随时可能已经断开（Host 页面切换/重连），DB 轮询是唯一保底
        // 通道。若这里不写库，progress 会在 transcribing 状态下停在建表
        // 时的初始值 0，看起来像卡死，实际只是还没等到第一次真实进度回调。
        UpdateTranscriptionRun(runId, "transcribing", 2);
        SendPostProcessStatus(
            hPipe,
            meetingId,
            "transcribing",
            2,
            "正在使用 OpenVINO Whisper 生成高精度最终稿…");
        WritePostProcessMessage(
            hPipe,
            "{\"type\":\"streaming_postprocess_transcript_reset\","
            "\"meeting_id\":" + std::to_string(meetingId) + "}\n");

        const std::string modelPath =
            meetingai::util::resolveModelFileUtf8(
                L"whisper_large_v3");
        std::vector<RefinedMeetingSegment> refinedSegments;
        std::size_t sourceIndex = 0;
        const std::size_t sourceCount = audioPaths.size();
        for (const std::string& source :
             std::vector<std::string>{ "microphone", "system" }) {
            const auto path = audioPaths.find(source);
            if (path == audioPaths.end()) {
                continue;
            }

            const int sourceBase =
                5 + static_cast<int>(
                    sourceIndex * 65 / std::max<std::size_t>(1, sourceCount));
            const int sourceSpan =
                static_cast<int>(
                    65 / std::max<std::size_t>(1, sourceCount));
            std::vector<
                meetingai::transcribe::WhisperOpenVINOSegment> segments;
            const bool transcribed =
                meetingai::transcribe::TranscribeAudioFileOpenVINO(
                    modelPath,
                    path->second,
                    segments,
                    "auto",
                    [hPipe,
                     meetingId,
                     runId,
                     sourceBase,
                     sourceSpan](int localProgress) {
                        const int overall =
                            sourceBase +
                            localProgress * sourceSpan / 100;
                        UpdateTranscriptionRun(
                            runId,
                            "transcribing",
                            overall);
                        SendPostProcessStatus(
                            hPipe,
                            meetingId,
                            "transcribing",
                            overall,
                            "Whisper 正在精修会议录音…");
                    },
                    whisperHotwords);
            if (!transcribed) {
                throw std::runtime_error(
                    (source == "system" ? "对方" : "我方") +
                    std::string("音频的 Whisper 转录失败"));
            }

            // 时间块 → 句子级片段，避免最终稿出现 10 秒硬切的断句。
            const auto sentenceSegments =
                MergeWhisperChunksIntoSentences(segments);

            std::int64_t sequence = 1;
            for (const auto& whisperSegment : sentenceSegments) {
                const std::string rawText =
                    meetingai::proto::trim(whisperSegment.text);
                if (rawText.empty()) {
                    continue;
                }
                std::string finalText =
                    meetingai::transcribe::NormalizeBilingualTranscript(
                        rawText);
                if (finalText.empty()) {
                    finalText = rawText;
                }
                const std::int64_t startMs =
                    std::max<std::int64_t>(
                        0,
                        static_cast<std::int64_t>(
                            whisperSegment.start_ts * 1000.0f));
                const std::int64_t endMs =
                    std::max<std::int64_t>(
                        startMs,
                        static_cast<std::int64_t>(
                            whisperSegment.end_ts * 1000.0f));
                const std::int64_t segmentId =
                    InsertWhisperFinalSegment(
                        runId,
                        meetingId,
                        source,
                        sequence,
                        startMs,
                        endMs,
                        rawText,
                        finalText);
                if (segmentId <= 0) {
                    throw std::runtime_error(
                        "Whisper 最终字幕写入数据库失败");
                }

                refinedSegments.push_back({
                    segmentId,
                    source,
                    sequence,
                    startMs,
                    endMs,
                    finalText
                });
                WritePostProcessMessage(
                    hPipe,
                    "{\"type\":\"streaming_postprocess_segment\","
                    "\"meeting_id\":" + std::to_string(meetingId) +
                    ",\"segment_id\":" + std::to_string(segmentId) +
                    ",\"source\":\"" +
                    meetingai::proto::jsonEscape(source) +
                    "\",\"sequence\":" + std::to_string(sequence) +
                    ",\"start_ms\":" + std::to_string(startMs) +
                    ",\"end_ms\":" + std::to_string(endMs) +
                    ",\"text\":\"" +
                    meetingai::proto::jsonEscape(finalText) +
                    "\"}\n");
                ++sequence;
            }
            ++sourceIndex;
        }

        if (refinedSegments.empty()) {
            throw std::runtime_error(
                "Whisper 未从会议录音中识别出有效文字");
        }

        // 从这一刻开始，数据库的规范会议原文切换到 Whisper 版本。
        if (!UpdateTranscriptionRun(
                runId,
                translationMode == "off"
                    ? "summarizing"
                    : "translating",
                72,
                {},
                true)) {
            throw std::runtime_error("无法发布 Whisper 最终稿");
        }

        if (translationMode != "off") {
            SendPostProcessStatus(
                hPipe,
                meetingId,
                "translating",
                74,
                "正在生成最终译文…");
            const std::string enZhModelDir =
                meetingai::util::resolveModelFileUtf8(
                    L"translation\\opus-mt-en-zh");
            const std::string zhEnModelDir =
                meetingai::util::resolveModelFileUtf8(
                    L"translation\\opus-mt-zh-en");
            const bool translationReady = g_offline_translator.Start(
                translationMode,
                enZhModelDir,
                zhEnModelDir,
                [hPipe, meetingId](
                    const meetingai::translation::TranslationEvent& event) {
                    if (event.targetLanguage.rfind("error:", 0) == 0) {
                        WritePostProcessMessage(
                            hPipe,
                            "{\"type\":\"streaming_postprocess_warning\","
                            "\"meeting_id\":" +
                            std::to_string(meetingId) +
                            ",\"message\":\"" +
                            meetingai::proto::jsonEscape(
                                event.targetLanguage.substr(6)) +
                            "\"}\n");
                        return;
                    }
                    const std::int64_t segmentId =
                        static_cast<std::int64_t>(event.utteranceId);
                    if (!InsertStreamingTranslation(
                            segmentId,
                            event.targetLanguage,
                            event.text)) {
                        WritePostProcessMessage(
                            hPipe,
                            "{\"type\":\"streaming_postprocess_warning\","
                            "\"meeting_id\":" +
                            std::to_string(meetingId) +
                            ",\"message\":\"最终译文写入数据库失败\"}\n");
                        return;
                    }
                    WritePostProcessMessage(
                        hPipe,
                        "{\"type\":\"streaming_postprocess_translation\","
                        "\"meeting_id\":" +
                        std::to_string(meetingId) +
                        ",\"segment_id\":" +
                        std::to_string(segmentId) +
                        ",\"source\":\"" +
                        meetingai::proto::jsonEscape(event.source) +
                        "\",\"target_language\":\"" +
                        meetingai::proto::jsonEscape(
                            event.targetLanguage) +
                        "\",\"text\":\"" +
                        meetingai::proto::jsonEscape(event.text) +
                        "\"}\n");
                });
            if (translationReady) {
                for (const auto& segment : refinedSegments) {
                    g_offline_translator.Submit(
                        segment.source,
                        segment.segmentId,
                        segment.text,
                        true);
                }
                g_offline_translator.Stop(true);
            }
            else {
                WritePostProcessMessage(
                    hPipe,
                    "{\"type\":\"streaming_postprocess_warning\","
                    "\"meeting_id\":" + std::to_string(meetingId) +
                    ",\"message\":\"最终译文暂未生成: " +
                    meetingai::proto::jsonEscape(
                        g_offline_translator.GetLastError()) +
                    "\"}\n");
            }
        }

        if (summaryEnabled) {
            if (!UpdateTranscriptionRun(
                runId,
                "summarizing",
                90)) {
                throw std::runtime_error(
                    "无法保存最终会议纪要任务状态");
            }
            SendPostProcessStatus(
                hPipe,
                meetingId,
                "summarizing",
                90,
                "正在依据 Whisper 最终稿生成最终会议纪要…");
            StartMeetingSummaryService(hPipe, meetingId, true);
            auto summaryFinalizer = std::async(
                std::launch::async,
                [] {
                    g_meeting_summary.Stop(true);
                });
            const auto summaryStartedAt =
                std::chrono::steady_clock::now();
            while (summaryFinalizer.wait_for(
                       std::chrono::seconds(5)) !=
                   std::future_status::ready) {
                const auto elapsedSeconds =
                    std::chrono::duration_cast<std::chrono::seconds>(
                        std::chrono::steady_clock::now() -
                        summaryStartedAt).count();
                const int progress = std::min(
                    97,
                    90 + static_cast<int>(elapsedSeconds / 20));
                const std::string message =
                    "Granite 正在生成最终会议纪要 · 已用时 " +
                    std::to_string(elapsedSeconds) + " 秒";
                UpdateTranscriptionRun(
                    runId,
                    "summarizing",
                    progress);
                SendPostProcessStatus(
                    hPipe,
                    meetingId,
                    "summarizing",
                    progress,
                    message);
            }
            summaryFinalizer.get();
            UpdateTranscriptionRun(
                runId,
                "saving",
                98);
            SendPostProcessStatus(
                hPipe,
                meetingId,
                "saving",
                98,
                "最终会议纪要已生成，正在保存任务状态…");
        }

        if (!UpdateTranscriptionRun(
                runId,
                "complete",
                100,
                {},
                true)) {
            // SQLite 短暂忙碌时再试一次，避免内容已写入但任务永久停在 90%。
            std::this_thread::sleep_for(
                std::chrono::milliseconds(150));
            if (!UpdateTranscriptionRun(
                    runId,
                    "complete",
                    100,
                    {},
                    true)) {
                throw std::runtime_error(
                    "最终稿已生成，但任务完成状态写入数据库失败");
            }
        }
        SendPostProcessStatus(
            hPipe,
            meetingId,
            "complete",
            100,
            "会议最终稿处理已完成");
        WritePostProcessMessage(
            hPipe,
            "{\"type\":\"streaming_postprocess_complete\","
            "\"meeting_id\":" + std::to_string(meetingId) +
            ",\"transcription_run_id\":" + std::to_string(runId) +
            ",\"segments\":" +
            std::to_string(refinedSegments.size()) + "}\n");

    }
    catch (const std::exception& exception) {
        g_offline_translator.Stop(false);
        g_meeting_summary.Stop(false);
        if (runId > 0) {
            UpdateTranscriptionRun(
                runId,
                "failed",
                100,
                exception.what());
        }
        SendPostProcessStatus(
            hPipe,
            meetingId,
            "failed",
            100,
            exception.what());
        WritePostProcessMessage(
            hPipe,
            "{\"type\":\"streaming_postprocess_error\","
            "\"meeting_id\":" + std::to_string(meetingId) +
            ",\"message\":\"" +
            meetingai::proto::jsonEscape(exception.what()) +
            "\"}\n");
    }

    g_postprocess_running.store(false);
    WritePostProcessMessage(
        hPipe,
        "{\"type\":\"streaming_stopped\"}\n");
}

static bool StartMeetingPostProcess(
    HANDLE hPipe,
    std::int64_t meetingId,
    std::unordered_map<std::string, std::string> audioPaths,
    const std::string& translationMode,
    const std::string& whisperHotwords,
    bool summaryEnabled) {
    std::lock_guard<std::mutex> lock(g_postprocess_mutex);
    if (g_postprocess_running.load()) {
        return false;
    }
    if (g_postprocess_thread.joinable()) {
        g_postprocess_thread.join();
    }
    g_postprocess_running.store(true);
    g_postprocess_thread = std::thread(
        RunMeetingPostProcess,
        hPipe,
        meetingId,
        std::move(audioPaths),
        translationMode,
        whisperHotwords,
        summaryEnabled);
    return true;
}

// ========== 工具函数：解码 JSON Unicode 转义序列 ==========
static std::string decodeJsonUnicode(const std::string& str) {
    std::string result;
    result.reserve(str.length());

    for (size_t i = 0; i < str.length(); i++) {
        if (str[i] == '\\' && i + 5 < str.length() && str[i + 1] == 'u') {
            // 解析 \uXXXX
            std::string hex = str.substr(i + 2, 4);
            try {
                int code_point = std::stoi(hex, nullptr, 16);

                // 将 Unicode 码点转换为 UTF-8
                if (code_point <= 0x7F) {
                    result += static_cast<char>(code_point);
                } else if (code_point <= 0x7FF) {
                    result += static_cast<char>(0xC0 | ((code_point >> 6) & 0x1F));
                    result += static_cast<char>(0x80 | (code_point & 0x3F));
                } else {
                    result += static_cast<char>(0xE0 | ((code_point >> 12) & 0x0F));
                    result += static_cast<char>(0x80 | ((code_point >> 6) & 0x3F));
                    result += static_cast<char>(0x80 | (code_point & 0x3F));
                }
                i += 5; // 跳过 \uXXXX
            } catch (...) {
                result += str[i]; // 解析失败，保留原字符
            }
        } else {
            result += str[i];
        }
    }

    return result;
}

// ========== 工具函数：获取环境变量 ==========
static std::string GetEnvOrDefault(const char* key, const std::string& fallback) {
    char* buf = nullptr;
    size_t len = 0;
    if (_dupenv_s(&buf, &len, key) == 0 && buf != nullptr) {
        std::string value(buf);
        free(buf);
        if (!value.empty()) {
            return value;
        }
    }
    return std::string(fallback);
}


// --------- 追加：通用工具 & 退出标志 ----------
static volatile BOOL g_shutdownRequested = FALSE;
// 用于回调里把段结果写回 Host
HANDLE g_pipe_for_callback = NULL;


// ========== Granite GenAI 初始化 ==========
static void InitializeGraniteGenAI(HANDLE hPipe, const std::string& device = "CPU") {
    std::wcout << L"[Worker] 初始化 Granite GenAI...\n";
    try {
        // 枚举可用设备并通过管道发送
        ov::Core core;
        auto available_devices = core.get_available_devices();

        std::string devices_msg = "{\"type\":\"info\",\"message\":\"[OpenVINO] 可用设备:\\n";
        for (const auto& dev : available_devices) {
            devices_msg += "  - " + dev;
            try {
                auto full_name = core.get_property(dev, ov::device::full_name);
                devices_msg += " (" + full_name + ")";
            } catch (...) {}
            devices_msg += "\\n";
        }
        devices_msg += "  将使用: " + device + "\"}\n";

        DWORD written;
        WriteFile(hPipe, devices_msg.data(), (DWORD)devices_msg.size(), &written, nullptr);

        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_GRANITE_MODEL",
            meetingai::util::resolveModelFileUtf8(L"granite-3.3-2b-npu")
        );

        g_granite = std::make_unique<meetingai::granite::GraniteGenAI>(model_dir, device);
        std::wcout << L"[Worker] Granite GenAI ✅ 初始化成功: " << device.c_str() << L"\n";

        // 通知 Host 模型已就绪
        std::string ready = "{\"type\":\"granite_ready\",\"device\":\"" + device + "\"}\n";
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] Granite GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"Granite 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Embedding GenAI 初始化 ==========
static void InitializeEmbeddingGenAI(HANDLE hPipe, const std::string& device = "CPU") {
    std::wcout << L"[Worker] 初始化 Embedding GenAI...\n";
    try {
        // 枚举可用设备并通过管道发送
        ov::Core core;
        auto available_devices = core.get_available_devices();

        std::string devices_msg = "{\"type\":\"info\",\"message\":\"[OpenVINO] 可用设备:\\n";
        for (const auto& dev : available_devices) {
            devices_msg += "  - " + dev;
            try {
                auto full_name = core.get_property(dev, ov::device::full_name);
                devices_msg += " (" + full_name + ")";
            } catch (...) {}
            devices_msg += "\\n";
        }
        devices_msg += "  将使用: " + device + "\"}";

        DWORD written;
        WriteFile(hPipe, devices_msg.data(), (DWORD)devices_msg.size(), &written, nullptr);

        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_EMBEDDING_MODEL",
            meetingai::util::resolveModelFileUtf8(L"bge-m3-npu")
        );

        g_embedding = std::make_unique<meetingai::embedding::EmbeddingGenAI>(model_dir, device);
        std::wcout << L"[Worker] Embedding GenAI ✅ 初始化成功: " << device.c_str()
                   << L" (dim=" << g_embedding->embedding_dim() << L")\n";

        // 通知 Host 模型已就绪
        std::string ready = std::string("{\"type\":\"embedding_ready\",\"device\":\"") +
                           device + "\",\"dim\":" + std::to_string(g_embedding->embedding_dim()) + "}\n";
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] Embedding GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"Embedding 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== LLaVA GenAI 初始化 ==========
static void InitializeLLaVAGenAI(HANDLE hPipe, const std::string& device = "NPU") {
    std::wcout << L"[Worker] 初始化 LLaVA GenAI...\n";
    try {
        // 使用模型目录路径（包含所有 LLaVA 模型文件）
        const std::string model_path = GetEnvOrDefault(
            "MEETINGAI_LLAVA_MODEL",
            meetingai::util::resolveModelFileUtf8(L"llava")
        );

        // 使用 VLMPipeline API，直接传入模型目录和设备
        g_llava = std::make_unique<llava::LLaVAGenAI>(model_path, device);

        std::wcout << L"[Worker] LLaVA GenAI ✅ 初始化成功: " << device.c_str() << L"\n";

        // 通知 Host 模型已就绪
        std::string ready = std::string("{\"type\":\"llava_ready\",\"device\":\"") + device + "\"}\n";
        DWORD written;
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] LLaVA GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"LLaVA 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== LLaVA GenAI 命令处理 ==========
static void handleLLaVACommand(HANDLE hPipe, const std::string& command) {
    try {
        // ========== 新增：独立的 load_llava 命令处理 ==========
        if (command.find("\"type\":\"load_llava\"") != std::string::npos) {
            DWORD written;

            std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 收到 load_llava 命令\"}\n";
            WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

            // 解析设备参数
            std::string device = "GPU";  // 默认使用 GPU
            auto devicePos = command.find("\"device\":\"");
            if (devicePos != std::string::npos) {
                auto start = devicePos + 10;
                auto end = command.find("\"", start);
                if (end != std::string::npos) {
                    device = command.substr(start, end - start);
                }
            }

            std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker] LLaVA 设备: " + device + "\"}\n";
            WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

            std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker] 开始加载 LLaVA 模型（这可能需要 30-60 秒）...\"}\n";
            WriteFile(hPipe, debug3.data(), (DWORD)debug3.size(), &written, nullptr);

            // 调用初始化函数（支持热拔插）
            {
                std::lock_guard<std::mutex> lock(g_llava_mutex);
                if (!g_llava_loaded) {
                    InitializeLLaVAGenAI(hPipe, device);
                    g_llava_loaded = g_llava != nullptr;
                }
            }

            std::string debug4 = "{\"type\":\"info\",\"message\":\"[Worker] LLaVA 加载完成\"}\n";
            WriteFile(hPipe, debug4.data(), (DWORD)debug4.size(), &written, nullptr);

            return;
        }

        // 检查模型是否已加载
        if (!g_llava) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ LLaVA 模型未加载，请先点击'加载LLaVA模型'按钮\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 辅助函数：提取 image_path 字段
        auto extractImagePath = [](const std::string& json) -> std::string {
            size_t pos = json.find("\"image_path\"");
            if (pos == std::string::npos) return "";
            size_t colonPos = json.find(":", pos);
            if (colonPos == std::string::npos) return "";
            size_t quoteStart = json.find("\"", colonPos);
            if (quoteStart == std::string::npos) return "";
            size_t quoteEnd = json.find("\"", quoteStart + 1);
            if (quoteEnd == std::string::npos) return "";
            return json.substr(quoteStart + 1, quoteEnd - quoteStart - 1);
        };

        // -------- 单轮模式：生成 --------
        if (command.find("\"llava_generate\"") != std::string::npos) {
            std::string image_path = extractImagePath(command);
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, 512);
            float temp = meetingai::proto::extractTemperature(command, 0.7f);

            std::wcout << L"[LLaVA] 单轮生成: " << image_path.c_str() << L"\n";

            g_llava->generateStream(image_path, prompt, [&](const std::string& token) {
                std::string chunk = "{\"type\":\"llava_token\",\"token\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"llava_complete\"}\n");
        }
        // -------- 多轮模式：开始会话 --------
        else if (command.find("\"llava_start_chat\"") != std::string::npos) {
            std::string image_path = extractImagePath(command);
            g_llava->startChat(image_path);
            write_json("{\"type\":\"llava_chat_status\",\"status\":\"started\"}\n");
            std::wcout << L"[LLaVA] 多轮会话已开始\n";
        }
        // -------- 多轮模式：流式对话 --------
        else if (command.find("\"llava_chat_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, 512);
            float temp = meetingai::proto::extractTemperature(command, 0.7f);

            std::wcout << L"[LLaVA] 多轮对话: " << prompt.c_str() << L"\n";

            g_llava->chatStream(prompt, [&](const std::string& token) {
                std::string chunk = "{\"type\":\"llava_token\",\"token\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"llava_complete\"}\n");
        }
        // -------- 多轮模式：结束会话 --------
        else if (command.find("\"llava_finish_chat\"") != std::string::npos) {
            g_llava->finishChat();
            write_json("{\"type\":\"llava_chat_status\",\"status\":\"finished\"}\n");
            std::wcout << L"[LLaVA] 多轮会话已结束\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[LLaVA] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// 单轮流式生成不能占用管道命令线程：生成几十秒期间 streaming_audio
// 命令会全部排队，实时字幕停摆。放到后台线程执行，并且必须持
// g_granite_mutex —— 实时摘要线程也在用同一个 Granite 管线，无锁并发
// 会把摘要服务打进错误状态。
static std::mutex g_granite_stream_start_mutex;
static std::thread g_granite_stream_thread;
static std::atomic<bool> g_granite_stream_running{ false };

static void RunGraniteStreamGenerate(
    HANDLE hPipe,
    std::string prompt,
    int maxTokens,
    float temperature) {
    try {
        std::lock_guard<std::mutex> graniteLock(g_granite_mutex);
        if (!g_granite) {
            throw std::runtime_error("Granite 模型未加载");
        }
        g_granite->generateStream(
            prompt,
            [hPipe](const std::string& token) {
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"token\",\"text\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n");
            },
            maxTokens,
            temperature);
        WriteStreamingMessage(hPipe, "{\"type\":\"done\"}\n");
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Granite] 后台生成异常: " << e.what() << L"\n";
        WriteStreamingMessage(
            hPipe,
            "{\"type\":\"error\",\"message\":\"" +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n");
        WriteStreamingMessage(hPipe, "{\"type\":\"done\"}\n");
    }
    g_granite_stream_running.store(false);
}

// ========== Granite GenAI 命令处理 ==========
static void handleGraniteCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 检查模型是否已加载（不再自动懒加载）
        if (!g_granite) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Granite 模型未加载，请先点击'开始加载模型'按钮\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 解析命令类型
        size_t typePos = command.find("\"type\"");
        if (typePos == std::string::npos) return;

        // -------- 单轮流式生成（后台线程，不阻塞音频命令） --------
        if (command.find("\"granite_generate_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, g_max_tokens);
            float temp = meetingai::proto::extractTemperature(command, g_temperature);

            std::wcout << L"[Granite] 单轮生成(后台): "
                       << prompt.substr(0, 80).c_str() << L"...\n";

            std::lock_guard<std::mutex> startLock(
                g_granite_stream_start_mutex);
            if (g_granite_stream_running.load()) {
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"token\",\"text\":\"Granite 正忙，请稍后再试。\"}\n");
                WriteStreamingMessage(hPipe, "{\"type\":\"done\"}\n");
                return;
            }
            if (g_granite_stream_thread.joinable()) {
                g_granite_stream_thread.join();
            }
            g_granite_stream_running.store(true);
            g_granite_stream_thread = std::thread(
                RunGraniteStreamGenerate,
                hPipe,
                std::move(prompt),
                maxTokens,
                temp);
        }
        // -------- 多轮：开始会话 --------
        else if (command.find("\"granite_start_chat\"") != std::string::npos) {
            std::string sysMsg = meetingai::proto::extractSystemMessage(command, g_system_prompt);
            g_granite->startChat(sysMsg);
            write_json("{\"type\":\"granite_chat_status\",\"status\":\"started\"}\n");
            std::wcout << L"[Granite] 多轮会话已开始\n";
        }
        // -------- 多轮：流式对话 --------
        else if (command.find("\"granite_chat_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, g_max_tokens);
            float temp = meetingai::proto::extractTemperature(command, g_temperature);

            std::wcout << L"[Granite] 多轮对话: " << prompt.c_str() << L"\n";

            g_granite->chatStream(prompt, [&](const std::string& token) {
                std::string chunk = "{\"type\":\"token\",\"text\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"done\"}\n");
        }
        // -------- 多轮：结束会话 --------
        else if (command.find("\"granite_finish_chat\"") != std::string::npos) {
            g_granite->finishChat();
            write_json("{\"type\":\"granite_chat_status\",\"status\":\"finished\"}\n");
            std::wcout << L"[Granite] 多轮会话已结束\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Granite] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Embedding GenAI 命令处理 ==========
static void handleEmbeddingCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 检查模型是否已加载（不再自动懒加载）
        if (!g_embedding) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Embedding 模型未加载，请先点击'开始加载模型'按钮\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 解析命令类型
        if (command.find("\"embedding_encode\"") != std::string::npos) {
            std::string text = meetingai::proto::extractPrompt(command);

            std::wcout << L"[Embedding] 编码文本: " << text.substr(0, 50).c_str() << L"...\n";

            // 生成向量
            auto embedding = g_embedding->encode(text);

            // 构建 JSON 响应（向量转为数组）
            std::string response = "{\"type\":\"embedding_result\",\"embedding\":[";
            for (size_t i = 0; i < embedding.size(); i++) {
                response += std::to_string(embedding[i]);
                if (i < embedding.size() - 1) response += ",";
            }
            response += "]}\n";

            write_json(response);
            std::wcout << L"[Embedding] ✅ 编码完成 (dim=" << embedding.size() << L")\n";
        }
        else if (command.find("\"embedding_test_similarity\"") != std::string::npos) {
            // 诊断测试：测试多组文本对的相似度
            std::wcout << L"[Embedding] 开始相似度诊断测试...\n";

            // 测试用的文本对（覆盖不同类型）
            std::vector<std::pair<std::string, std::string>> test_pairs = {
                // === 组1：简单问候 vs 专业内容 ===
                {"你好", "量子力学的基本原理包括波粒二象性和不确定性原理"},
                {"你好", "白少雄在伦敦大学学院攻读法学硕士"},
                {"早上好", "神经网络的反向传播算法是深度学习的核心"},
                {"谢谢", "区块链技术采用分布式账本来确保数据安全"},

                // === 组2：日常对话 vs 技术内容 ===
                {"今天天气如何", "深度学习模型使用反向传播算法进行训练"},
                {"吃饭了吗", "自然语言处理需要大量的语料库进行训练"},
                {"周末愉快", "卷积神经网络广泛应用于图像识别领域"},

                // === 组3：单个词 vs 长文本 ===
                {"苹果", "TCP/IP协议是互联网通信的基础协议栈"},
                {"学习", "人工智能的发展需要数学、统计学和计算机科学的结合"},
                {"电脑", "量子计算机利用量子叠加态进行并行计算"},

                // === 组4：人名相关（应该高相似度）===
                {"白少雄", "白少雄是一个计算机科学博士生"},
                {"白少雄", "白少雄在伦敦大学学院学习"},
                {"白少雄研究", "白少雄的研究方向是人工智能与法律"},

                // === 组5：语义相关（应该中等相似度）===
                {"机器学习", "人工智能是计算机科学的一个重要分支"},
                {"深度学习", "神经网络是模拟人脑工作的计算模型"},
                {"算法", "数据结构是计算机程序设计的基础"},

                // === 组6：完全无关 ===
                {"猫", "火箭发射需要精确的轨道计算"},
                {"音乐", "化学反应的速率取决于温度和催化剂"},
                {"颜色", "经济学研究资源的稀缺性和配置效率"},

                // === 组7：抽象概念 vs 具体描述 ===
                {"爱情", "心理学家认为人际关系建立在相互理解的基础上"},
                {"自由", "政治哲学探讨个人权利与社会责任的平衡"},
                {"科学", "实验方法是验证假设的重要手段"},

                // === 组8：短语 vs 相关内容 ===
                {"人工智能应用", "机器学习在医疗诊断中发挥重要作用"},
                {"数据分析", "统计学方法帮助我们从数据中提取有价值的信息"},
                {"编程语言", "Python因其简洁的语法而受到数据科学家的青睐"},

                // === 组9：短语 vs 不相关内容 ===
                {"编程学习", "美食烹饪需要掌握火候和调味技巧"},
                {"数学公式", "旅游景点的选择应考虑季节和交通便利性"},
                {"计算机网络", "园艺爱好者应该了解植物的生长习性"}
            };

            std::string result = "{\"type\":\"similarity_test_result\",\"pairs\":[";

            for (size_t i = 0; i < test_pairs.size(); i++) {
                const auto& pair = test_pairs[i];

                // 计算两个文本的 embedding
                auto emb1 = g_embedding->encode(pair.first);
                auto emb2 = g_embedding->encode(pair.second);

                // 计算余弦相似度
                float dot = 0.0f, norm1 = 0.0f, norm2 = 0.0f;
                for (size_t j = 0; j < emb1.size(); j++) {
                    dot += emb1[j] * emb2[j];
                    norm1 += emb1[j] * emb1[j];
                    norm2 += emb2[j] * emb2[j];
                }
                float similarity = dot / (sqrtf(norm1) * sqrtf(norm2));

                // 构建 JSON
                result += "{\"text1\":\"" + meetingai::proto::jsonEscape(pair.first) + "\",";
                result += "\"text2\":\"" + meetingai::proto::jsonEscape(pair.second) + "\",";
                result += "\"similarity\":" + std::to_string(similarity) + "}";

                if (i < test_pairs.size() - 1) result += ",";

                std::wcout << L"[Test] '" << pair.first.c_str() << L"' vs '"
                          << pair.second.c_str() << L"' = " << similarity << L"\n";
            }

            result += "]}\n";
            write_json(result);
            std::wcout << L"[Embedding] ✅ 诊断测试完成\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Embedding] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Stable Diffusion 初始化 ==========
static void InitializeSDEngine(HANDLE hPipe, const std::string& device = "NPU") {
    std::wcout << L"[Worker] 初始化 Stable Diffusion 引擎...\n";
    try {
        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_SD_MODEL",
            meetingai::util::resolveModelFileUtf8(L"stable-deffusion-1.5")
        );

        std::string info_msg = "{\"type\":\"info\",\"message\":\"[SD] 正在加载模型: " + model_dir + " (" + device + ")\"}\n";
        DWORD written;
        WriteFile(hPipe, info_msg.data(), (DWORD)info_msg.size(), &written, nullptr);

        g_sd = std::make_unique<meetingai::sd::SDEngine>(model_dir, device);

        if (g_sd->isInitialized()) {
            std::wcout << L"[Worker] ✅ Stable Diffusion 初始化成功\n";
            std::string success = "{\"type\":\"sd_ready\",\"message\":\"✅ SD 引擎已就绪\"}\n";
            WriteFile(hPipe, success.data(), (DWORD)success.size(), &written, nullptr);
        } else {
            throw std::runtime_error("SD Engine initialization failed");
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] ❌ SD 初始化失败: " << e.what() << L"\n";
        std::string error = std::string("{\"type\":\"error\",\"message\":\"SD 初始化失败: ") +
                           meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), (DWORD)error.size(), &written, nullptr);
    }
}

// ========== Stable Diffusion 命令处理 ==========
static void handleSDCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理 SD 生成命令\n";
    
    try {
        // 确保 SD 引擎已加载
        if (!g_sd) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ SD 引擎未加载\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 解析命令参数
        meetingai::sd::GenerationConfig config;
        
        // 提取 mode (text2img / img2img)
        std::string mode = "text2img";
        size_t mode_pos = command.find("\"mode\":\"");
        if (mode_pos != std::string::npos) {
            size_t start = mode_pos + 8;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                mode = command.substr(start, end - start);
            }
        }

        // 提取 prompt（解码 Unicode 转义序列）
        size_t prompt_pos = command.find("\"prompt\":\"");
        if (prompt_pos != std::string::npos) {
            size_t start = prompt_pos + 10;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                std::string raw_prompt = command.substr(start, end - start);
                config.prompt = decodeJsonUnicode(raw_prompt);
            }
        }

        // 提取 negative_prompt（解码 Unicode 转义序列）
        size_t neg_pos = command.find("\"negative_prompt\":\"");
        if (neg_pos != std::string::npos) {
            size_t start = neg_pos + 19;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                std::string raw_neg = command.substr(start, end - start);
                config.negative_prompt = decodeJsonUnicode(raw_neg);
            }
        }

        // 提取数值参数
        auto extract_int = [&](const std::string& key, int& value) {
            size_t pos = command.find("\"" + key + "\":");
            if (pos != std::string::npos) {
                size_t start = pos + key.length() + 3;
                size_t end = command.find_first_of(",}", start);
                if (end != std::string::npos) {
                    value = std::stoi(command.substr(start, end - start));
                }
            }
        };

        auto extract_float = [&](const std::string& key, float& value) {
            size_t pos = command.find("\"" + key + "\":");
            if (pos != std::string::npos) {
                size_t start = pos + key.length() + 3;
                size_t end = command.find_first_of(",}", start);
                if (end != std::string::npos) {
                    value = std::stof(command.substr(start, end - start));
                }
            }
        };

        extract_int("width", config.width);
        extract_int("height", config.height);
        extract_int("steps", config.num_inference_steps);
        extract_float("cfg_scale", config.guidance_scale);
        extract_int("seed", config.seed);

        // img2img 专用参数
        if (mode == "img2img") {
            size_t img_pos = command.find("\"input_image\":\"");
            if (img_pos != std::string::npos) {
                size_t start = img_pos + 15;
                size_t end = command.find("\"", start);
                if (end != std::string::npos) {
                    config.input_image_path = command.substr(start, end - start);
                }
            }
            extract_float("strength", config.strength);
        }

        // 进度回调
        auto progress_callback = [hPipe](int current, int total, const std::string& preview_path) {
            std::string progress = "{\"type\":\"sd_progress\",\"current\":" +
                                 std::to_string(current) +
                                 ",\"total\":" + std::to_string(total);
            
            if (!preview_path.empty()) {
                progress += ",\"preview\":\"" + meetingai::proto::jsonEscape(preview_path) + "\"";
            }
            progress += "}\n";

            DWORD written;
            WriteFile(hPipe, progress.data(), (DWORD)progress.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 生成图片
        std::string output_path;
        if (mode == "img2img") {
            output_path = g_sd->generateImageToImage(config, progress_callback);
        } else {
            output_path = g_sd->generateTextToImage(config, progress_callback);
        }

        // 发送结果
        if (!output_path.empty()) {
            std::string result = "{\"type\":\"sd_complete\",\"image_path\":\"" +
                               meetingai::proto::jsonEscape(output_path) + "\"}\n";
            DWORD written;
            std::cout << "[Worker] 正在发送 sd_complete 消息: " << result << std::flush;
            WriteFile(hPipe, result.data(), (DWORD)result.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
            std::cout << "[Worker] sd_complete 消息已发送, written=" << written << " bytes" << std::endl;

            std::wcout << L"[Worker] ✅ SD 生成完成: " << output_path.c_str() << L"\n";
        } else {
            std::string error = "{\"type\":\"error\",\"message\":\"生成失败: " +
                              meetingai::proto::jsonEscape(g_sd->getLastError()) + "\"}\n";
            DWORD written;
            WriteFile(hPipe, error.data(), (DWORD)error.size(), &written, nullptr);
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] SD 命令处理异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
                         meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Token 计数命令处理 ==========
static void handleCountTokensCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 检查 Embedding 模型是否已加载（使用 Embedding 的 tokenizer）
        if (!g_embedding) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Embedding 模型未加载，无法计算 token 数\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 提取文本内容
        std::string text = meetingai::proto::extractPrompt(command);

        if (text.empty()) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ 文本内容为空\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 使用 Embedding 的 tokenizer 计算 token 数
        auto token_count = g_embedding->countTokens(text);

        // 构建响应
        std::string response = "{\"type\":\"token_count_result\",\"count\":" +
                               std::to_string(token_count) +
                               ",\"text_length\":" + std::to_string(text.length()) + "}\n";

        DWORD written;
        WriteFile(hPipe, response.data(), (DWORD)response.size(), &written, nullptr);
        FlushFileBuffers(hPipe);

        std::wcout << L"[TokenCount] ✅ 计算完成: " << token_count << L" tokens (文本长度: " << text.length() << L" 字符)\n";
    }
    catch (const std::exception& e) {
        std::wcerr << L"[TokenCount] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// 新增：处理 OpenVINO Whisper 转录命令
static void handleTranscribeOpenVINOCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理 OpenVINO Whisper 转录命令\n";

    // 检查模型是否已加载
    if (!meetingai::transcribe::IsWhisperOpenVINOModelLoaded()) {
        std::string error = "{\"type\":\"error\",\"message\":\"OpenVINO Whisper 模型未加载。请先在 Startup 页面加载模型。\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        std::wcerr << L"[Worker] 错误：模型未加载\n";
        return;
    }

    // 提取文件路径
    std::string audioPath = meetingai::proto::extractPath(command);
    if (audioPath.empty()) {
        std::string error = "{\"type\":\"error\",\"message\":\"无法解析音频文件路径\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }

    std::wcout << L"[Worker] 音频文件路径: " << audioPath.c_str() << L"\n";

    // 提取language参数
    std::string language = meetingai::proto::extractLanguage(command);
    std::cout << "[Worker] 语言设置: " << language << std::endl;

    // OpenVINO 模型路径（从已加载的模型获取）
    std::string modelPath = meetingai::util::resolveModelFileUtf8(L"whisper_large_v3");
    std::cout << "[Worker] 使用已加载的模型\n";

    // 定义进度回调函数
    auto progressCallback = [hPipe](int progress) {
        std::string progressMsg = "{\"type\":\"progress\",\"value\":" +
            std::to_string(progress) + "}\n";
        DWORD written;
        WriteFile(hPipe, progressMsg.data(), static_cast<DWORD>(progressMsg.size()), &written, nullptr);
    };

    // 执行转录
    std::vector<meetingai::transcribe::WhisperOpenVINOSegment> segments;
    bool success = meetingai::transcribe::TranscribeAudioFileOpenVINO(
        modelPath,
        audioPath,
        segments,
        language,
        progressCallback
    );

    if (!success) {
        std::string error = "{\"type\":\"error\",\"message\":\"转录失败\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }

    // 发送每个转录片段
    for (const auto& segment : segments) {
        // 插入数据库
        InsertTranscript("Unknown", segment.text, segment.start_ts);

        // 发送给 Host（使用与whisper.cpp相同的格式保持兼容）
        std::string response = std::string("{\"type\":\"asr_segment\",\"text\":\"") +
            meetingai::proto::jsonEscape(segment.text) +
            "\",\"t0_ms\":" + std::to_string((int)(segment.start_ts * 1000)) +
            ",\"t1_ms\":" + std::to_string((int)(segment.end_ts * 1000)) + "}\n";

        DWORD written;
        WriteFile(hPipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr);

        std::wcout << L"[Worker] 发送片段: " << segment.text.c_str() << L"\n";
    }

    // 发送完成信号
    std::string complete = "{\"type\":\"transcribe_complete\",\"segments\":" +
        std::to_string(segments.size()) + "}\n";
    DWORD written;
    WriteFile(hPipe, complete.data(), static_cast<DWORD>(complete.size()), &written, nullptr);

    std::wcout << L"[Worker] OpenVINO Whisper 转录完成\n";
}

// 新增：处理 Sherpa-ONNX 流式转录命令
static void handleSherpaStreamingCommand(HANDLE hPipe, const std::string& command) {
    auto sendTranscript = [hPipe](
        const char* type,
        const std::string& source,
        const std::string& rawText,
        const std::string& text) {
        if (text.empty()) {
            return;
        }

        const bool isFinal = std::string(type) == "streaming_final";
        long long& utteranceId = g_streaming_utterance_ids[source];
        if (utteranceId <= 0) {
            utteranceId = 1;
        }

        std::int64_t segmentId = 0;
        if (isFinal && g_streaming_meeting_id > 0) {
            const auto now = std::chrono::steady_clock::now();
            const long long endMs =
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    now - g_streaming_started_at).count();
            const long long startMs =
                std::min(g_streaming_last_end_ms[source], endMs);
            segmentId = InsertStreamingFinal(
                g_streaming_meeting_id,
                source,
                utteranceId,
                startMs,
                endMs,
                rawText.empty() ? text : rawText,
                text);
            if (segmentId > 0) {
                {
                    std::lock_guard<std::mutex> lock(
                        g_streaming_segment_mutex);
                    g_streaming_segment_ids[source][utteranceId] =
                        segmentId;
                }
                g_streaming_last_end_ms[source] = endMs;
                g_meeting_summary.NotifyFinalTranscript(
                    source,
                    utteranceId,
                    text.size());
            }
            else {
                std::string dbError =
                    "{\"type\":\"streaming_persistence_error\","
                    "\"message\":\"最终字幕写入数据库失败\"}\n";
                WriteStreamingMessage(hPipe, dbError);
            }
        }

        std::string response = "{\"type\":\"" + std::string(type) +
            "\",\"source\":\"" + meetingai::proto::jsonEscape(source) +
            "\",\"utterance_id\":" + std::to_string(utteranceId) +
            ",\"segment_id\":" + std::to_string(segmentId) +
            ",\"text\":\"" + meetingai::proto::jsonEscape(text) + "\"}\n";
        WriteStreamingMessage(hPipe, response);

        if (!isFinal) {
            g_meeting_summary.UpdateLiveTranscript(
                source,
                utteranceId,
                text);
        }

        // 原文先发给 UI，再把翻译任务放入旁路线程；翻译速度不会反压音频。
        if (g_offline_translator.IsActive()) {
            g_offline_translator.Submit(
                source,
                utteranceId,
                text,
                isFinal);
        }

        if (isFinal) {
            ++utteranceId;
        }
    };

    auto flushPendingTranscript = [&sendTranscript](const std::string& source) {
        auto pendingIt = g_streaming_pending_raw.find(source);
        if (pendingIt == g_streaming_pending_raw.end() ||
            pendingIt->second.empty()) {
            return;
        }

        std::string& pending = pendingIt->second;
        const std::string rawText = pending;
        std::string text = g_punct
            ? g_punct->AddPunctuation(pending)
            : meetingai::transcribe::NormalizeBilingualTranscript(
                pending);
        sendTranscript("streaming_final", source, rawText, text);
        std::cout << "[Worker] semantic final [" << source << "]: "
                  << text << std::endl;
        pending.clear();
    };

    try {
        // ==================== start_streaming ====================
        if (meetingai::proto::isStartStreaming(command)) {
            // 注意：这条路径统一用 std::cout（窄字符 UTF-8）。
            // std::wcout 遇到中文会置 failbit，之后该流的所有输出被静默丢弃，
            // 排查时等于完全失明。
            std::cout << "[Worker] recv start_streaming" << std::endl;

            if (g_postprocess_running.load()) {
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_error\","
                    "\"message\":\"上一场会议正在生成最终稿，请完成后再开始新会议\"}\n");
                return;
            }
            // 上一场会议完成后可能保留了手动“重新生成纪要”服务。
            // 新会议开始前先关闭，避免两个会议共享同一个摘要状态。
            if (g_meeting_summary.IsRunning()) {
                g_meeting_summary.Stop(false);
            }

            // 进度也通过管道回传一份，否则 Host 只能干等，看不出卡在哪一步
            auto notify = [hPipe](const std::string& text) {
                std::string msg = "{\"type\":\"info\",\"message\":\"" +
                    meetingai::proto::jsonEscape(text) + "\"}\n";
                WriteStreamingMessage(hPipe, msg);
            };

            const auto meetingContext =
                meetingai::proto::extractMeetingContext(command);
            const bool ragContextEnabled =
                meetingai::proto::extractRagContextEnabled(command);
            const bool asrHotwordsEnabled =
                meetingai::proto::extractAsrHotwordsEnabled(command);
            const std::string hotwordsBuffer =
                asrHotwordsEnabled
                    ? meetingai::proto::buildSherpaHotwordsBuffer(
                        meetingContext)
                    : std::string{};
            std::string whisperHotwords;
            for (const auto& hotword :
                 asrHotwordsEnabled
                    ? meetingContext.hotwords
                    : std::vector<
                        meetingai::proto::MeetingHotwordConfig>{}) {
                if (hotword.text.empty()) {
                    continue;
                }
                if (!whisperHotwords.empty()) {
                    whisperHotwords += ", ";
                }
                whisperHotwords += hotword.text;
                // Whisper 的 prompt 上下文不宜无限增长；Sherpa 仍保留完整
                // 的最多 100 条热词。
                if (whisperHotwords.size() >= 1000) {
                    break;
                }
            }
            const int requestedSampleRate =
                meetingai::proto::extractSampleRate(command);

            // 模型必须预先由 Startup 页面加载。Startup 使用支持动态热词
            // 的 modified_beam_search recognizer，因此是否启用术语增强只
            // 决定创建 stream 时是否注入热词，不会再偷偷切换或重载模型。
            {
                std::lock_guard<std::mutex> lock(g_sherpa_mutex);
                if (g_sherpa && g_sherpa->IsRunning()) {
                    WriteStreamingMessage(
                        hPipe,
                        "{\"type\":\"streaming_error\",\"message\":"
                        "\"已有流式会话正在运行，请先停止\"}\n");
                    return;
                }

                if (!g_sherpa_loaded || !g_sherpa) {
                    WriteStreamingMessage(
                        hPipe,
                        "{\"type\":\"streaming_error\","
                        "\"message\":\"Sherpa 实时转录模型未加载，请先到 Startup 手动加载\"}\n");
                    return;
                }
                if (requestedSampleRate != 16000) {
                    WriteStreamingMessage(
                        hPipe,
                        "{\"type\":\"streaming_error\","
                        "\"message\":\"当前 Sherpa 模型固定使用 16000Hz 音频\"}\n");
                    return;
                }

                if (!hotwordsBuffer.empty()) {
                    notify(
                        "[Sherpa] 本场会议已启用 " +
                        std::to_string(meetingContext.hotwords.size()) +
                        " 个上下文热词");
                }
                if (!g_punct) {
                    notify(
                        "[Sherpa] 标点模型未加载；实时转录继续运行，但不做标点恢复");
                }
            }

            std::string requestedSource = meetingai::proto::extractSource(command);
            if (requestedSource != "microphone" &&
                requestedSource != "system" &&
                requestedSource != "both") {
                requestedSource = "microphone";
            }

            const std::string translationMode =
                meetingai::proto::extractTranslationMode(command);
            if (translationMode != "off") {
                notify("[Translation] 正在检查 Startup 已加载的离线翻译模型…");
            }

            const std::string enZhModelDir =
                meetingai::util::resolveModelFileUtf8(
                    L"translation\\opus-mt-en-zh");
            const std::string zhEnModelDir =
                meetingai::util::resolveModelFileUtf8(
                    L"translation\\opus-mt-zh-en");

            const auto translationStartedAt =
                std::chrono::steady_clock::now();
            const bool translationReady = g_offline_translator.Start(
                translationMode,
                enZhModelDir,
                zhEnModelDir,
                [hPipe](const meetingai::translation::TranslationEvent& event) {
                    if (event.targetLanguage.rfind("error:", 0) == 0) {
                        const std::string errorText =
                            event.targetLanguage.substr(6);
                        const std::string message =
                            "{\"type\":\"streaming_translation_error\",\"source\":\"" +
                            meetingai::proto::jsonEscape(event.source) +
                            "\",\"utterance_id\":" +
                            std::to_string(event.utteranceId) +
                            ",\"message\":\"" +
                            meetingai::proto::jsonEscape(errorText) +
                            "\"}\n";
                        WriteStreamingMessage(hPipe, message);
                        return;
                    }

                    const char* type = event.isFinal
                        ? "streaming_translation_final"
                        : "streaming_translation_partial";

                    if (event.isFinal) {
                        std::int64_t segmentId = 0;
                        {
                            std::lock_guard<std::mutex> lock(
                                g_streaming_segment_mutex);
                            const auto sourceIt =
                                g_streaming_segment_ids.find(event.source);
                            if (sourceIt != g_streaming_segment_ids.end()) {
                                const auto utteranceIt =
                                    sourceIt->second.find(event.utteranceId);
                                if (utteranceIt != sourceIt->second.end()) {
                                    segmentId = utteranceIt->second;
                                }
                            }
                        }
                        if (segmentId > 0 &&
                            !InsertStreamingTranslation(
                                segmentId,
                                event.targetLanguage,
                                event.text)) {
                            WriteStreamingMessage(
                                hPipe,
                                "{\"type\":\"streaming_persistence_error\","
                                "\"message\":\"最终译文写入数据库失败\"}\n");
                        }
                    }

                    const std::string message =
                        "{\"type\":\"" + std::string(type) +
                        "\",\"source\":\"" +
                        meetingai::proto::jsonEscape(event.source) +
                        "\",\"utterance_id\":" +
                        std::to_string(event.utteranceId) +
                        ",\"target_language\":\"" +
                        meetingai::proto::jsonEscape(event.targetLanguage) +
                        "\",\"text\":\"" +
                        meetingai::proto::jsonEscape(event.text) +
                        "\"}\n";
                    WriteStreamingMessage(hPipe, message);
                });

            std::string activeTranslationMode = translationMode;
            if (!translationReady) {
                activeTranslationMode = "off";
                const std::string err =
                    "{\"type\":\"streaming_translation_error\",\"message\":\"离线翻译未启用: " +
                    meetingai::proto::jsonEscape(
                        g_offline_translator.GetLastError()) +
                    "\"}\n";
                WriteStreamingMessage(hPipe, err);
            }

            if (activeTranslationMode != "off") {
                const auto translationMs =
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now()
                        - translationStartedAt).count();
                notify(
                    "[Translation] 离线翻译模型已就绪，耗时 "
                    + std::to_string(translationMs)
                    + " ms");
            }

            std::vector<std::string> requestedSources =
                requestedSource == "both"
                ? std::vector<std::string>{ "microphone", "system" }
                : std::vector<std::string>{ requestedSource };

            std::vector<std::string> startedSources;
            for (const std::string& source : requestedSources) {
                if (!g_sherpa->StartSession(source, hotwordsBuffer)) {
                    for (const std::string& started : startedSources) {
                        std::vector<meetingai::transcribe::SherpaStreamResult> ignored;
                        g_sherpa->EndSession(started, ignored);
                    }
                    std::string err =
                        "{\"type\":\"streaming_error\",\"message\":\"" +
                        meetingai::proto::jsonEscape(g_sherpa->GetLastError()) +
                        "\"}\n";
                    g_offline_translator.Stop(false);
                    WriteStreamingMessage(hPipe, err);
                    return;
                }
                startedSources.push_back(source);
            }

            g_streaming_active_sources = std::move(startedSources);
            g_streaming_pending_raw.clear();
            g_streaming_utterance_ids.clear();
            ResetStreamingPersistenceState();
            for (const std::string& source : g_streaming_active_sources) {
                g_streaming_pending_raw.emplace(source, std::string{});
                g_streaming_utterance_ids.emplace(source, 1);
                g_streaming_last_end_ms.emplace(source, 0);
            }

            const int streamingSampleRate =
                meetingai::proto::extractSampleRate(command);
            const bool ragEnabled =
                ragContextEnabled &&
                meetingContext.HasPreparation() &&
                !meetingContext.documentIds.empty();
            const std::string contextSnapshotJson =
                meetingContext.HasPreparation()
                ? meetingai::proto::buildMeetingContextSnapshotJson(
                    meetingContext)
                : std::string{};
            g_streaming_meeting_id = BeginStreamingMeeting(
                g_streaming_active_sources,
                streamingSampleRate,
                meetingContext.title,
                meetingContext.preparationId,
                contextSnapshotJson,
                asrHotwordsEnabled
                    ? static_cast<int>(meetingContext.hotwords.size())
                    : 0,
                ragEnabled);
            if (g_streaming_meeting_id <= 0) {
                for (const std::string& source :
                     g_streaming_active_sources) {
                    std::vector<
                        meetingai::transcribe::SherpaStreamResult> ignored;
                    g_sherpa->EndSession(source, ignored);
                }
                g_offline_translator.Stop(false);
                g_streaming_active_sources.clear();
                g_streaming_pending_raw.clear();
                g_streaming_utterance_ids.clear();
                ResetStreamingPersistenceState();
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_error\","
                    "\"message\":\"创建会议数据库记录失败\"}\n");
                return;
            }
            g_streaming_started_at = std::chrono::steady_clock::now();

            const bool summaryEnabled =
                meetingai::proto::extractSummaryEnabled(command);
            g_streaming_translation_mode = activeTranslationMode;
            g_streaming_whisper_hotwords = std::move(whisperHotwords);
            g_streaming_summary_enabled = summaryEnabled;
            g_streaming_recording_failed = false;

            std::string recordingError;
            if (!g_meeting_audio_recorder.Start(
                    g_streaming_meeting_id,
                    g_streaming_active_sources,
                    streamingSampleRate,
                    recordingError)) {
                for (const std::string& source :
                     g_streaming_active_sources) {
                    std::vector<
                        meetingai::transcribe::SherpaStreamResult> ignored;
                    g_sherpa->EndSession(source, ignored);
                }
                g_offline_translator.Stop(false);
                CloseStreamingMeetingRecord();
                g_streaming_active_sources.clear();
                g_streaming_pending_raw.clear();
                g_streaming_utterance_ids.clear();
                ResetStreamingPersistenceState();
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_error\",\"message\":\"" +
                    meetingai::proto::jsonEscape(recordingError) +
                    "\"}\n");
                return;
            }
            for (const auto& [source, mediaPath] :
                 g_meeting_audio_recorder.Paths()) {
                if (!UpdateStreamingMediaPath(
                        g_streaming_meeting_id,
                        source,
                        mediaPath)) {
                    WriteStreamingMessage(
                        hPipe,
                        "{\"type\":\"streaming_persistence_error\","
                        "\"message\":\"会议录音路径写入数据库失败\"}\n");
                }
            }

            // 发送成功响应
            std::string ok = "{\"type\":\"streaming_started\",\"source\":\"" +
                meetingai::proto::jsonEscape(requestedSource) +
                "\",\"translation_mode\":\"" +
                meetingai::proto::jsonEscape(activeTranslationMode) +
                "\",\"meeting_id\":" +
                std::to_string(g_streaming_meeting_id) +
                ",\"preparation_id\":" +
                std::to_string(meetingContext.preparationId) +
                ",\"context_title\":\"" +
                meetingai::proto::jsonEscape(meetingContext.title) +
                "\",\"hotword_count\":" +
                std::to_string(
                    asrHotwordsEnabled
                        ? meetingContext.hotwords.size()
                        : 0) +
                ",\"context_document_count\":" +
                std::to_string(meetingContext.documentIds.size()) +
                ",\"rag_enabled\":" +
                (ragEnabled ? "true" : "false") +
                ",\"asr_hotwords_enabled\":" +
                (asrHotwordsEnabled ? "true" : "false") +
                ",\"summary_enabled\":" +
                (summaryEnabled ? "true" : "false") +
                ",\"postprocess_enabled\":true" +
                "}\n";
            WriteStreamingMessage(hPipe, ok);

            if (summaryEnabled) {
                StartMeetingSummaryService(
                    hPipe,
                    g_streaming_meeting_id);
            }
            else {
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_summary_status\","
                    "\"state\":\"disabled\","
                    "\"message\":\"本地会议摘要已关闭\"}\n");
            }
            std::cout << "[Worker] streaming session started: "
                      << requestedSource << std::endl;
        }

        // ==================== streaming_audio ====================
        else if (meetingai::proto::isStreamingAudio(command)) {
            std::string source = meetingai::proto::extractSource(command);
            if (source != "microphone" && source != "system") {
                if (g_streaming_active_sources.size() == 1) {
                    source = g_streaming_active_sources.front();
                }
                else {
                    source = "microphone";
                }
            }

            if (!g_sherpa || !g_sherpa->IsRunning(source)) {
                std::string err =
                    "{\"type\":\"streaming_error\",\"source\":\"" +
                    meetingai::proto::jsonEscape(source) +
                    "\",\"message\":\"该音频来源的流式会话未启动\"}\n";
                WriteStreamingMessage(hPipe, err);
                return;
            }

            // 提取 Base64 音频数据
            std::string audioData = meetingai::proto::extractAudioData(command);
            if (audioData.empty()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"音频数据为空\"}\n";
                WriteStreamingMessage(hPipe, err);
                return;
            }

            // Base64 解码为 float32 样本
            std::vector<float> samples = meetingai::util::Base64DecodeToFloat(audioData);
            if (samples.empty()) {
                // 把实际收到的内容报出来，否则"解码失败"没有任何可查线索
                std::string head = audioData.substr(0, 48);
                std::cout << "[Worker] base64 decode failed."
                          << " cmd_len=" << command.size()
                          << " b64_len=" << audioData.size()
                          << " head=[" << head << "]" << std::endl;

                std::string err = "{\"type\":\"streaming_error\",\"message\":\"音频解码失败 b64_len=" +
                    std::to_string(audioData.size()) + " head=" +
                    meetingai::proto::jsonEscape(head) + "\"}\n";
                WriteStreamingMessage(hPipe, err);
                return;
            }

            // 发送音频到转录器
            if (!g_streaming_recording_failed &&
                !g_meeting_audio_recorder.Append(
                    source,
                    samples.data(),
                    static_cast<int>(samples.size()))) {
                g_streaming_recording_failed = true;
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_persistence_error\","
                    "\"message\":\"会议录音写入失败；实时转录仍会继续\"}\n");
            }

            std::vector<meetingai::transcribe::SherpaStreamResult> results;
            if (!g_sherpa->AcceptWaveform(
                source,
                samples.data(),
                (int)samples.size(),
                results)) {
                std::string err = "{\"type\":\"streaming_error\",\"source\":\"" +
                    meetingai::proto::jsonEscape(source) + "\",\"message\":\"" +
                    meetingai::proto::jsonEscape(g_sherpa->GetLastError()) + "\"}\n";
                WriteStreamingMessage(hPipe, err);
                return;
            }

            // Partial 始终实时显示，但只做轻量规则大小写，不在 100ms
            // 热路径里调用标点模型。Sherpa 的 final 先视为“声学稳定片段”，
            // 缓存并等待下一片段提供语义前瞻后才提交到 UI。
            for (const auto& result : results) {
                if (!result.is_final) {
                    const std::string combined =
                        meetingai::transcribe::JoinTranscriptFragments(
                            g_streaming_pending_raw[source],
                            result.text);
                    sendTranscript(
                        "streaming_partial",
                        source,
                        combined,
                        meetingai::transcribe::NormalizeBilingualTranscript(combined));
                    continue;
                }

                if (!result.text.empty()) {
                    g_streaming_pending_raw[source] =
                        meetingai::transcribe::JoinTranscriptFragments(
                            g_streaming_pending_raw[source],
                            result.text);

                    // 没有标点模型时无法可靠判断语义边界，保持旧的声学
                    // endpoint 行为，但仍进行大小写归一化。
                    if (!g_punct) {
                        flushPendingTranscript(source);
                        continue;
                    }

                    const std::string punctuated =
                        g_punct->AddPunctuation(g_streaming_pending_raw[source]);
                    meetingai::transcribe::StableTranscriptPrefix stable;
                    if (meetingai::transcribe::TryExtractStableTranscriptPrefix(
                        g_streaming_pending_raw[source],
                        punctuated,
                        stable)) {
                        sendTranscript(
                            "streaming_final",
                            source,
                            stable.finalizedRawText,
                            stable.finalizedText);
                        std::cout << "[Worker] semantic final [" << source << "]: "
                                  << stable.finalizedText << std::endl;
                        g_streaming_pending_raw[source] =
                            std::move(stable.remainingRawText);

                        // final 会替换当前 partial；若还有尚未定稿的后半句，
                        // 立即作为下一条 partial 显示，画面不会丢字。
                        if (!g_streaming_pending_raw[source].empty()) {
                            sendTranscript(
                                "streaming_partial",
                                source,
                                g_streaming_pending_raw[source],
                                meetingai::transcribe::NormalizeBilingualTranscript(
                                    g_streaming_pending_raw[source]));
                        }
                    }
                    continue;
                }

                // reset 后再次触发空 endpoint，表示已经持续静音约
                // rule1 的时长；此时没有更多前瞻，强制提交剩余文本。
                if (result.endpoint_detected) {
                    flushPendingTranscript(source);
                }
            }
        }

        // ==================== stop_streaming ====================
        else if (meetingai::proto::isStopStreaming(command)) {
            std::wcout << L"[Worker] 收到 stop_streaming 命令\n";

            if (!g_sherpa || !g_sherpa->IsRunning()) {
                g_offline_translator.Stop(false);
                g_meeting_summary.Stop(false);
                g_meeting_audio_recorder.Stop();
                CloseStreamingMeetingRecord();
                ResetStreamingPersistenceState();
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"流式会话未启动\"}\n";
                WriteStreamingMessage(hPipe, err);
                return;
            }

            const std::int64_t completedMeetingId =
                g_streaming_meeting_id;
            const std::string completedTranslationMode =
                g_streaming_translation_mode;
            const std::string completedWhisperHotwords =
                g_streaming_whisper_hotwords;
            const bool completedSummaryEnabled =
                g_streaming_summary_enabled;
            std::string stopError;

            // Host 已经停止采集和发送音频，此时先立即封口 WAV。
            // 实时译文排空和 Granite 当前摘要线程退出都可能耗时几十秒，
            // 不能让这些工作阻塞“录音已安全保存”的确认。
            WriteStreamingMessage(
                hPipe,
                "{\"type\":\"streaming_stop_progress\","
                "\"message\":\"正在封存会议录音…\"}\n");
            const auto audioPaths = g_meeting_audio_recorder.Stop();

            for (const std::string& source : g_streaming_active_sources) {
                if (!g_sherpa->IsRunning(source)) {
                    continue;
                }

                std::vector<meetingai::transcribe::SherpaStreamResult> finalResults;
                if (!g_sherpa->EndSession(source, finalResults)) {
                    stopError = g_sherpa->GetLastError();
                    continue;
                }

                // 把停止前该来源当前 stream 中的文字并入自己的语义缓冲。
                for (const auto& result : finalResults) {
                    if (!result.text.empty()) {
                        g_streaming_pending_raw[source] =
                            meetingai::transcribe::JoinTranscriptFragments(
                                g_streaming_pending_raw[source],
                                result.text);
                    }
                }
                flushPendingTranscript(source);
            }

            // EndSession 产生的最后一条字幕已经先于此消息写入管道，
            // Host 收到回执后可以安全关闭实时 partial 并进入处理界面。
            WriteStreamingMessage(
                hPipe,
                "{\"type\":\"streaming_recording_stopped\","
                "\"meeting_id\":" +
                std::to_string(completedMeetingId) +
                ",\"postprocess_started\":false,"
                "\"postprocess_pending\":true}\n");

            // 先把实时稿最后一条译文排空。实时滚动摘要在这里结束，但不再
            // 基于 Sherpa 稿生成“最终摘要”；真正的最终纪要会在 Whisper
            // 精修稿发布以后生成。
            WriteStreamingMessage(
                hPipe,
                "{\"type\":\"streaming_stop_progress\","
                "\"message\":\"正在完成实时译文和字幕…\"}\n");
            g_offline_translator.Stop(true);
            g_meeting_summary.Stop(false);

            WriteStreamingMessage(
                hPipe,
                "{\"type\":\"streaming_stop_progress\","
                "\"message\":\"正在保存会议记录…\"}\n");
            CloseStreamingMeetingRecord();

            g_streaming_active_sources.clear();
            g_streaming_pending_raw.clear();
            g_streaming_utterance_ids.clear();
            g_streaming_translation_mode = "off";
            g_streaming_whisper_hotwords.clear();
            g_streaming_summary_enabled = false;
            g_streaming_recording_failed = false;
            ResetStreamingPersistenceState();

            if (!stopError.empty()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"" +
                    meetingai::proto::jsonEscape(stopError) + "\"}\n";
                WriteStreamingMessage(hPipe, err);
            }

            const bool postProcessStarted = StartMeetingPostProcess(
                hPipe,
                completedMeetingId,
                audioPaths,
                completedTranslationMode,
                completedWhisperHotwords,
                completedSummaryEnabled);
            if (!postProcessStarted) {
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_postprocess_error\","
                    "\"meeting_id\":" +
                    std::to_string(completedMeetingId) +
                    ",\"message\":\"无法启动会后精修任务\"}\n");
                WriteStreamingMessage(
                    hPipe,
                    "{\"type\":\"streaming_stopped\"}\n");
            }
            std::wcout << L"[Worker] 录音已停止，会后精修已在后台启动\n";
        }
    }
    catch (const std::exception& e) {
        g_offline_translator.Stop(false);
        g_meeting_summary.Stop(false);
        g_meeting_audio_recorder.Stop();
        CloseStreamingMeetingRecord();
        ResetStreamingPersistenceState();
        std::string err = std::string("{\"type\":\"streaming_error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        WriteStreamingMessage(hPipe, err);
    }
}

// 处理控制台关闭/注销/关机等信号，优雅退出
static BOOL WINAPI ConsoleCtrlHandler(DWORD dwCtrlType) {
    switch (dwCtrlType) {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
    case CTRL_LOGOFF_EVENT:
    case CTRL_SHUTDOWN_EVENT:
        g_shutdownRequested = TRUE;
        return TRUE; // 我们处理了
    }
    return FALSE;
}

static std::string ExtractJsonStringField(
    const std::string& json,
    const std::string& key,
    const std::string& fallback = {}) {
    const std::string marker = "\"" + key + "\":\"";
    const auto markerPosition = json.find(marker);
    if (markerPosition == std::string::npos) {
        return fallback;
    }
    const auto valueStart = markerPosition + marker.size();
    const auto valueEnd = json.find('"', valueStart);
    return valueEnd == std::string::npos
        ? fallback
        : json.substr(valueStart, valueEnd - valueStart);
}

static void SendModelState(
    HANDLE hPipe,
    const std::string& model,
    bool loaded,
    const std::string& device = {},
    const std::string& message = {}) {
    WriteStreamingMessage(
        hPipe,
        "{\"type\":\"model_state\",\"model\":\"" +
        meetingai::proto::jsonEscape(model) +
        "\",\"loaded\":" + (loaded ? "true" : "false") +
        ",\"device\":\"" +
        meetingai::proto::jsonEscape(device) +
        "\",\"message\":\"" +
        meetingai::proto::jsonEscape(message) + "\"}\n");
}

static void SendModelStatusSnapshot(HANDLE hPipe) {
    const bool translationEnZh =
        g_offline_translator.IsDirectionLoaded("en_zh");
    const bool translationZhEn =
        g_offline_translator.IsDirectionLoaded("zh_en");
    WriteStreamingMessage(
        hPipe,
        "{\"type\":\"model_status\","
        "\"granite\":" + std::string(g_granite_loaded ? "true" : "false") +
        ",\"embedding\":" +
        std::string(g_embedding_loaded ? "true" : "false") +
        ",\"openvino_whisper\":" +
        std::string(
            meetingai::transcribe::IsWhisperOpenVINOModelLoaded()
                ? "true" : "false") +
        ",\"sherpa\":" +
        std::string(g_sherpa_loaded ? "true" : "false") +
        ",\"punctuator\":" +
        std::string(g_punct ? "true" : "false") +
        ",\"translation_en_zh\":" +
        std::string(translationEnZh ? "true" : "false") +
        ",\"translation_zh_en\":" +
        std::string(translationZhEn ? "true" : "false") +
        ",\"llava\":" +
        std::string(g_llava_loaded ? "true" : "false") +
        ",\"stable_diffusion\":" +
        std::string(g_sd_loaded ? "true" : "false") +
        "}\n");
}

static bool HandleStartupModelCommand(
    HANDLE hPipe,
    const std::string& command) {
    const std::string type =
        ExtractJsonStringField(command, "type");
    if (type == "get_model_status") {
        SendModelStatusSnapshot(hPipe);
        return true;
    }

    if (type == "load_granite") {
        const std::string device =
            ExtractJsonStringField(command, "device", "CPU");
        bool loaded = false;
        {
            std::lock_guard<std::mutex> lock(g_granite_mutex);
            if (!g_granite_loaded || !g_granite) {
                InitializeGraniteGenAI(hPipe, device);
                g_granite_loaded = g_granite != nullptr;
            }
            loaded = g_granite_loaded && g_granite != nullptr;
            if (loaded) {
                g_granite_device = device;
            }
        }
        SendModelState(
            hPipe,
            "granite",
            loaded,
            loaded ? device : std::string{},
            loaded ? "Granite 已就绪" : "Granite 加载失败");
        return true;
    }

    if (type == "unload_granite") {
        if (g_meeting_summary.IsRunning() ||
            g_meeting_summary.IsGenerating()) {
            SendModelState(
                hPipe,
                "granite",
                true,
                g_granite_device,
                "Granite 正在生成会议摘要，暂时不能卸载");
            return true;
        }
        {
            std::lock_guard<std::mutex> lock(g_granite_mutex);
            g_granite.reset();
            g_granite_loaded = false;
        }
        SendModelState(hPipe, "granite", false, {}, "Granite 已卸载");
        return true;
    }

    if (type == "load_embedding") {
        const std::string device =
            ExtractJsonStringField(command, "device", "CPU");
        bool loaded = false;
        {
            std::lock_guard<std::mutex> lock(g_embedding_mutex);
            if (!g_embedding_loaded || !g_embedding) {
                InitializeEmbeddingGenAI(hPipe, device);
                g_embedding_loaded = g_embedding != nullptr;
            }
            loaded = g_embedding_loaded && g_embedding != nullptr;
            if (loaded) {
                g_embedding_device = device;
            }
        }
        SendModelState(
            hPipe,
            "embedding",
            loaded,
            loaded ? device : std::string{},
            loaded
                ? "OpenVINO GenAI TextEmbeddingPipeline 已就绪"
                : "Embedding 加载失败");
        return true;
    }

    if (type == "unload_embedding") {
        {
            std::lock_guard<std::mutex> lock(g_embedding_mutex);
            g_embedding.reset();
            g_embedding_loaded = false;
        }
        SendModelState(
            hPipe,
            "embedding",
            false,
            {},
            "Embedding 已卸载");
        return true;
    }

    if (type == "load_sherpa") {
        bool loaded = false;
        std::string error;
        {
            std::lock_guard<std::mutex> lock(g_sherpa_mutex);
            if (g_sherpa && g_sherpa->IsRunning()) {
                error = "Sherpa 正在转录，不能重新加载";
            }
            else if (g_sherpa_loaded && g_sherpa) {
                loaded = true;
            }
            else {
                const std::string modelDir =
                    meetingai::util::resolveModelFileUtf8(
                        L"sherpa\\sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20");
                const std::string tokensPath =
                    modelDir + "\\tokens.txt";
                const std::string bpeVocabPath =
                    modelDir + "\\bpe.vocab";
                auto transcriber =
                    std::make_unique<
                        meetingai::transcribe::SherpaStreamingTranscriber>();
                loaded = transcriber->Initialize(
                    modelDir,
                    tokensPath,
                    16000,
                    {},
                    bpeVocabPath,
                    true);
                if (loaded) {
                    g_sherpa = std::move(transcriber);
                    g_sherpa_loaded = true;
                    g_sherpa_recognizer_signature =
                        "16000:hotwords-capable";
                }
                else {
                    error = transcriber->GetLastError();
                    g_sherpa.reset();
                    g_sherpa_loaded = false;
                    g_sherpa_recognizer_signature.clear();
                }
            }
        }
        SendModelState(
            hPipe,
            "sherpa",
            loaded,
            loaded ? "CPU" : std::string{},
            loaded ? "Sherpa 实时转录已就绪" : error);
        return true;
    }

    if (type == "unload_sherpa") {
        bool unloaded = false;
        {
            std::lock_guard<std::mutex> lock(g_sherpa_mutex);
            if (!g_sherpa || !g_sherpa->IsRunning()) {
                if (g_sherpa) {
                    g_sherpa->Stop();
                }
                g_sherpa.reset();
                g_sherpa_loaded = false;
                g_sherpa_recognizer_signature.clear();
                unloaded = true;
            }
        }
        SendModelState(
            hPipe,
            "sherpa",
            !unloaded,
            unloaded ? std::string{} : "CPU",
            unloaded
                ? "Sherpa 已卸载"
                : "Sherpa 正在转录，暂时不能卸载");
        return true;
    }

    if (type == "load_punctuator") {
        bool loaded = false;
        std::string error;
        {
            std::lock_guard<std::mutex> lock(g_sherpa_mutex);
            if (g_punct) {
                loaded = true;
            }
            else {
                const std::string modelDir =
                    meetingai::util::resolveModelFileUtf8(
                        L"sherpa\\sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8");
                auto punctuator =
                    std::make_unique<
                        meetingai::transcribe::Punctuator>();
                loaded = punctuator->Initialize(modelDir);
                if (loaded) {
                    g_punct = std::move(punctuator);
                }
                else {
                    error = punctuator->GetLastError();
                }
            }
        }
        SendModelState(
            hPipe,
            "punctuator",
            loaded,
            loaded ? "CPU" : std::string{},
            loaded ? "中英标点模型已就绪" : error);
        return true;
    }

    if (type == "unload_punctuator") {
        {
            std::lock_guard<std::mutex> lock(g_sherpa_mutex);
            g_punct.reset();
            g_punct_attempted = false;
        }
        SendModelState(
            hPipe,
            "punctuator",
            false,
            {},
            "中英标点模型已卸载");
        return true;
    }

    if (type == "load_translation" ||
        type == "unload_translation") {
        const std::string direction =
            ExtractJsonStringField(command, "direction");
        const bool load = type == "load_translation";
        bool success = false;
        if (load) {
            const std::wstring relativePath =
                direction == "en_zh"
                    ? L"translation\\opus-mt-en-zh"
                    : L"translation\\opus-mt-zh-en";
            success = g_offline_translator.LoadDirection(
                direction,
                meetingai::util::resolveModelFileUtf8(
                    relativePath.c_str()));
        }
        else {
            success =
                g_offline_translator.UnloadDirection(direction);
        }
        const bool loaded =
            g_offline_translator.IsDirectionLoaded(direction);
        SendModelState(
            hPipe,
            direction == "en_zh"
                ? "translation_en_zh"
                : "translation_zh_en",
            loaded,
            loaded ? "CPU" : std::string{},
            success
                ? (loaded ? "翻译模型已就绪" : "翻译模型已卸载")
                : g_offline_translator.GetLastError());
        return true;
    }

    if (type == "unload_sd") {
        {
            std::lock_guard<std::mutex> lock(g_sd_mutex);
            g_sd.reset();
            g_sd_loaded = false;
        }
        SendModelState(
            hPipe,
            "stable_diffusion",
            false,
            {},
            "Stable Diffusion 已卸载");
        return true;
    }

    return false;
}



int wmain() {
    // ★ 设置控制台 UTF-8 编码（修复中文显示问题）
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);

    // ★ 新增 1: 初始化数据库
    if (!InitDatabaseOnce()) {
        std::wcerr << L"[Worker] 数据库初始化失败！\n";
        return 1;
    }

    // 打印实际数据库位置。启动过程不再写入伪造的 worker started 字幕。
    {
        std::string dbPath = meetingai::util::getDatabasePath();
        std::cout << "[DB] path = " << dbPath << "\n";

        bool exists = std::filesystem::exists(dbPath);
        std::cout << "[DB] exists = " << (exists ? "true" : "false") << "\n";
    }

    // 解析命令行参数：--ppid, --granite-device, --embedding-device
    DWORD parentPid = 0;
    for (int i = 1; i < __argc; i++) {
        if (std::wstring(__wargv[i]) == L"--ppid" && i + 1 < __argc) {
            parentPid = std::wcstoul(__wargv[++i], nullptr, 10);
        }
        else if (std::wstring(__wargv[i]) == L"--granite-device" && i + 1 < __argc) {
            std::wstring wdevice = __wargv[++i];
            g_granite_device = std::string(wdevice.begin(), wdevice.end());
            std::wcout << L"[Worker] Granite 设备: " << wdevice.c_str() << L"\n";
        }
        else if (std::wstring(__wargv[i]) == L"--embedding-device" && i + 1 < __argc) {
            std::wstring wdevice = __wargv[++i];
            g_embedding_device = std::string(wdevice.begin(), wdevice.end());
            std::wcout << L"[Worker] Embedding 设备: " << wdevice.c_str() << L"\n";
        }
    }

    HANDLE hParent = nullptr;
    if (parentPid) {
        hParent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
    }

    const wchar_t* pipeName = L"\\\\.\\pipe\\MeetingAI_Pipe";

    // ★ 注册控制台控制事件
    SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);
    // --- 单实例互斥量（当前会话） ---
    HANDLE hMutex = CreateMutexW(nullptr, TRUE, L"Local\\MeetingAI_Worker_Singleton");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        // 退出码 2 让 Host 能把这种情况和正常退出区分开（见 MainWindow.Pipe.cs）
        std::wcerr << L"[Worker] another instance is already running, exiting.\n";
        std::wcerr.flush();
        return 2;
    }

    // 父进程看门狗。
    // 主循环顶部那次 hParent 检查只在两次连接之间才轮得到，而 ConnectNamedPipe
    // 是无超时的阻塞调用，客户端进程死掉并不会让它返回。Host 退出时 Worker 往往
    // 正卡在那一行，于是变成占着互斥量和管道的僵尸进程。这里单开一个线程专门等
    // 父进程句柄，不受主循环阻塞影响。
    if (hParent) {
        std::thread([hParent] {
            ::WaitForSingleObject(hParent, INFINITE);
            std::cerr << "[Worker] host process exited, terminating.\n";
            std::cerr.flush();
            ::ExitProcess(3);
        }).detach();
    }
    else if (parentPid) {
        std::cerr << "[Worker] warning: OpenProcess(" << parentPid
                  << ") failed, parent watchdog disabled.\n";
    }

    std::wcout << L"[Worker PID " << GetCurrentProcessId() << L"] starting pipe server...\n";

    SECURITY_ATTRIBUTES sa{};
    PSECURITY_DESCRIPTOR pSD = nullptr;
    if (!meetingai::ipc::createPipeSecurity(sa, pSD)) return 1;

    bool shutdownRequested = false;

    while (!shutdownRequested && !g_shutdownRequested) {
        // 如果 Host 已退出，直接标记结束
        if (hParent && WaitForSingleObject(hParent, 0) == WAIT_OBJECT_0) {
            std::wcout << L"[Worker] Host exited, shutting down\n";
            shutdownRequested = true;
            break; // 直接跳出循环
        }

        // 1) 创建管道（单实例；客户端断开后再循环创建）
        HANDLE hPipe = CreateNamedPipeW(
            pipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,              // 单实例即可；需要并发再开多实例
            // 流式音频每包约 4.3KB（100ms PCM 经 base64 膨胀），原来的 4096 比单个包还小，
            // 每次写入都要等对端边读边腾地方。放大到 256KB 让收发都不必来回阻塞。
            256 * 1024, 256 * 1024, 0,
            &sa
        );
        if (hPipe == INVALID_HANDLE_VALUE) {
            meetingai::util::logLastError(L"[IPC] CreateNamedPipe failed");
            break;
        }

        std::wcout << L"[Worker] pipe created, waiting for client...\n";

        // 2) 等待客户端
        BOOL connected = ConnectNamedPipe(hPipe, nullptr) ? TRUE :
            (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected) {
            meetingai::util::logLastError(L"[Worker] ConnectNamedPipe failed");
            CloseHandle(hPipe);
            continue; // 重新创建实例
        }

        std::wcout << L"[Worker] client connected\n";

        // 3) 连接存活期间，循环读取“按行”的多条消息
        std::string buffer;
        DWORD read = 0;
        char ch = 0;
        DWORD pendingBytes = 0;
        bool clientGone = false;

        while (true) {
            // 同步(非 OVERLAPPED)管道句柄上，挂起的阻塞 ReadFile 会一直占住
            // 文件对象的内核锁：其他线程(翻译/摘要/会后精修)对同一句柄的
            // WriteFile 全部排在这次读后面等待。会议期间 Host 的音频包源源
            // 不断、读很快完成，看不出问题；停止录音后 Host 不再发任何命令，
            // 这里的 ReadFile 永远挂起，会后线程的第一条进度消息就被永久
            // 堵死——这就是最终稿一直卡在 0%/2% 的根因(CancelSynchronousIo
            // 也救不了它：写请求还排在文件对象锁上，尚未成为可取消的 I/O)。
            // 因此改为 PeekNamedPipe 确认有数据后才调 ReadFile，让锁只在
            // 真正搬运数据的瞬间被持有。
            if (pendingBytes == 0) {
                DWORD available = 0;
                while (true) {
                    if (!PeekNamedPipe(
                            hPipe, nullptr, 0, nullptr, &available, nullptr)) {
                        DWORD err = GetLastError();
                        if (err == ERROR_BROKEN_PIPE) {
                            std::wcout << L"[Worker] client disconnected\n";
                        }
                        else {
                            meetingai::util::logLastError(
                                L"[Worker] PeekNamedPipe failed");
                        }
                        clientGone = true;
                        break;
                    }
                    if (available != 0 || g_shutdownRequested) {
                        break;
                    }
                    Sleep(1);
                }
                if (clientGone) {
                    break; // 退出连接循环，去清理并等待下一个客户端
                }
                if (available == 0 && g_shutdownRequested) {
                    std::wcout << L"[Worker] global shutdown requested\n";
                    break;
                }
                pendingBytes = available;
            }

            if (!ReadFile(hPipe, &ch, 1, &read, nullptr)) {
                DWORD err = GetLastError();
                if (err == ERROR_BROKEN_PIPE) {
                    std::wcout << L"[Worker] client disconnected\n";
                }
                else {
                    meetingai::util::logLastError(L"[Worker] ReadFile failed");
                }
                break; // 退出连接循环，去清理并等待下一个客户端
            }
            if (read == 0) {
                // 对端优雅关闭
                std::wcout << L"[Worker] client closed\n";
                break;
            }
            pendingBytes -= read;

            // ★ 新增：全局退出检查
            if (g_shutdownRequested) {
                std::wcout << L"[Worker] global shutdown requested\n";
                break; // 跳出连接循环
            }

            if (ch == '\n') {
                // 收到一整行，处理并回复
                std::wcout << L"[Worker] received: " << buffer.c_str() << L"\n";

                // ---- 退出命令（容忍空白/额外字段）----
                if (meetingai::proto::isQuit(buffer)) {
                    std::wcout << L"[Worker] quit requested\n";
                    shutdownRequested = true; // 进程级退出
                    // 回个确认（可选）
                    std::string bye = "{\"type\":\"bye\"}\n";
                    DWORD w = 0; WriteFile(hPipe, bye.data(), (DWORD)bye.size(), &w, nullptr);
                    break;
                }

                // ---- Startup 统一模型生命周期 ----
                if (HandleStartupModelCommand(hPipe, buffer)) {
                    buffer.clear();
                    continue;
                }

                // ---- 新增：预加载模型命令 ----
                if (buffer.find("\"preload_models\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到预加载模型命令\n";

                    DWORD written;

                    // 发送调试消息
                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 收到 preload_models 命令\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 解析设备选择
                    std::string graniteDeviceCmd = g_granite_device;
                    std::string embeddingDeviceCmd = g_embedding_device;
                    std::string llavaDeviceCmd = g_llava_device;

                    auto granitePos = buffer.find("\"granite_device\":\"");
                    if (granitePos != std::string::npos) {
                        auto start = granitePos + 18;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            graniteDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    auto embeddingPos = buffer.find("\"embedding_device\":\"");
                    if (embeddingPos != std::string::npos) {
                        auto start = embeddingPos + 20;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            embeddingDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    auto llavaPos = buffer.find("\"llava_device\":\"");
                    if (llavaPos != std::string::npos) {
                        auto start = llavaPos + 16;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            llavaDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 设备参数: Granite=" + graniteDeviceCmd + ", Embedding=" + embeddingDeviceCmd + ", LLaVA=" + llavaDeviceCmd + "\"}\n";
                    WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

                    // 发送确认消息
                    std::string ack = "{\"type\":\"preload_started\"}\n";
                    WriteFile(hPipe, ack.data(), (DWORD)ack.size(), &written, nullptr);

                    // 直接在主线程加载模型（支持热拔插）
                    std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载Granite...\"}\n";
                    WriteFile(hPipe, debug3.data(), (DWORD)debug3.size(), &written, nullptr);

                    {
                        std::lock_guard<std::mutex> lock(g_granite_mutex);
                        if (!g_granite_loaded) {
                            InitializeGraniteGenAI(hPipe, graniteDeviceCmd);
                            g_granite_loaded = true;
                        }
                    }

                    std::string debug4 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载Embedding...\"}\n";
                    WriteFile(hPipe, debug4.data(), (DWORD)debug4.size(), &written, nullptr);

                    {
                        std::lock_guard<std::mutex> lock(g_embedding_mutex);
                        if (!g_embedding_loaded) {
                            InitializeEmbeddingGenAI(hPipe, embeddingDeviceCmd);
                            g_embedding_loaded = true;
                        }
                    }

                    // ---- 临时注释：测试 LLaVA 加载问题 ----
                    // std::string debug4_5 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载LLaVA...\"}\n";
                    // WriteFile(hPipe, debug4_5.data(), (DWORD)debug4_5.size(), &written, nullptr);
                    //
                    // std::call_once(g_llava_once, [hPipe, llavaDeviceCmd]() {
                    //     InitializeLLaVAGenAI(hPipe, llavaDeviceCmd);
                    // });

                    std::string debug5 = "{\"type\":\"info\",\"message\":\"[Worker Debug]预加载完成\"}\n";
                    WriteFile(hPipe, debug5.data(), (DWORD)debug5.size(), &written, nullptr);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 加载命令 ----
                if (buffer.find("\"load_whisper_openvino\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 load_whisper_openvino 命令\n";

                    // 解析模型路径
                    std::string modelPath = meetingai::util::resolveModelFileUtf8(L"whisper_large_v3");
                    auto pathPos = buffer.find("\"model_path\":\"");
                    if (pathPos != std::string::npos) {
                        auto start = pathPos + 14;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            modelPath = buffer.substr(start, end - start);
                        }
                    }

                    // 命令里给的相对路径会按进程 CWD 解析，而 Worker 的 CWD 继承自 Host，
                    // 不是模型所在目录。统一折算到 models 目录下，绝对路径则原样使用。
                    if (!modelPath.empty() && std::filesystem::path(modelPath).is_relative()) {
                        const auto leaf = std::filesystem::path(modelPath).filename().wstring();
                        modelPath = meetingai::util::resolveModelFileUtf8(leaf.c_str());
                    }

                    // 解析设备选择
                    std::string device = "CPU";  // 默认使用 CPU
                    auto devicePos = buffer.find("\"device\":\"");
                    if (devicePos != std::string::npos) {
                        auto start = devicePos + 10;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            device = buffer.substr(start, end - start);
                        }
                    }

                    std::string debug1 =
                        "{\"type\":\"info\",\"message\":\"[Worker] OpenVINO Whisper 模型路径: " +
                        meetingai::proto::jsonEscape(modelPath) + "\"}\n";
                    WriteStreamingMessage(hPipe, debug1);

                    std::string debug2 =
                        "{\"type\":\"info\",\"message\":\"[Worker] OpenVINO Whisper 设备: " +
                        meetingai::proto::jsonEscape(device) + "\"}\n";
                    WriteStreamingMessage(hPipe, debug2);

                    std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker] 开始加载 OpenVINO Whisper 模型...\"}\n";
                    WriteStreamingMessage(hPipe, debug3);

                    // 加载 OpenVINO Whisper 模型（支持热拔插）
                    bool success = meetingai::transcribe::LoadWhisperOpenVINOModel(modelPath, device);
                    if (success) {
                        std::string ready =
                            "{\"type\":\"whisper_openvino_ready\",\"model_path\":\"" +
                            meetingai::proto::jsonEscape(modelPath) +
                            "\",\"device\":\"" +
                            meetingai::proto::jsonEscape(device) + "\"}\n";
                        WriteStreamingMessage(hPipe, ready);
                        std::wcout << L"[Worker] OpenVINO Whisper 模型加载成功\n";
                    }
                    else {
                        std::string err = "{\"type\":\"whisper_openvino_error\",\"message\":\"模型加载失败\"}\n";
                        WriteStreamingMessage(hPipe, err);
                        std::wcerr << L"[Worker] OpenVINO Whisper 加载失败\n";
                    }

                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 卸载命令 ----
                if (buffer.find("\"unload_whisper_openvino\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_whisper_openvino 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 OpenVINO Whisper 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 OpenVINO Whisper 资源（支持热拔插）
                    meetingai::transcribe::UnloadWhisperOpenVINOModel();

                    std::string unloaded = "{\"type\":\"whisper_openvino_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] OpenVINO Whisper 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：LLaVA 卸载命令 ----
                if (buffer.find("\"unload_llava\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_llava 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 LLaVA 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 LLaVA 资源（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_llava_mutex);
                        g_llava.reset();
                        g_llava_loaded = false;
                    }

                    std::string unloaded = "{\"type\":\"llava_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] LLaVA 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Granite & Embedding 卸载命令 ----
                if (buffer.find("\"unload_granite_embedding\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_granite_embedding 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 Granite & Embedding 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 Granite 和 Embedding 资源（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_granite_mutex);
                        g_granite.reset();
                        g_granite_loaded = false;
                    }
                    {
                        std::lock_guard<std::mutex> lock(g_embedding_mutex);
                        g_embedding.reset();
                        g_embedding_loaded = false;
                    }

                    std::string unloaded = "{\"type\":\"granite_embedding_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] Granite & Embedding 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Granite 命令处理 ----
                if (buffer.find("\"granite_") != std::string::npos) {
                    if (g_meeting_summary.IsGenerating()) {
                        WriteStreamingMessage(
                            hPipe,
                            "{\"type\":\"error\","
                            "\"message\":\"会议摘要正在使用 Granite，"
                            "请稍后再发送聊天请求\"}\n");
                        buffer.clear();
                        continue;
                    }
                    handleGraniteCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Embedding 命令处理 ----
                if (buffer.find("\"embedding_") != std::string::npos) {
                    handleEmbeddingCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：LLaVA 命令处理 ----
                if (buffer.find("\"llava_") != std::string::npos || buffer.find("\"load_llava\"") != std::string::npos) {
                    handleLLaVACommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Stable Diffusion 命令处理 ----
                if (buffer.find("\"sd_") != std::string::npos || buffer.find("\"load_sd\"") != std::string::npos) {
                    // 如果是加载命令
                    if (buffer.find("\"load_sd\"") != std::string::npos) {
                        std::string device = "NPU";  // 默认使用 NPU
                        auto devicePos = buffer.find("\"device\":\"");
                        if (devicePos != std::string::npos) {
                            auto start = devicePos + 10;
                            auto end = buffer.find("\"", start);
                            if (end != std::string::npos) {
                                device = buffer.substr(start, end - start);
                            }
                        }

                        // 支持热拔插
                        {
                            std::lock_guard<std::mutex> lock(g_sd_mutex);
                            if (!g_sd_loaded) {
                                InitializeSDEngine(hPipe, device);
                                g_sd_loaded =
                                    g_sd && g_sd->isInitialized();
                            }
                        }
                    } else {
                        // 其他 SD 命令
                        handleSDCommand(hPipe, buffer);
                    }
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Token 计数命令处理 ----
                if (buffer.find("\"count_tokens\"") != std::string::npos) {
                    handleCountTokensCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 转录命令处理 ----
                if (meetingai::proto::isTranscribeOpenVINO(buffer)) {
                    handleTranscribeOpenVINOCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- Streaming Meeting：失败后重试会后精修 ----
                if (meetingai::proto::isRetryMeetingPostProcess(buffer)) {
                    const std::int64_t meetingId =
                        meetingai::proto::extractMeetingId(buffer);
                    if (meetingId <= 0) {
                        WriteStreamingMessage(
                            hPipe,
                            "{\"type\":\"streaming_postprocess_error\","
                            "\"message\":\"无效的会议编号\"}\n");
                    }
                    else if (g_postprocess_running.load()) {
                        WriteStreamingMessage(
                            hPipe,
                            "{\"type\":\"streaming_postprocess_error\","
                            "\"meeting_id\":" +
                            std::to_string(meetingId) +
                            ",\"message\":\"已有会后精修任务正在运行\"}\n");
                    }
                    else {
                        MeetingPostProcessInput input;
                        if (!LoadMeetingPostProcessInput(
                                meetingId,
                                input)) {
                            WriteStreamingMessage(
                                hPipe,
                                "{\"type\":\"streaming_postprocess_error\","
                                "\"meeting_id\":" +
                                std::to_string(meetingId) +
                                ",\"message\":\"找不到该会议的录音文件记录\"}\n");
                        }
                        else {
                            if (g_meeting_summary.IsRunning()) {
                                g_meeting_summary.Stop(false);
                            }
                            const bool started =
                                StartMeetingPostProcess(
                                    hPipe,
                                    meetingId,
                                    std::move(input.audioPaths),
                                    input.translationMode,
                                    input.hotwordsText,
                                    meetingai::proto::
                                        extractSummaryEnabled(buffer));
                            if (!started) {
                                WriteStreamingMessage(
                                    hPipe,
                                    "{\"type\":\"streaming_postprocess_error\","
                                    "\"meeting_id\":" +
                                    std::to_string(meetingId) +
                                    ",\"message\":\"无法启动重试任务\"}\n");
                            }
                        }
                    }
                    buffer.clear();
                    continue;
                }

                // ---- Streaming Meeting：手动立即生成一版滚动摘要 ----
                if (meetingai::proto::isRequestMeetingSummary(buffer)) {
                    if (!g_meeting_summary.IsRunning()) {
                        const std::int64_t meetingId =
                            meetingai::proto::extractMeetingId(buffer);
                        if (meetingId > 0) {
                            StartMeetingSummaryService(
                                hPipe,
                                meetingId,
                                true);
                        }
                    }
                    if (g_meeting_summary.IsRunning()) {
                        g_meeting_summary.RequestNow();
                        WriteStreamingMessage(
                            hPipe,
                            "{\"type\":\"streaming_summary_status\","
                            "\"state\":\"queued\","
                            "\"message\":\"已捕获当前字幕快照，正在生成详细摘要\"}\n");
                    }
                    else {
                        WriteStreamingMessage(
                            hPipe,
                            "{\"type\":\"streaming_summary_status\","
                            "\"state\":\"error\","
                            "\"message\":\"会议摘要服务尚未运行\"}\n");
                    }
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Sherpa-ONNX 实时流式转录命令处理 ----
                if (meetingai::proto::isStartStreaming(buffer) ||
                    meetingai::proto::isStreamingAudio(buffer) ||
                    meetingai::proto::isStopStreaming(buffer)) {
                    handleSherpaStreamingCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // 正常回显
                std::string resp = "{\"type\":\"pong\",\"echo\":\"" + buffer + "\"}\n";
                DWORD written = 0;
                if (!WriteFile(hPipe, resp.data(), static_cast<DWORD>(resp.size()), &written, nullptr)) {
                    meetingai::util::logLastError(L"[Worker] WriteFile failed");
                    break; // 写失败也结束本次连接
                }
                else {
                    std::wcout << L"[Worker] response sent to client\n";
                }

                // 清空缓冲，继续等待下一条消息
                buffer.clear();
            }
            else {
                buffer.push_back(ch);
            }
        }

        // 4) 清理当前连接，回到外层 while 等待下一个客户端或退出
        // Host 异常退出时不会发送 stop_streaming。这里必须释放两路 Sherpa
        // session、摘要线程并封口数据库会议，否则下一次连接会误判已有会话。
        if (!g_streaming_active_sources.empty() ||
            g_streaming_meeting_id > 0) {
            g_offline_translator.Stop(false);
            g_meeting_summary.Stop(false);
            g_meeting_audio_recorder.Stop();
            if (g_sherpa) {
                for (const std::string& source :
                     g_streaming_active_sources) {
                    if (!g_sherpa->IsRunning(source)) {
                        continue;
                    }
                    std::vector<
                        meetingai::transcribe::SherpaStreamResult> ignored;
                    g_sherpa->EndSession(source, ignored);
                }
            }
            CloseStreamingMeetingRecord();
            g_streaming_active_sources.clear();
            g_streaming_pending_raw.clear();
            g_streaming_utterance_ids.clear();
            ResetStreamingPersistenceState();
        }
        // 正常停止后可能保留了只响应手动请求的会后摘要服务。
        // Pipe 已断开时必须一并释放，避免回调继续持有失效的句柄。
        if (g_meeting_summary.IsRunning()) {
            g_meeting_summary.Stop(false);
        }
        // 会后精修仍在运行时，让它把数据库任务安全收尾后再释放管道。
        // 这样即使用户在处理过程中关掉页面，已录制的会议仍会完成保存。
        {
            std::lock_guard<std::mutex> lock(g_postprocess_mutex);
            if (g_postprocess_thread.joinable()) {
                g_postprocess_thread.join();
            }
        }
        {
            std::lock_guard<std::mutex> lock(g_granite_stream_start_mutex);
            if (g_granite_stream_thread.joinable()) {
                g_granite_stream_thread.join();
            }
        }
        if (g_meeting_summary.IsRunning()) {
            g_meeting_summary.Stop(false);
        }

        FlushFileBuffers(hPipe);
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }

    if (pSD) LocalFree(pSD);
    if (hMutex) CloseHandle(hMutex);
    std::wcout << L"[Worker] exit.\n";
    return 0;
}
