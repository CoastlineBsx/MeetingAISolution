#pragma once
#include <string>
#include <vector>
#include <memory>

namespace meetingai::transcribe {

/// <summary>
/// Sherpa-ONNX 实时流式转录结果
/// </summary>
struct SherpaStreamResult {
    std::string text;           // 转录文本
    bool is_final;              // 是否是最终结果（true=final, false=partial）
    bool endpoint_detected;     // 是否由 Sherpa endpoint 触发（空文本表示长静音）
    int speaker_id;             // 说话人ID（-1表示未识别）
    float confidence;           // 置信度（0.0-1.0）

    SherpaStreamResult()
        : text(""), is_final(false), endpoint_detected(false),
          speaker_id(-1), confidence(0.0f) {}
};

/// <summary>
/// Sherpa-ONNX 实时流式转录器
/// 使用 Zipformer 模型进行中英双语实时转录
/// </summary>
class SherpaStreamingTranscriber {
public:
    SherpaStreamingTranscriber();
    ~SherpaStreamingTranscriber();

    /// <summary>
    /// 初始化转录器
    /// </summary>
    /// <param name="modelDir">模型目录路径（包含 encoder/decoder/joiner ONNX 文件）</param>
    /// <param name="tokensPath">Tokens 文件路径（bpe.vocab 或 tokens.txt）</param>
    /// <param name="sampleRate">音频采样率（默认 16000）</param>
    /// <returns>成功返回 true</returns>
    bool Initialize(const std::string& modelDir,
                    const std::string& tokensPath,
                    int sampleRate = 16000);

    /// <summary>
    /// 开始流式转录会话
    /// </summary>
    /// <returns>成功返回 true</returns>
    bool StartSession(const std::string& source);

    /// <summary>
    /// 接受音频流数据（PCM 16-bit, mono, 16kHz）
    /// </summary>
    /// <param name="samples">音频样本数据（float32, [-1.0, 1.0]）</param>
    /// <param name="numSamples">样本数量</param>
    /// <param name="results">输出结果列表（可能包含 partial 和 final 结果）</param>
    /// <returns>成功返回 true</returns>
    bool AcceptWaveform(const std::string& source,
                        const float* samples,
                        int numSamples,
                        std::vector<SherpaStreamResult>& results);

    /// <summary>
    /// 结束流式转录会话（获取最终结果）
    /// </summary>
    /// <param name="finalResults">输出最终结果列表</param>
    /// <returns>成功返回 true</returns>
    bool EndSession(const std::string& source,
                    std::vector<SherpaStreamResult>& finalResults);

    /// <summary>
    /// 停止转录器（释放资源）
    /// </summary>
    void Stop();

    /// <summary>
    /// 检查转录器是否已初始化
    /// </summary>
    bool IsInitialized() const { return m_initialized; }

    /// <summary>
    /// 检查转录器是否正在运行
    /// </summary>
    bool IsRunning() const { return m_running; }
    bool IsRunning(const std::string& source) const;

    /// <summary>
    /// 获取最后一次错误消息
    /// </summary>
    std::string GetLastError() const { return m_lastError; }

private:
    // Sherpa-ONNX 内部实现（前向声明，避免头文件依赖）
    struct Impl;
    std::unique_ptr<Impl> m_impl;

    bool m_initialized;
    bool m_running;
    std::string m_lastError;
    int m_sampleRate;

    // 禁用拷贝
    SherpaStreamingTranscriber(const SherpaStreamingTranscriber&) = delete;
    SherpaStreamingTranscriber& operator=(const SherpaStreamingTranscriber&) = delete;
};

} // namespace meetingai::transcribe
