#include "whisper_openvino_transcriber.hpp"
#include <iostream>
#include <filesystem>
#include <chrono>
#include <fstream>
#include <memory>
#include <mutex>
#include "openvino/genai/whisper_pipeline.hpp"

namespace fs = std::filesystem;

// 声明外部函数：复用现有的 WAV 加载函数（在 whisper_transcriber.cpp 中）
// 该函数已经实现了：16kHz 重采样 + 单声道转换 + 音频增强
extern bool LoadWavFile(const std::string& filename, std::vector<float>& audio_data);

namespace meetingai::transcribe {

// 全局模型实例（单例模式）
static std::unique_ptr<ov::genai::WhisperPipeline> g_pipeline;
static std::mutex g_pipeline_mutex;
static bool g_pipeline_loaded = false;

bool LoadWhisperOpenVINOModel(const std::string& modelPath, const std::string& device) {
    std::lock_guard<std::mutex> lock(g_pipeline_mutex);

    if (g_pipeline_loaded && g_pipeline) {
        std::cout << "[OpenVINO Whisper] 模型已加载" << std::endl;
        return true;
    }

    try {
        std::cout << "[OpenVINO Whisper] 正在加载模型: " << modelPath << std::endl;
        std::cout << "[OpenVINO Whisper] 设备: " << device << std::endl;
        g_pipeline = std::make_unique<ov::genai::WhisperPipeline>(modelPath, device);
        g_pipeline_loaded = true;
        std::cout << "[OpenVINO Whisper] 模型加载成功" << std::endl;
        return true;
    }
    catch (const std::exception& ex) {
        std::cerr << "[OpenVINO Whisper] 模型加载失败: " << ex.what() << std::endl;
        g_pipeline.reset();
        g_pipeline_loaded = false;
        return false;
    }
}

void UnloadWhisperOpenVINOModel() {
    std::lock_guard<std::mutex> lock(g_pipeline_mutex);
    g_pipeline.reset();
    g_pipeline_loaded = false;
    std::cout << "[OpenVINO Whisper] 模型已卸载" << std::endl;
}

bool IsWhisperOpenVINOModelLoaded() {
    std::lock_guard<std::mutex> lock(g_pipeline_mutex);
    return g_pipeline_loaded && g_pipeline != nullptr;
}

bool TranscribeAudioFileOpenVINO(
    const std::string& modelPath,
    const std::string& audioPath,
    std::vector<WhisperOpenVINOSegment>& segments,
    const std::string& language,
    ProgressCallback progressCallback
) {
    try {
        std::cout << "\n========================================" << std::endl;
        std::cout << "   OpenVINO Whisper 转录开始" << std::endl;
        std::cout << "========================================\n" << std::endl;

        // ========== 1. 报告进度：开始加载音频 ==========
        if (progressCallback) progressCallback(5);

        // 加载音频文件（复用现有函数）
        std::vector<float> audio_data;
        if (!LoadWavFile(audioPath, audio_data)) {
            std::cerr << "[OpenVINO] 音频加载失败: " << audioPath << std::endl;
            return false;
        }

        float duration = audio_data.size() / 16000.0f;
        std::cout << "[OpenVINO] 音频加载成功:" << std::endl;
        std::cout << "  - 时长: " << duration << " 秒" << std::endl;
        std::cout << "  - 采样点: " << audio_data.size() << std::endl;

        // ========== 2. 报告进度：加载模型 ==========
        if (progressCallback) progressCallback(10);

        // 检查模型路径
        if (!fs::exists(modelPath)) {
            std::cerr << "[OpenVINO] 模型路径不存在: " << modelPath << std::endl;
            return false;
        }

        std::cout << "\n[OpenVINO] 加载模型..." << std::endl;
        std::cout << "  - 模型路径: " << modelPath << std::endl;
        std::cout << "  - 设备: CPU" << std::endl;

        auto load_start = std::chrono::high_resolution_clock::now();
        ov::genai::WhisperPipeline pipeline(modelPath, "CPU");
        auto load_end = std::chrono::high_resolution_clock::now();
        auto load_time = std::chrono::duration_cast<std::chrono::milliseconds>(load_end - load_start).count();

        std::cout << "  ✓ 模型加载完成 (" << load_time << " ms)" << std::endl;

        // ========== 3. 报告进度：配置参数 ==========
        if (progressCallback) progressCallback(20);

        // 配置生成参数
        ov::genai::WhisperGenerationConfig config(fs::path(modelPath) / "generation_config.json");

        // ⭐ 核心配置：启用 DTW timestamps
        config.return_timestamps = true;
        config.max_new_tokens = 100;
        config.task = "transcribe";

        // 语言配置
        if (language != "auto" && !language.empty()) {
            config.language = "<|" + language + "|>";
            std::cout << "\n[OpenVINO] 配置:" << std::endl;
            std::cout << "  - 语言: " << language << std::endl;
        } else {
            std::cout << "\n[OpenVINO] 配置:" << std::endl;
            std::cout << "  - 语言: 自动检测" << std::endl;
        }
        std::cout << "  - 时间戳: 启用 (DTW)" << std::endl;
        std::cout << "  - 最大 tokens: " << config.max_new_tokens << std::endl;

        // ========== 4. 报告进度：开始推理 ==========
        if (progressCallback) progressCallback(30);

        std::cout << "\n[OpenVINO] 开始推理..." << std::endl;
        std::cout << "  (首次运行可能需要 10-30 秒编译模型)" << std::endl;

        auto infer_start = std::chrono::high_resolution_clock::now();

        // 执行推理
        ov::genai::RawSpeechInput raw_speech(audio_data.begin(), audio_data.end());
        auto result = pipeline.generate(raw_speech, config);

        auto infer_end = std::chrono::high_resolution_clock::now();
        auto infer_time = std::chrono::duration_cast<std::chrono::milliseconds>(infer_end - infer_start).count();

        std::cout << "  ✓ 推理完成 (" << infer_time << " ms)" << std::endl;

        // ========== 5. 报告进度：处理结果 ==========
        if (progressCallback) progressCallback(90);

        // 提取结果
        std::cout << "\n[OpenVINO] 转录文本:" << std::endl;
        std::cout << "  \"" << result << "\"\n" << std::endl;

        // 提取时间戳分段
        if (result.chunks && !result.chunks->empty()) {
            std::cout << "[OpenVINO] 时间戳分段: " << result.chunks->size() << " 个\n" << std::endl;

            for (size_t i = 0; i < result.chunks->size(); ++i) {
                const auto& chunk = (*result.chunks)[i];

                WhisperOpenVINOSegment seg;
                seg.start_ts = chunk.start_ts;
                seg.end_ts = chunk.end_ts;
                seg.text = chunk.text;
                segments.push_back(seg);

                // 打印分段信息
                std::cout << "  [" << i << "] "
                          << std::fixed << std::setprecision(2)
                          << seg.start_ts << "s - " << seg.end_ts << "s"
                          << " (" << (seg.end_ts - seg.start_ts) << "s): "
                          << "\"" << seg.text << "\"" << std::endl;
            }
        } else {
            std::cout << "[OpenVINO] ⚠️ 未检测到时间戳" << std::endl;

            // 如果没有时间戳，创建单个分段
            WhisperOpenVINOSegment seg;
            seg.start_ts = 0.0f;
            seg.end_ts = duration;
            seg.text = std::string(result);
            segments.push_back(seg);
        }

        // ========== 6. 报告进度：完成 ==========
        if (progressCallback) progressCallback(100);

        // 性能统计
        float rtf = (infer_time / 1000.0f) / duration;
        std::cout << "\n========================================" << std::endl;
        std::cout << "   性能统计" << std::endl;
        std::cout << "========================================" << std::endl;
        std::cout << "  音频时长: " << duration << " 秒" << std::endl;
        std::cout << "  推理耗时: " << infer_time << " ms" << std::endl;
        std::cout << "  实时系数: " << std::fixed << std::setprecision(2) << rtf << "x" << std::endl;
        std::cout << "  分段数量: " << segments.size() << std::endl;
        std::cout << "========================================\n" << std::endl;

        return true;

    } catch (const std::exception& e) {
        std::cerr << "\n[OpenVINO] ❌ 异常: " << e.what() << std::endl;
        return false;
    } catch (...) {
        std::cerr << "\n[OpenVINO] ❌ 未知异常" << std::endl;
        return false;
    }
}

} // namespace meetingai::transcribe
