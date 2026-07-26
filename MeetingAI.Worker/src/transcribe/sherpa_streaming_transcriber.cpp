#include "pch.h"
#include "sherpa_streaming_transcriber.h"
#include <sherpa-onnx/c-api/c-api.h>
#include <cstring>
#include <iostream>
#include <filesystem>
#include <unordered_map>

namespace meetingai::transcribe {

// ==================== Pimpl 实现结构体 ====================
struct SherpaStreamingTranscriber::Impl {
    // sherpa-onnx C API 的句柄一律以 const 指针形式返回并传入
    const SherpaOnnxOnlineRecognizer* recognizer = nullptr;
    // 同一个 recognizer（同一份模型）可以创建多个互相独立的 OnlineStream。
    // microphone/system 各自保存解码状态，既能同时识别又不会混音。
    std::unordered_map<std::string, const SherpaOnnxOnlineStream*> streams;
    std::string modelDir;
    std::string tokensPath;
    int sampleRate = 16000;
};

// ==================== 构造/析构 ====================

SherpaStreamingTranscriber::SherpaStreamingTranscriber()
    : m_impl(std::make_unique<Impl>())
    , m_initialized(false)
    , m_running(false)
    , m_sampleRate(16000)
{
}

SherpaStreamingTranscriber::~SherpaStreamingTranscriber()
{
    Stop();
}

// ==================== 初始化 ====================

bool SherpaStreamingTranscriber::Initialize(const std::string& modelDir,
                                             const std::string& tokensPath,
                                             int sampleRate,
                                             const std::string& hotwordsBuffer,
                                             const std::string& bpeVocabPath)
{
    if (m_initialized) {
        m_lastError = "Already initialized";
        return false;
    }

    m_impl->modelDir = modelDir;
    m_impl->tokensPath = tokensPath;
    m_impl->sampleRate = sampleRate;
    m_sampleRate = sampleRate;

    // 构建模型文件路径。
    // 优先 int8：encoder 173MB vs fp32 315MB，首次加载（含 onnxruntime 图优化）
    // 时间差一倍以上，中英混说场景精度损失通常可接受。
    // 设 MEETINGAI_SHERPA_FP32=1 可强制走 fp32。
    std::string suffix = ".int8.onnx";
    {
        bool forceFp32 = false;
        char* buf = nullptr; size_t len = 0;
        if (_dupenv_s(&buf, &len, "MEETINGAI_SHERPA_FP32") == 0 && buf) {
            forceFp32 = (std::string(buf) == "1");
            free(buf);
        }

        std::error_code ec;
        const std::string int8Encoder = modelDir + "\\encoder-epoch-99-avg-1.int8.onnx";
        if (forceFp32 || !std::filesystem::exists(int8Encoder, ec)) {
            suffix = ".onnx";
        }
    }

    std::string encoderPath = modelDir + "\\encoder-epoch-99-avg-1" + suffix;
    std::string decoderPath = modelDir + "\\decoder-epoch-99-avg-1" + suffix;
    std::string joinerPath = modelDir + "\\joiner-epoch-99-avg-1" + suffix;

    std::cout << "[Sherpa] encoder: " << encoderPath << std::endl;

    // 缺文件时 sherpa 内部可能直接 exit()，进程无声消失。先自查给出可读错误。
    {
        std::error_code ec;
        for (const auto* p : { &encoderPath, &decoderPath, &joinerPath }) {
            if (!std::filesystem::exists(*p, ec)) {
                m_lastError = "模型文件不存在: " + *p;
                return false;
            }
        }
        if (!std::filesystem::exists(tokensPath, ec)) {
            m_lastError = "tokens 文件不存在: " + tokensPath;
            return false;
        }
        if (!hotwordsBuffer.empty() &&
            !std::filesystem::exists(bpeVocabPath, ec)) {
            m_lastError = "热词 BPE 词表不存在: " + bpeVocabPath;
            return false;
        }
    }

    // 配置识别器
    SherpaOnnxOnlineRecognizerConfig config;
    memset(&config, 0, sizeof(config));

    // 设置 Transducer 模型路径
    config.model_config.transducer.encoder = encoderPath.c_str();
    config.model_config.transducer.decoder = decoderPath.c_str();
    config.model_config.transducer.joiner = joinerPath.c_str();

    // 设置 tokens 路径 (使用 bpe.model)
    config.model_config.tokens = tokensPath.c_str();
    config.model_config.num_threads = 2;
    config.model_config.provider = "cpu";
    config.model_config.debug = 0;

    // 设置特征提取参数
    config.feat_config.sample_rate = sampleRate;
    config.feat_config.feature_dim = 80;

    // Sherpa 的上下文热词只在 Transducer 的 modified_beam_search 下生效。
    // 没有绑定会议资料时继续走原来的 greedy_search，避免平白增加延迟。
    const bool hotwordsEnabled = !hotwordsBuffer.empty();
    config.decoding_method = hotwordsEnabled
        ? "modified_beam_search"
        : "greedy_search";
    config.max_active_paths = 4;
    config.enable_endpoint = 1;
    if (hotwordsEnabled) {
        config.model_config.modeling_unit = "cjkchar+bpe";
        config.model_config.bpe_vocab = bpeVocabPath.c_str();
        config.hotwords_score = 1.5f;
    }

    // 端点检测配置
    config.rule1_min_trailing_silence = 2.4f;
    config.rule2_min_trailing_silence = 1.2f;
    // 规则 3 只是保护超长流，不能拿它当字幕切句器。原来的 20 秒会在
    // 连续讲话时无条件从句子中间切断；提高到 60 秒后，最终显示层再用
    // 标点模型的一段前瞻做语义切句。
    config.rule3_min_utterance_length = 60.0f;

    // 创建识别器
    m_impl->recognizer = SherpaOnnxCreateOnlineRecognizer(&config);

    if (m_impl->recognizer == nullptr) {
        m_lastError = "Failed to create Sherpa-ONNX recognizer";
        return false;
    }

    m_initialized = true;
    m_lastError = "";
    return true;
}

// ==================== 会话管理 ====================

bool SherpaStreamingTranscriber::StartSession(
    const std::string& source,
    const std::string& hotwordsBuffer)
{
    if (!m_initialized) {
        m_lastError = "Not initialized";
        return false;
    }

    if (source.empty()) {
        m_lastError = "Source is empty";
        return false;
    }

    if (m_impl->streams.contains(source)) {
        m_lastError = "Source session already running: " + source;
        return false;
    }

    const SherpaOnnxOnlineStream* stream = nullptr;
    try {
        stream = hotwordsBuffer.empty()
            ? SherpaOnnxCreateOnlineStream(m_impl->recognizer)
            : SherpaOnnxCreateOnlineStreamWithHotwords(
                m_impl->recognizer,
                hotwordsBuffer.c_str());
    }
    catch (const std::exception& error) {
        m_lastError =
            "Sherpa 热词解析失败: " + std::string(error.what());
        return false;
    }
    catch (...) {
        m_lastError = "Sherpa 热词解析失败: 未知错误";
        return false;
    }

    if (stream == nullptr) {
        m_lastError = "Failed to create stream for source: " + source;
        return false;
    }

    m_impl->streams.emplace(source, stream);
    m_running = true;
    m_lastError = "";
    return true;
}

// ==================== 音频处理 ====================

bool SherpaStreamingTranscriber::AcceptWaveform(const std::string& source,
                                                 const float* samples,
                                                 int numSamples,
                                                 std::vector<SherpaStreamResult>& results)
{
    const auto streamIt = m_impl->streams.find(source);
    if (!m_running || streamIt == m_impl->streams.end() ||
        streamIt->second == nullptr) {
        m_lastError = "Source session is not running: " + source;
        return false;
    }
    const SherpaOnnxOnlineStream* stream = streamIt->second;

    results.clear();

    // 接受音频数据
    SherpaOnnxOnlineStreamAcceptWaveform(stream, m_sampleRate, samples, numSamples);

    // 检查是否准备好解码
    while (SherpaOnnxIsOnlineStreamReady(m_impl->recognizer, stream)) {
        SherpaOnnxDecodeOnlineStream(m_impl->recognizer, stream);
    }

    // 获取部分结果 (partial result)
    const SherpaOnnxOnlineRecognizerResult* result =
        SherpaOnnxGetOnlineStreamResult(m_impl->recognizer, stream);

    if (result != nullptr) {
        if (result->text != nullptr && strlen(result->text) > 0) {
            SherpaStreamResult partialResult;
            partialResult.text = result->text;
            partialResult.is_final = false;
            partialResult.speaker_id = -1;
            partialResult.confidence = 0.0f; // Sherpa-ONNX 不提供置信度

            results.push_back(partialResult);
        }
        SherpaOnnxDestroyOnlineRecognizerResult(result);
    }

    // 检查端点（是否完成一句话）
    if (SherpaOnnxOnlineStreamIsEndpoint(m_impl->recognizer, stream)) {
        // 获取最终结果
        const SherpaOnnxOnlineRecognizerResult* finalResult =
            SherpaOnnxGetOnlineStreamResult(m_impl->recognizer, stream);

        SherpaStreamResult endpointResult;
        endpointResult.is_final = true;
        endpointResult.endpoint_detected = true;
        endpointResult.speaker_id = -1;
        endpointResult.confidence = 1.0f;

        if (finalResult != nullptr) {
            if (finalResult->text != nullptr && strlen(finalResult->text) > 0) {
                endpointResult.text = finalResult->text;
            }
            SherpaOnnxDestroyOnlineRecognizerResult(finalResult);
        }

        // 即使 endpoint 没有文字也要上报。它表示 reset 后又经历了 rule1
        // 的长静音，Worker 用这个信号把仍在等待语义前瞻的文本强制收尾。
        results.push_back(std::move(endpointResult));

        // 重置流以准备下一句话
        SherpaOnnxOnlineStreamReset(m_impl->recognizer, stream);
    }

    return true;
}

// ==================== 结束会话 ====================

bool SherpaStreamingTranscriber::EndSession(
    const std::string& source,
    std::vector<SherpaStreamResult>& finalResults)
{
    const auto streamIt = m_impl->streams.find(source);
    if (streamIt == m_impl->streams.end() || streamIt->second == nullptr) {
        m_lastError = "Source session is not running: " + source;
        return false;
    }
    const SherpaOnnxOnlineStream* stream = streamIt->second;

    finalResults.clear();

    if (stream != nullptr) {
        // 输入结束信号
        SherpaOnnxOnlineStreamInputFinished(stream);

        // 解码剩余数据
        while (SherpaOnnxIsOnlineStreamReady(m_impl->recognizer, stream)) {
            SherpaOnnxDecodeOnlineStream(m_impl->recognizer, stream);
        }

        // 获取最终结果
        const SherpaOnnxOnlineRecognizerResult* result =
            SherpaOnnxGetOnlineStreamResult(m_impl->recognizer, stream);

        if (result != nullptr) {
            if (result->text != nullptr && strlen(result->text) > 0) {
                SherpaStreamResult finalResult;
                finalResult.text = result->text;
                finalResult.is_final = true;
                finalResult.endpoint_detected = true;
                finalResult.speaker_id = -1;
                finalResult.confidence = 1.0f;

                finalResults.push_back(finalResult);
            }
            SherpaOnnxDestroyOnlineRecognizerResult(result);
        }

        // 销毁流
        SherpaOnnxDestroyOnlineStream(stream);
    }

    m_impl->streams.erase(streamIt);
    m_running = !m_impl->streams.empty();
    return true;
}

bool SherpaStreamingTranscriber::IsRunning(const std::string& source) const
{
    const auto it = m_impl->streams.find(source);
    return it != m_impl->streams.end() && it->second != nullptr;
}

// ==================== 停止 ====================

void SherpaStreamingTranscriber::Stop()
{
    for (const auto& [source, stream] : m_impl->streams) {
        (void)source;
        if (stream != nullptr) {
            SherpaOnnxDestroyOnlineStream(stream);
        }
    }
    m_impl->streams.clear();

    if (m_impl->recognizer != nullptr) {
        SherpaOnnxDestroyOnlineRecognizer(m_impl->recognizer);
        m_impl->recognizer = nullptr;
    }

    m_initialized = false;
    m_running = false;
}

} // namespace meetingai::transcribe
