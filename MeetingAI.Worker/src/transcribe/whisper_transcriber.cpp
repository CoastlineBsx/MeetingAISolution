#include "pch.h"
#include "transcriber.hpp"
#include "whisper.h"
#include "fvad.h"  // ★ WebRTC VAD
#include <iostream>
#include <fstream>
#include <vector>
#include <windows.h>
#include <thread>
#include <string>
#include <algorithm>
#include <mutex>
#include <atomic>
#include <chrono>
#include <cstring>  // for strncmp
#include <cmath>    // for sin, M_PI

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

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

// WAV 加载并转为 16kHz 单声道（Whisper 标准格式）
static bool LoadWavFile(const std::string& filename, std::vector<float>& audio_data) {
    std::ifstream file(filename, std::ios::binary);
    if (!file.is_open()) {
        std::cerr << "[Whisper] 无法打开音频文件: " << filename << std::endl;
        return false;
    }

    // 读取 WAV 头
    char riff[4], wave[4], fmt[4];
    uint32_t chunk_size, subchunk1_size, byte_rate, subchunk2_size;
    uint16_t audio_format, num_channels, block_align, bits_per_sample;
    uint32_t sample_rate;

    file.read(riff, 4); // "RIFF"
    file.read(reinterpret_cast<char*>(&chunk_size), 4);
    file.read(wave, 4); // "WAVE"
    file.read(fmt, 4);  // "fmt "
    file.read(reinterpret_cast<char*>(&subchunk1_size), 4);
    file.read(reinterpret_cast<char*>(&audio_format), 2);
    file.read(reinterpret_cast<char*>(&num_channels), 2);
    file.read(reinterpret_cast<char*>(&sample_rate), 4);
    file.read(reinterpret_cast<char*>(&byte_rate), 4);
    file.read(reinterpret_cast<char*>(&block_align), 2);
    file.read(reinterpret_cast<char*>(&bits_per_sample), 2);

    // 跳过可能的扩展字段，找到 "data"
    if (subchunk1_size > 16) {
        file.seekg(subchunk1_size - 16, std::ios::cur);
    }

    char data_marker[4];
    file.read(data_marker, 4);
    while (strncmp(data_marker, "data", 4) != 0 && file) {
        file.read(reinterpret_cast<char*>(&subchunk2_size), 4);
        file.seekg(subchunk2_size, std::ios::cur);
        file.read(data_marker, 4);
    }
    file.read(reinterpret_cast<char*>(&subchunk2_size), 4);

    std::cout << "[Whisper] WAV 格式: " << sample_rate << " Hz, "
        << num_channels << " 声道, " << bits_per_sample << " bit, format=" << audio_format << std::endl;

    // 读取原始音频数据（支持16位PCM和32位浮点）
    std::vector<float> raw_samples;
    size_t num_samples = subchunk2_size / (bits_per_sample / 8);
    raw_samples.reserve(num_samples);

    if (audio_format == 3 && bits_per_sample == 32) {
        // IEEE Float 32位浮点格式（WASAPI Loopback默认格式）
        std::cout << "[Whisper] 检测到32位浮点格式 (IEEE Float)" << std::endl;
        float sample;
        while (file.read(reinterpret_cast<char*>(&sample), sizeof(sample))) {
            raw_samples.push_back(sample);
        }
    }
    else if (audio_format == 1 && bits_per_sample == 16) {
        // PCM 16位整数格式
        std::cout << "[Whisper] 检测到16位PCM格式" << std::endl;
        int16_t sample;
        while (file.read(reinterpret_cast<char*>(&sample), sizeof(sample))) {
            raw_samples.push_back(sample / 32768.0f);
        }
    }
    else {
        std::cerr << "[Whisper] 不支持的音频格式: format=" << audio_format
            << ", bits=" << bits_per_sample << std::endl;
        return false;
    }

    // 转为单声道（取平均）
    std::vector<float> mono_data;
    if (num_channels == 1) {
        mono_data = std::move(raw_samples);
    } else {
        size_t mono_size = raw_samples.size() / num_channels;
        mono_data.resize(mono_size);
        for (size_t i = 0; i < mono_size; ++i) {
            float sum = 0.0f;
            for (int ch = 0; ch < num_channels; ++ch) {
                sum += raw_samples[i * num_channels + ch];
            }
            mono_data[i] = sum / num_channels;
        }
        std::cout << "[Whisper] 转换为单声道: " << num_channels << " → 1 声道" << std::endl;
    }

    // 重采样到 16kHz（Lanczos-3 sinc 插值 + 抗混叠滤波）
    if (sample_rate != 16000) {
        double ratio = static_cast<double>(sample_rate) / 16000.0;
        size_t target_size = static_cast<size_t>(mono_data.size() / ratio);
        audio_data.resize(target_size);

        // Lanczos-3 窗口 sinc 插值（工业级抗混叠）
        auto lanczos_kernel = [](double x, int a = 3) -> double {
            if (x == 0.0) return 1.0;
            if (std::abs(x) >= a) return 0.0;
            const double pi_x = M_PI * x;
            return (a * std::sin(pi_x) * std::sin(pi_x / a)) / (pi_x * pi_x);
        };

        std::cout << "[Whisper] 重采样: " << sample_rate << " Hz → 16000 Hz (Lanczos-3 sinc)" << std::endl;

        for (size_t i = 0; i < target_size; ++i) {
            double src_idx = i * ratio;
            int center = static_cast<int>(src_idx);

            // Lanczos-3: 使用前后各3个采样点（共7个点）
            double sum = 0.0;
            double weight_sum = 0.0;

            for (int tap = -3; tap <= 3; ++tap) {
                int src_pos = center + tap;
                if (src_pos < 0 || src_pos >= static_cast<int>(mono_data.size())) continue;

                double offset = src_idx - src_pos;
                double weight = lanczos_kernel(offset, 3);
                sum += mono_data[src_pos] * weight;
                weight_sum += weight;
            }

            audio_data[i] = static_cast<float>(weight_sum > 0.0 ? sum / weight_sum : 0.0);
        }
    } else {
        audio_data = std::move(mono_data);
    }

    // ★ 音频增强：提升人声检测率
    // 1. 高通滤波（去除低频伴奏，保留人声频段 300Hz+）
    // 简单IIR高通（一阶：y[i] = alpha * (y[i-1] + x[i] - x[i-1])）
    const float alpha = 0.95f;  // 截止频率约 300Hz @ 16kHz
    float prev_in = 0.0f, prev_out = 0.0f;
    for (size_t i = 0; i < audio_data.size(); ++i) {
        float curr_in = audio_data[i];
        float curr_out = alpha * (prev_out + curr_in - prev_in);
        audio_data[i] = curr_out;
        prev_in = curr_in;
        prev_out = curr_out;
    }

    // 2. 音量归一化（峰值归一化到 0.8，避免削波）
    float max_abs = 0.0f;
    for (float sample : audio_data) {
        max_abs = (std::max)(max_abs, std::abs(sample));
    }
    if (max_abs > 0.01f) {  // 避免除零
        float scale = 0.8f / max_abs;
        for (float& sample : audio_data) {
            sample *= scale;
        }
        std::cout << "[Whisper] 音量归一化: 增益 " << (scale > 1.0f ? "+" : "")
            << (int)((scale - 1.0f) * 100) << "%" << std::endl;
    }

    std::cout << "[Whisper] 加载完成，样本数: " << audio_data.size()
        << " (时长 " << (audio_data.size() / 16000.0) << " 秒)" << std::endl;
    return true;
}

