#include "pch.h"
#include "transcriber.hpp"
#include "whisper.h"
#include <iostream>
#include <fstream>
#include <vector>
#include <windows.h>
#include <thread>
#include <string>
#include <algorithm>
#include <mutex>
#include <atomic>
#include <chrono>   // ★ 用于计时

extern HANDLE g_pipe_for_callback;   // ★ 声明：用外面的那份

// 最简 JSON 转义，避免字幕里带引号把 JSON 搞坏
static std::string EscapeJson(const char* s) {
    std::string out; out.reserve(strlen(s) + 8);
    for (const unsigned char c : std::string(s)) {
        switch (c) {
        case '\"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b";  break;
        case '\f': out += "\\f";  break;
        case '\n': out += "\\n";  break;
        case '\r': out += "\\r";  break;
        case '\t': out += "\\t";  break;
        default:
            if (c < 0x20) { char buf[7]; sprintf_s(buf, "\\u%04x", c); out += buf; }
            else out += (char)c;
        }
    }
    return out;
}

static void OnProgress(whisper_context*, whisper_state*, int progress, void*) {
    if (g_pipe_for_callback) {
        std::string js = std::string("{\"type\":\"asr_progress\",\"percent\":")
            + std::to_string(progress) + "}\n";
        DWORD w = 0; WriteFile(g_pipe_for_callback, js.data(), (DWORD)js.size(), &w, nullptr);
    }
}

// ★ 用 state 读取新片段（与 with_state API 对齐）
static void OnNewSegment(whisper_context* /*ctx*/, whisper_state* state, int n_new, void* /*user_data*/) {
    const int n = whisper_full_n_segments_from_state(state);
    for (int i = n - n_new; i < n; ++i) {
        const char* text = whisper_full_get_segment_text_from_state(state, i);
        if (!text || !*text) continue;

        static thread_local std::string last;
        if (last == text) continue;
        last = text;

        const int t0_cs = whisper_full_get_segment_t0_from_state(state, i);
        const int t1_cs = whisper_full_get_segment_t1_from_state(state, i);

        std::string line = std::string("{\"type\":\"asr_segment\",\"text\":\"")
            + EscapeJson(text) + "\",\"t0_ms\":" + std::to_string(t0_cs * 10)
            + ",\"t1_ms\":" + std::to_string(t1_cs * 10) + "}\n";

        if (g_pipe_for_callback) {
            DWORD w = 0;
            WriteFile(g_pipe_for_callback, line.data(), (DWORD)line.size(), &w, nullptr);
        }
    }
}

// 全局 whisper 上下文（常驻，避免重复加载模型）
static struct whisper_context* g_whisper_ctx = nullptr;
static std::once_flag g_model_once; // 只加载一次

// 简化版 WAV 加载（假设 16-bit PCM，44 字节头）
static bool LoadWavFile(const std::string& filename, std::vector<float>& audio_data) {
    std::ifstream file(filename, std::ios::binary);
    if (!file.is_open()) {
        std::cerr << "[Whisper] 无法打开音频文件: " << filename << std::endl;
        return false;
    }
    file.seekg(44);

    std::vector<int16_t> pcm_data;
    int16_t sample;
    while (file.read(reinterpret_cast<char*>(&sample), sizeof(sample))) {
        pcm_data.push_back(sample);
    }

    audio_data.resize(pcm_data.size());
    for (size_t i = 0; i < pcm_data.size(); ++i) {
        audio_data[i] = static_cast<float>(pcm_data[i]) / 32768.0f;
    }

    std::cout << "[Whisper] 加载音频文件成功，样本数: " << audio_data.size() << std::endl;
    return true;
}

// 显式初始化（不建议每次任务调用）
bool InitWhisper(const std::string& modelPath) {
    if (g_whisper_ctx != nullptr) {
        whisper_free(g_whisper_ctx);
        g_whisper_ctx = nullptr;
    }

    std::cout << "[Whisper] 正在加载模型: " << modelPath << std::endl;

    struct whisper_context_params cparams = whisper_context_default_params();
    g_whisper_ctx = whisper_init_from_file_with_params(modelPath.c_str(), cparams);

    if (g_whisper_ctx == nullptr) {
        std::cerr << "[Whisper] 模型加载失败: " << modelPath << std::endl;
        return false;
    }

    std::cout << "[Whisper] 模型加载成功" << std::endl;
    return true;
}

