#pragma once

#include <functional>
#include <memory>
#include <string>

namespace meetingai::translation {

struct TranslationEvent {
    std::string source;
    long long utteranceId = 0;
    std::string text;
    bool isFinal = false;
    std::string targetLanguage;
};

class OfflineTranslator {
public:
    using ResultCallback = std::function<void(const TranslationEvent&)>;

    OfflineTranslator();
    ~OfflineTranslator();

    OfflineTranslator(const OfflineTranslator&) = delete;
    OfflineTranslator& operator=(const OfflineTranslator&) = delete;

    // 模型生命周期只允许由 Startup 页面触发。会议开始只启动翻译队列，
    // 不再隐式读取模型文件。
    bool LoadDirection(
        const std::string& direction,
        const std::string& modelDir);
    bool UnloadDirection(const std::string& direction);
    bool IsDirectionLoaded(const std::string& direction) const;

    // mode: off、auto、to_zh 或 to_en。
    // 所需方向必须已经通过 LoadDirection 加载。
    bool Start(
        const std::string& mode,
        const std::string& englishToChineseModelDir,
        const std::string& chineseToEnglishModelDir,
        ResultCallback callback);

    // Partial 只保留每个来源的最新版本并做节流；final 始终按顺序处理。
    void Submit(
        const std::string& source,
        long long utteranceId,
        const std::string& text,
        bool isFinal);

    // drainFinals=true 时会丢弃未完成的 partial，但等待所有 final 翻译完成。
    void Stop(bool drainFinals);

    bool IsActive() const;
    std::string GetLastError() const;

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace meetingai::translation