// 音频场景检测（自动识别音乐/对话）
enum class AudioScene {
    MUSIC,      // 音乐/歌曲
    SPEECH,     // 对话/会议
    MIXED       // 混合（带背景音乐的对话）
};

// VAD 段落（静音 or 有人声）
struct SilenceSegment {
    double start_time;
    double end_time;
};

struct VoiceSegment {
    double start_time;
    double end_time;
    size_t start_sample;  // 在音频数组中的起始位置
    size_t end_sample;    // 在音频数组中的结束位置
};

static AudioScene DetectAudioScene(const std::vector<float>& audio_data) {
    if (audio_data.size() < 16000) {
        return AudioScene::SPEECH; // 太短，默认对话
    }

    // 1. 计算零交叉率（Zero Crossing Rate）
    //    音乐：低ZCR（平滑波形）
    //    语音：高ZCR（快速变化）
    size_t zero_crossings = 0;
    for (size_t i = 1; i < (std::min)(audio_data.size(), size_t(16000 * 10)); ++i) {
        if ((audio_data[i] >= 0 && audio_data[i - 1] < 0) ||
            (audio_data[i] < 0 && audio_data[i - 1] >= 0)) {
            zero_crossings++;
        }
    }
    float zcr = static_cast<float>(zero_crossings) / (std::min)(audio_data.size(), size_t(16000 * 10));

    // 2. 计算能量方差（Energy Variance）
    //    音乐：方差小（能量平稳）
    //    语音：方差大（有停顿）
    const size_t frame_size = 400; // 25ms @ 16kHz
    std::vector<float> frame_energies;
    for (size_t i = 0; i + frame_size < audio_data.size(); i += frame_size) {
        float energy = 0.0f;
        for (size_t j = 0; j < frame_size; ++j) {
            energy += audio_data[i + j] * audio_data[i + j];
        }
        frame_energies.push_back(energy / frame_size);
    }

    float mean_energy = 0.0f;
    for (float e : frame_energies) mean_energy += e;
    mean_energy /= frame_energies.size();

    float variance = 0.0f;
    for (float e : frame_energies) {
        float diff = e - mean_energy;
        variance += diff * diff;
    }
    variance /= frame_energies.size();

    // 3. 判断逻辑（调整后的阈值）
    std::cout << "[Whisper] 音频特征分析: ZCR=" << zcr
              << ", EnergyVar=" << variance << std::endl;

    // ★ 放宽音乐判定（优先避免重复）
    if (zcr < 0.12f || variance < 0.005f) {
        std::cout << "[Whisper] 场景识别: 音乐/歌曲" << std::endl;
        return AudioScene::MUSIC;
    } else if (zcr > 0.20f && variance > 0.02f) {
        std::cout << "[Whisper] 场景识别: 对话/会议" << std::endl;
        return AudioScene::SPEECH;
    } else {
        std::cout << "[Whisper] 场景识别: 混合场景（带背景音）" << std::endl;
        return AudioScene::MIXED;
    }
}