// ★ 仅加载一次（多次调用也只会首次真正加载）
bool InitWhisperOnce(const std::string& modelPath) {
    std::call_once(g_model_once, [&]() {
        (void)InitWhisper(modelPath);
        });
    return g_whisper_ctx != nullptr;
}

void CleanupWhisper() {
    if (g_whisper_ctx != nullptr) {
        whisper_free(g_whisper_ctx);
        g_whisper_ctx = nullptr;
    }
}

bool TranscribeAudioFile(
    const std::string& modelPath,
    const std::string& audioPath,
    std::vector<WhisperSegment>& segments
) {
    // —— A：只在第一次调用时加载模型（随后复用全局 ctx） ——
    if (!InitWhisperOnce(modelPath)) {
        std::cerr << "[Whisper] 模型初始化失败（InitWhisperOnce）: " << modelPath << std::endl;
        return false;
    }

    // 加载音频文件
    std::vector<float> audio_data;
    if (!LoadWavFile(audioPath, audio_data)) {
        return false;
    }

    // —— 每次任务仅创建 whisper_state（轻量，可并发） ——
    whisper_state* st = whisper_init_state(g_whisper_ctx);   // 新 API：返回指针
    if (!st) {
        std::cerr << "[Whisper] 创建 whisper_state 失败" << std::endl;
        return false;
    }

    // —— 设置 whisper 参数（工业折中：半句话就回传） ——
    struct whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);

    // 性能：用满 CPU 线程（Release | x64 下效果最佳）
    unsigned int t = std::thread::hardware_concurrency();
    if (t == 0) t = 4;
    params.n_threads = (int)t;
    std::cout << "[Whisper] n_threads = " << params.n_threads << std::endl;

    // 分段与上下文
    params.max_tokens = 64;     // 48~96 之间调
    params.max_len = 80;     // 每段最长字符
    params.split_on_word = true;   // 词边界切分
    params.audio_ctx = 768;    // 上下文窗口
    params.no_context = true;   // ★ 加速：不带入历史上下文（你当前流程不需要上下文）

    // 其他设置
    params.language = "zh";   // 自动检测语言
    params.translate = false;    // 不翻译
    params.print_realtime = false;
    params.print_progress = false;
    params.print_timestamps = true;

    // 回调
    params.progress_callback = &OnProgress;
    params.progress_callback_user_data = nullptr;
    params.new_segment_callback_user_data = nullptr;
    params.new_segment_callback = &OnNewSegment;

    // ★ 统计总耗时（从“开始转录”到推理结束）
    auto t_total_begin = std::chrono::high_resolution_clock::now();

    std::cout << "[Whisper] 开始转录，音频长度: "
        << (audio_data.size() / 16000.0) << " 秒" << std::endl;

    // —— 使用 with_state：复用全局 ctx，仅 state 属于本次任务 ——
    auto t_begin = std::chrono::high_resolution_clock::now();

    int rc = whisper_full_with_state(g_whisper_ctx, st, params,
        audio_data.data(), (int)audio_data.size());

    auto t_end = std::chrono::high_resolution_clock::now();
    auto ms_infer = std::chrono::duration_cast<std::chrono::milliseconds>(t_end - t_begin).count();
    std::cout << "[Whisper][Perf] infer=" << ms_infer << " ms" << std::endl;

    if (rc != 0) {
        std::cerr << "[Whisper] 转录失败" << std::endl;
        whisper_free_state(st);
        return false;
    }

    // —— 提取最终结果（从 state 取；线程安全） ——
    const int n_segments = whisper_full_n_segments_from_state(st);
    segments.clear();
    segments.reserve(n_segments);

    for (int i = 0; i < n_segments; ++i) {
        WhisperSegment seg;
        seg.text = whisper_full_get_segment_text_from_state(st, i);
        seg.start_time = whisper_full_get_segment_t0_from_state(st, i) / 100.0; // cs → s
        seg.end_time = whisper_full_get_segment_t1_from_state(st, i) / 100.0;

        segments.push_back(seg);

        std::cout << "[Whisper] Segment " << i << ": ["
            << seg.start_time << "s - " << seg.end_time << "s] "
            << seg.text << std::endl;
    }

    whisper_free_state(st); // 释放本次任务的 state

    auto t_total_end = std::chrono::high_resolution_clock::now();
    auto ms_total = std::chrono::duration_cast<std::chrono::milliseconds>(t_total_end - t_total_begin).count();
    std::cout << "[Whisper][Perf] total=" << ms_total << " ms" << std::endl;

    std::cout << "[Whisper] 转录完成，共 " << segments.size() << " 个片段" << std::endl;
    return true;
}
