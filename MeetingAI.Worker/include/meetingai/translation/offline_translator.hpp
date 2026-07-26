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

    // mode: off、auto、to_zh 或 to_en。
    // 模型只在第一次使用对应方向时加载，之后的会议会直接复用。
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