// ★★ 工业级VAD：使用 WebRTC VAD (libfvad) ★★
// Google WebRTC 项目的久经考验的 VAD 算法
static std::vector<SilenceSegment> DetectRealSilence(const std::vector<float>& audio_data, AudioScene scene) {
    std::vector<SilenceSegment> silence_segments;

    if (audio_data.empty()) {
        return silence_segments;
    }

    // ★ 1. 创建 VAD 实例
    Fvad* vad = fvad_new();
    if (!vad) {
        std::cerr << "[VAD] 创建 WebRTC VAD 实例失败" << std::endl;
        return silence_segments;
    }

    // ★ 2. 配置 VAD 参数
    // Mode: 0=Quality, 1=Low Bitrate, 2=Aggressive, 3=Very Aggressive
    // 根据场景选择不同的模式
    int vad_mode = 1;  // 默认 mode 1
    if (scene == AudioScene::MUSIC) {
        vad_mode = 0;  // 音乐模式使用 Quality mode（最宽松，避免把音乐误判为静音）
    } else if (scene == AudioScene::SPEECH) {
        vad_mode = 2;  // 对话模式使用 Aggressive mode（更严格）
    }

    if (fvad_set_mode(vad, vad_mode) < 0) {
        std::cerr << "[VAD] 设置 VAD 模式失败" << std::endl;
        fvad_free(vad);
        return silence_segments;
    }

    // 采样率：16000 Hz
    if (fvad_set_sample_rate(vad, 16000) < 0) {
        std::cerr << "[VAD] 设置采样率失败" << std::endl;
        fvad_free(vad);
        return silence_segments;
    }

    std::cout << "[VAD] WebRTC VAD 初始化成功 (mode=" << vad_mode
              << " [" << (vad_mode == 0 ? "Quality" : vad_mode == 1 ? "Low Bitrate" : vad_mode == 2 ? "Aggressive" : "Very Aggressive")
              << "], rate=16000)" << std::endl;

    // ★ 3. 转换 float → int16_t（libfvad 需要 int16 格式）
    std::vector<int16_t> audio_int16;
    audio_int16.reserve(audio_data.size());
    for (float sample : audio_data) {
        // Clamp 到 [-1.0, 1.0] 范围
        sample = (std::max)(-1.0f, (std::min)(1.0f, sample));
        audio_int16.push_back(static_cast<int16_t>(sample * 32767.0f));
    }

    // ★ 4. 逐帧进行 VAD 检测
    const size_t sample_rate = 16000;
    const size_t frame_duration_ms = 30;  // 30ms 帧（libfvad 支持 10/20/30ms）
    const size_t frame_size = (sample_rate * frame_duration_ms) / 1000;  // 480 samples

    std::cout << "[VAD] 帧大小: " << frame_size << " samples (" << frame_duration_ms << "ms)" << std::endl;

    // ★ 根据场景调整静音检测阈值
    size_t min_silence_frames;   // 最小静音帧数
    size_t hangover_frames;       // 挂起帧数
    double min_silence_duration;  // 最小静音时长（秒）

    if (scene == AudioScene::MUSIC) {
        // 音乐模式：非常保守，只检测长时间的真实静音
        min_silence_frames = 50;   // 1.5秒 (50帧×30ms)
        hangover_frames = 10;      // 容错：300ms
        min_silence_duration = 2.0; // 至少2秒才算静音
    } else if (scene == AudioScene::SPEECH) {
        // 对话模式：较敏感，检测说话间的停顿
        min_silence_frames = 10;   // 300ms
        hangover_frames = 3;       // 容错：90ms
        min_silence_duration = 0.5; // 至少0.5秒
    } else {
        // 混合模式：中等
        min_silence_frames = 20;   // 600ms
        hangover_frames = 5;       // 容错：150ms
        min_silence_duration = 1.0; // 至少1秒
    }

    std::cout << "[VAD] 静音阈值: min_duration=" << min_silence_duration << "s, "
              << "min_frames=" << min_silence_frames << ", hangover=" << hangover_frames << std::endl;

    // 状态机：跟踪静音段落
    bool in_silence = false;
    double silence_start = 0.0;
    size_t silence_frame_count = 0;
    size_t hangover_count = 0;

    size_t total_frames = audio_int16.size() / frame_size;
    std::cout << "[VAD] 总帧数: " << total_frames << std::endl;

    for (size_t i = 0; i + frame_size <= audio_int16.size(); i += frame_size) {
        double frame_time = static_cast<double>(i) / sample_rate;

        // 调用 WebRTC VAD
        int is_voice = fvad_process(vad, &audio_int16[i], frame_size);

        if (is_voice < 0) {
            std::cerr << "[VAD] VAD 处理错误" << std::endl;
            break;
        }

        // is_voice: 1=有人声, 0=静音/背景音
        if (!in_silence) {
            // 当前不在静音中
            if (is_voice == 0) {
                // 检测到静音，开始跟踪
                silence_start = frame_time;
                silence_frame_count = 1;
                in_silence = true;
            }
        } else {
            // 当前在静音中
            if (is_voice == 0) {
                // 继续静音
                silence_frame_count++;
                hangover_count = 0;  // 重置挂起
            } else {
                // 检测到人声，但给予容错
                hangover_count++;
                silence_frame_count++;

                if (hangover_count >= hangover_frames) {
                    // 超过挂起时间，结束静音
                    if (silence_frame_count >= min_silence_frames) {
                        // 足够长，记录这段静音
                        SilenceSegment seg;
                        seg.start_time = silence_start;
                        seg.end_time = frame_time - (hangover_count * frame_duration_ms / 1000.0);

                        // ★ 使用场景相关的最小静音时长过滤
                        if (seg.end_time - seg.start_time >= min_silence_duration) {
                            silence_segments.push_back(seg);
                        }
                    }
                    in_silence = false;
                    hangover_count = 0;
                    silence_frame_count = 0;
                }
            }
        }
    }

    // ★ 5. 处理结尾的静音
    if (in_silence && silence_frame_count >= min_silence_frames) {
        SilenceSegment seg;
        seg.start_time = silence_start;
        seg.end_time = static_cast<double>(audio_int16.size()) / sample_rate;

        // ★ 使用场景相关的最小静音时长过滤
        if (seg.end_time - seg.start_time >= min_silence_duration) {
            silence_segments.push_back(seg);
        }
    }

    // ★ 6. 清理
    fvad_free(vad);

    std::cout << "[VAD] WebRTC 检测到 " << silence_segments.size() << " 段真实静音" << std::endl;
    for (size_t i = 0; i < silence_segments.size(); ++i) {
        const auto& seg = silence_segments[i];
        std::cout << "[VAD] 静音段 " << (i + 1) << ": ["
                  << seg.start_time << "s - " << seg.end_time << "s] ("
                  << (seg.end_time - seg.start_time) << "s)" << std::endl;
    }

    return silence_segments;
}

