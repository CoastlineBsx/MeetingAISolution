#pragma once
#include <string>
#include <memory>

namespace meetingai::transcribe {

/// <summary>
/// 中英双语标点恢复（sherpa-onnx CT-Transformer）。
///
/// 名字里的 "Offline" 只表示需要一整句文本而不是音频流，实际耗时在毫秒级，
/// 对每个定稿的 segment 调用一次完全可以实时使用。标点在工业界一贯是独立的
/// 后处理模型，不属于声学模型。
/// </summary>
class Punctuator {
public:
    Punctuator();
    ~Punctuator();

    /// <summary>
    /// 加载模型。
    /// </summary>
    /// <param name="modelDir">模型目录（含 model.int8.onnx 与 tokens.json）</param>
    /// <returns>成功返回 true；失败时 GetLastError() 给出原因</returns>
    bool Initialize(const std::string& modelDir);

    /// <summary>
    /// 给文本加标点。未初始化或失败时原样返回输入，保证调用方永远拿得到可用文本。
    /// </summary>
    std::string AddPunctuation(const std::string& text) const;

    bool IsInitialized() const { return m_initialized; }
    std::string GetLastError() const { return m_lastError; }

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;

    bool m_initialized;
    mutable std::string m_lastError;

    Punctuator(const Punctuator&) = delete;
    Punctuator& operator=(const Punctuator&) = delete;
};

} // namespace meetingai::transcribe