// ★★ 提取有人声的段落（治本方案：只转录有人声的部分）★★
static std::vector<VoiceSegment> DetectVoiceSegments(const std::vector<float>& audio_data, AudioScene scene) {
    std::vector<VoiceSegment> voice_segments;

    if (audio_data.empty()) {
        return voice_segments;
    }

    // 复用现有的 VAD 逻辑，但提取"有人声"的段落
    Fvad* vad = fvad_new();
    if (!vad) {
        std::cerr << "[VAD] 创建 WebRTC VAD 实例失败" << std::endl;
        return voice_segments;
    }

    int vad_mode = 1;
    if (scene == AudioScene::MUSIC) {
        vad_mode = 0;  // Quality mode - 宽松
    } else if (scene == AudioScene::SPEECH) {
        vad_mode = 2;  // Aggressive mode - 严格
    }

    fvad_set_mode(vad, vad_mode);
    fvad_set_sample_rate(vad, 16000);

    std::cout << "[VAD] 提取有人声段落 (mode=" << vad_mode << ")" << std::endl;

    // 转换为 int16
    std::vector<int16_t> audio_int16;
    audio_int16.reserve(audio_data.size());
    for (float sample : audio_data) {
        sample = (std::max)(-1.0f, (std::min)(1.0f, sample));
        audio_int16.push_back(static_cast<int16_t>(sample * 32767.0f));
    }

    const size_t sample_rate = 16000;
    const size_t frame_duration_ms = 30;
    const size_t frame_size = (sample_rate * frame_duration_ms) / 1000;

    // 状态机：跟踪有人声的段落
    bool in_voice = false;
    size_t voice_start_frame = 0;
    size_t consecutive_voice_frames = 0;
    size_t consecutive_silence_frames = 0;

    // 阈值：连续多少帧才确认是人声/静音
    const size_t min_voice_frames = 10;      // 至少 300ms 人声
    const size_t max_silence_gap_frames = 20; // 允许 600ms 的静音间隙（不断开）

    for (size_t i = 0; i + frame_size <= audio_int16.size(); i += frame_size) {
        size_t frame_idx = i / frame_size;
        int is_voice_frame = fvad_process(vad, &audio_int16[i], frame_size);

        if (is_voice_frame < 0) break;

        if (is_voice_frame == 1) {
            // 检测到人声帧
            consecutive_silence_frames = 0;

            if (!in_voice) {
                // 开始新的人声段落
                voice_start_frame = i;
                in_voice = true;
                consecutive_voice_frames = 1;
            } else {
                consecutive_voice_frames++;
            }
        } else {
            // 静音帧
            if (in_voice) {
                consecutive_silence_frames++;

                // 如果静音间隙太长，结束当前人声段落
                if (consecutive_silence_frames > max_silence_gap_frames) {
                    // 保存人声段落（如果足够长）
                    if (consecutive_voice_frames >= min_voice_frames) {
                        VoiceSegment seg;
                        seg.start_sample = voice_start_frame;
                        seg.end_sample = i - (consecutive_silence_frames * frame_size);
                        seg.start_time = static_cast<double>(seg.start_sample) / sample_rate;
                        seg.end_time = static_cast<double>(seg.end_sample) / sample_rate;

                        // 过滤过短的段落（< 1秒）
                        if (seg.end_time - seg.start_time >= 1.0) {
                            voice_segments.push_back(seg);
                        }
                    }

                    in_voice = false;
                    consecutive_voice_frames = 0;
                    consecutive_silence_frames = 0;
                }
            }
        }
    }

    // 处理结尾的人声段落
    if (in_voice && consecutive_voice_frames >= min_voice_frames) {
        VoiceSegment seg;
        seg.start_sample = voice_start_frame;
        seg.end_sample = audio_int16.size();
        seg.start_time = static_cast<double>(seg.start_sample) / sample_rate;
        seg.end_time = static_cast<double>(seg.end_sample) / sample_rate;

        if (seg.end_time - seg.start_time >= 1.0) {
            voice_segments.push_back(seg);
        }
    }

    fvad_free(vad);

    std::cout << "[VAD] 检测到 " << voice_segments.size() << " 段有人声的音频" << std::endl;
    for (size_t i = 0; i < voice_segments.size(); ++i) {
        const auto& seg = voice_segments[i];
        std::cout << "[VAD] 人声段 " << (i + 1) << ": ["
                  << seg.start_time << "s - " << seg.end_time << "s] ("
                  << (seg.end_time - seg.start_time) << "s)" << std::endl;
    }

    return voice_segments;
}

// ★★ 合并Whisper转录结果和VAD静音段 ★★
static void MergeSegmentsWithSilence(
    std::vector<WhisperSegment>& output_segments,
    const std::vector<WhisperSegment>& whisper_segments,
    const std::vector<SilenceSegment>& silence_segments,
    double audio_duration,
    AudioScene scene
) {
    output_segments.clear();

    // 创建所有segment的时间轴（用于排序）
    struct TimelineEntry {
        double start_time;
        double end_time;
        std::string text;
        int type; // 0=whisper, 1=silence, 2=unrecognized
    };

    std::vector<TimelineEntry> timeline;

    // 添加Whisper识别的segment
    for (const auto& seg : whisper_segments) {
        TimelineEntry entry;
        entry.start_time = seg.start_time;
        entry.end_time = seg.end_time;
        entry.text = seg.text;
        entry.type = 0;
        timeline.push_back(entry);
    }

    // 添加VAD检测的静音段
    for (const auto& seg : silence_segments) {
        TimelineEntry entry;
        entry.start_time = seg.start_time;
        entry.end_time = seg.end_time;
        entry.text = "[静音]";
        entry.type = 1;
        timeline.push_back(entry);
    }

    // 按开始时间排序
    std::sort(timeline.begin(), timeline.end(), [](const TimelineEntry& a, const TimelineEntry& b) {
        return a.start_time < b.start_time;
    });

    // 检测未覆盖的时间段（Whisper无法识别的音频）
    double last_covered_time = 0.0;
    std::vector<TimelineEntry> merged_timeline;

    for (const auto& entry : timeline) {
        // 如果有间隙（未覆盖的时间段）
        if (entry.start_time - last_covered_time > 1.0) {  // 间隙大于1秒
            TimelineEntry gap;
            gap.start_time = last_covered_time;
            gap.end_time = entry.start_time;
            // 根据场景选择标记
            if (scene == AudioScene::MUSIC) {
                gap.text = "[音乐]";  // 音乐模式下，未识别的可能是纯音乐
            } else {
                gap.text = "[无法识别]";  // 其他模式下
            }
            gap.type = 2;
            merged_timeline.push_back(gap);
        }

        merged_timeline.push_back(entry);
        last_covered_time = entry.end_time;
    }

    // 检查音频末尾是否有未覆盖部分
    if (audio_duration - last_covered_time > 1.0) {
        TimelineEntry gap;
        gap.start_time = last_covered_time;
        gap.end_time = audio_duration;
        if (scene == AudioScene::MUSIC) {
            gap.text = "[音乐]";
        } else {
            gap.text = "[无法识别]";
        }
        gap.type = 2;
        merged_timeline.push_back(gap);
    }

    // 转换为输出格式
    for (const auto& entry : merged_timeline) {
        WhisperSegment seg;
        seg.start_time = entry.start_time;
        seg.end_time = entry.end_time;
        seg.text = entry.text;
        output_segments.push_back(seg);
    }

    std::cout << "[Merge] 合并完成: Whisper=" << whisper_segments.size()
              << ", VAD静音=" << silence_segments.size()
              << ", 最终=" << output_segments.size() << " 段" << std::endl;
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
    std::vector<WhisperSegment>& segments,
    const std::string& sceneMode,
    const std::string& language
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

    // ★ 场景选择（手动优先，否则自动检测）
    AudioScene scene;
    if (sceneMode == "speech") {
        scene = AudioScene::SPEECH;
        std::cout << "[Whisper] 手动指定: 对话/会议模式" << std::endl;
    } else if (sceneMode == "music") {
        scene = AudioScene::MUSIC;
        std::cout << "[Whisper] 手动指定: 音乐/歌曲模式" << std::endl;
    } else if (sceneMode == "mixed") {
        scene = AudioScene::MIXED;
        std::cout << "[Whisper] 手动指定: 混合模式" << std::endl;
    } else {
        // auto 或其他值 → 自动检测
        scene = DetectAudioScene(audio_data);
    }

    // —— 每次任务仅创建 whisper_state（轻量，可并发） ——
    whisper_state* st = whisper_init_state(g_whisper_ctx);   // 新 API：返回指针
    if (!st) {
        std::cerr << "[Whisper] 创建 whisper_state 失败" << std::endl;
        return false;
    }

    // —— 自适应参数配置 ——
    struct whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);

    // 性能：用满 CPU 线程（Release | x64 下效果最佳）
    unsigned int t = std::thread::hardware_concurrency();
    if (t == 0) t = 4;
    params.n_threads = (int)t;
    std::cout << "[Whisper] n_threads = " << params.n_threads << std::endl;

    // ★★ 根据场景动态配置参数 ★★
    if (scene == AudioScene::MUSIC) {
        // 音乐/歌曲模式：平衡歌词识别和幻觉抑制
        std::cout << "[Whisper] 应用音乐模式参数" << std::endl;
        params.max_tokens = 48;            // ★ 提高到48（给歌词更多空间）
        params.max_len = 1;                // ★ 限制1个token（防止卡死）
        params.split_on_word = true;
        params.audio_ctx = 1500;
        params.no_context = true;          // 禁用上下文（避免重复）
        params.suppress_blank = true;
        params.temperature = 0.0f;
        params.temperature_inc = 0.0f;
        params.entropy_thold = 2.8f;       // ★ 适中阈值（平衡准确率）
        params.max_initial_ts = 1.0f;
        params.no_speech_thold = 0.6f;     // ★ 提高到0.6（更宽松，避免漏掉人声）
        params.logprob_thold = -1.0f;
    } else if (scene == AudioScene::SPEECH) {
        // 对话/会议模式：保留连贯性，提高检测率
        std::cout << "[Whisper] 应用对话模式参数" << std::endl;
        params.max_tokens = 64;
        params.max_len = 0;
        params.split_on_word = true;
        params.audio_ctx = 1500;
        params.no_context = false;         // 启用上下文（更连贯）
        params.suppress_blank = true;
        params.temperature = 0.0f;
        params.temperature_inc = 0.0f;
        params.entropy_thold = 2.2f;       // 中等阈值
        params.max_initial_ts = 1.0f;
        params.no_speech_thold = 0.7f;     // 宽松（检测更多弱音）
        params.logprob_thold = -1.0f;
    } else {
        // 混合模式：折中配置（也禁用上下文！）
        std::cout << "[Whisper] 应用混合模式参数" << std::endl;
        params.max_tokens = 40;            // ★ 降低
        params.max_len = 1;                // ★ 也限制
        params.split_on_word = true;
        params.audio_ctx = 1500;
        params.no_context = true;
        params.suppress_blank = true;
        params.temperature = 0.0f;
        params.temperature_inc = 0.0f;
        params.entropy_thold = 2.7f;       // 提高
        params.max_initial_ts = 1.0f;
        params.no_speech_thold = 0.5f;     // 严格
        params.logprob_thold = -1.0f;
    }

    // ★★ 语言设置（优先使用指定语言）
    if (language != "auto" && !language.empty()) {
        // 用户指定了语言
        params.language = language.c_str();
        std::cout << "[Whisper] 手动指定语言: " << language << std::endl;
    } else {
        // 自动检测，但针对音乐场景添加智能提示
        if (scene == AudioScene::MUSIC) {
            // ★ 音乐模式下，默认偏向中文（因为中文歌曲常被误判为英语）
            // Whisper仍会自动检测，但会优先考虑中文
            params.language = nullptr;  // 让Whisper自动检测
            params.initial_prompt = "以下是中文歌曲歌词：";  // 提示模型这是中文内容
            std::cout << "[Whisper] 音乐模式自动检测，添加中文提示" << std::endl;
        } else {
            params.language = nullptr;  // 完全自动检测
            std::cout << "[Whisper] 自动检测语言（中英日韩等）" << std::endl;
        }
    }
    params.translate = false;         // 不翻译
    params.print_realtime = false;
    params.print_progress = false;
    params.print_timestamps = true;
    params.single_segment = false;    // 允许多段

    // 回调
    params.progress_callback = &OnProgress;
    params.progress_callback_user_data = nullptr;
    params.new_segment_callback_user_data = nullptr;
    params.new_segment_callback = &OnNewSegment;

    // ★★ 治本方案：先VAD检测有人声的段落，只转录这些段落 ★★
    std::cout << "[VAD] 开始检测有人声的段落..." << std::endl;
    auto vad_begin = std::chrono::high_resolution_clock::now();
    std::vector<VoiceSegment> voice_segments = DetectVoiceSegments(audio_data, scene);
    auto vad_end = std::chrono::high_resolution_clock::now();
    auto ms_vad = std::chrono::duration_cast<std::chrono::milliseconds>(vad_end - vad_begin).count();
    std::cout << "[VAD][Perf] vad=" << ms_vad << " ms" << std::endl;

    // 计算人声覆盖率
    double total_voice_duration = 0.0;
    for (const auto& seg : voice_segments) {
        total_voice_duration += (seg.end_time - seg.start_time);
    }
    double audio_duration = audio_data.size() / 16000.0;
    double voice_ratio = (total_voice_duration / audio_duration) * 100.0;
    std::cout << "[VAD] 人声覆盖率: " << voice_ratio << "% ("
              << total_voice_duration << "s / " << audio_duration << "s)" << std::endl;

    // ★ 统计总耗时
    auto t_total_begin = std::chrono::high_resolution_clock::now();

    std::cout << "[Whisper] 开始分段转录，共 " << voice_segments.size() << " 个有人声段落" << std::endl;

    // ★★ 对每个有人声的段落单独转录 ★★
    std::vector<WhisperSegment> all_whisper_segments;
    int total_infer_ms = 0;

    for (size_t seg_idx = 0; seg_idx < voice_segments.size(); ++seg_idx) {
        const auto& voice_seg = voice_segments[seg_idx];

        std::cout << "[Whisper] 转录段落 " << (seg_idx + 1) << "/" << voice_segments.size()
                  << ": [" << voice_seg.start_time << "s - " << voice_seg.end_time << "s]" << std::endl;

        // 提取这段音频
        std::vector<float> segment_audio(
            audio_data.begin() + voice_seg.start_sample,
            audio_data.begin() + voice_seg.end_sample
        );

        // 对这段音频进行转录
        auto t_begin = std::chrono::high_resolution_clock::now();

        int rc = whisper_full_with_state(g_whisper_ctx, st, params,
            segment_audio.data(), (int)segment_audio.size());

        auto t_end = std::chrono::high_resolution_clock::now();
        auto ms_infer = std::chrono::duration_cast<std::chrono::milliseconds>(t_end - t_begin).count();
        total_infer_ms += ms_infer;

        if (rc != 0) {
            std::cerr << "[Whisper] 段落 " << (seg_idx + 1) << " 转录失败，跳过" << std::endl;
            continue;
        }

        // 提取这段的转录结果
        const int n_segments = whisper_full_n_segments_from_state(st);
        for (int i = 0; i < n_segments; ++i) {
            WhisperSegment seg;
            seg.text = whisper_full_get_segment_text_from_state(st, i);
            // ★ 时间戳需要加上段落的起始时间
            seg.start_time = voice_seg.start_time + (whisper_full_get_segment_t0_from_state(st, i) / 100.0);
            seg.end_time = voice_seg.start_time + (whisper_full_get_segment_t1_from_state(st, i) / 100.0);
            all_whisper_segments.push_back(seg);
        }

        std::cout << "[Whisper] 段落 " << (seg_idx + 1) << " 完成: "
                  << n_segments << " 个转录段，耗时 " << ms_infer << "ms" << std::endl;
    }

    std::cout << "[Whisper][Perf] total_infer=" << total_infer_ms << " ms" << std::endl;
    std::cout << "[Whisper] 原始转录段数: " << all_whisper_segments.size() << std::endl;

    // ★ 幻觉文本检测（常见模式）
    auto is_hallucination = [](const std::string& text) -> bool {
        // 常见的Whisper幻觉模式
        const std::vector<std::string> hallucination_patterns = {
            // 视频平台
            "优优独播", "优酷独播", "独播剧场", "YoYo Television",
            "Television Series Exclusive", "字幕", "Subtitle",
            "请不要相信", "仅供", "版权所有", "Copyright",
            "youtube.com", "bilibili.com", "订阅", "Subscribe",

            // ★ 音乐元数据幻觉（新增）
            "作曲", "作词", "编曲", "制作人", "混音", "母带",
            "出品", "发行", "制作公司", "录音", "音乐公司",
            "QQ音乐", "网易云音乐", "网易音乐", "酷狗音乐", "酷我音乐",
            "Apple Music", "Spotify", "JOOX",

            // ★ 常见重复幻觉词（新增）
            "好好好好", "哦哦哦哦", "啊啊啊啊",
            "嗯嗯嗯嗯", "呃呃呃呃",

            // ★ 艺人名字重复（当作元数据处理）
            "李宗盛", "周杰伦", "林俊杰", "陈奕迅", "邓紫棋"
        };

        // 转换为小写比较（对英文不区分大小写）
        std::string text_lower = text;
        std::transform(text_lower.begin(), text_lower.end(), text_lower.begin(), ::tolower);

        for (const auto& pattern : hallucination_patterns) {
            std::string pattern_lower = pattern;
            std::transform(pattern_lower.begin(), pattern_lower.end(), pattern_lower.begin(), ::tolower);

            if (text_lower.find(pattern_lower) != std::string::npos) {
                return true;
            }
        }

        // ★ 检测过短或过长的异常文本
        if (text.length() < 2 || text.length() > 200) {
            return true;
        }

        // ★ 检测重复字符（如"合合合合"）
        if (text.length() >= 4) {
            bool all_same = true;
            for (size_t i = 1; i < text.length(); ++i) {
                if (text[i] != text[0]) {
                    all_same = false;
                    break;
                }
            }
            if (all_same) return true;
        }

        return false;
    };

    // ★ 过滤幻觉文本
    std::vector<WhisperSegment> whisper_segments;
    for (const auto& seg : all_whisper_segments) {
        if (is_hallucination(seg.text)) {
            std::cout << "[Whisper] 过滤幻觉文本: \"" << seg.text << "\"" << std::endl;
            continue;
        }
        whisper_segments.push_back(seg);
    }

    std::cout << "[Whisper] 过滤后转录段数: " << whisper_segments.size() << std::endl;

    // ★★ 新的合并逻辑：基于 VAD 的人声段落，填充非人声段落为 [音乐] ★★
    segments.clear();
    // audio_duration 已在前面定义，这里直接使用
    double last_time = 0.0;
    size_t whisper_idx = 0;

    // 按时间顺序合并人声段落和非人声段落
    for (const auto& voice_seg : voice_segments) {
        // 如果有间隙（非人声段落），填充 [音乐]
        if (voice_seg.start_time - last_time > 1.0) {
            WhisperSegment gap;
            gap.start_time = last_time;
            gap.end_time = voice_seg.start_time;
            gap.text = "[音乐]";  // 纯音乐，没有人声
            segments.push_back(gap);
        }

        // 添加这个人声段落中的所有转录结果
        while (whisper_idx < whisper_segments.size() &&
               whisper_segments[whisper_idx].start_time < voice_seg.end_time) {
            segments.push_back(whisper_segments[whisper_idx]);
            whisper_idx++;
        }

        last_time = voice_seg.end_time;
    }

    // 处理结尾的非人声段落
    if (audio_duration - last_time > 1.0) {
        WhisperSegment gap;
        gap.start_time = last_time;
        gap.end_time = audio_duration;
        gap.text = "[音乐]";
        segments.push_back(gap);
    }

    std::cout << "[Merge] 合并完成: Whisper=" << whisper_segments.size()
              << ", 人声段=" << voice_segments.size()
              << ", 最终=" << segments.size() << " 段" << std::endl;

    // 打印最终结果
    for (size_t i = 0; i < segments.size(); ++i) {
        const auto& seg = segments[i];
        std::cout << "[Final] Segment " << i << ": ["
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
