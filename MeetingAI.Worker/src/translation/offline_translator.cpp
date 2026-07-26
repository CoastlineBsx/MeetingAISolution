#include "translation/offline_translator.hpp"

#ifdef MEETINGAI_TRANSLATION_DISABLED

#include <utility>

namespace meetingai::translation {

class OfflineTranslator::Impl {
public:
    bool Start(
        const std::string& mode,
        const std::string&,
        const std::string&,
        ResultCallback) {
        active_ = false;
        lastError_ = mode == "off"
            ? std::string{}
            : "Debug 构建未启用离线翻译；请使用 Release x64";
        return mode == "off";
    }

    void Submit(
        const std::string&,
        long long,
        const std::string&,
        bool) {
    }

    void Stop(bool) {
        active_ = false;
    }

    bool IsActive() const {
        return active_;
    }

    std::string GetLastError() const {
        return lastError_;
    }

private:
    bool active_ = false;
    std::string lastError_;
};

OfflineTranslator::OfflineTranslator()
    : impl_(std::make_unique<Impl>()) {
}

OfflineTranslator::~OfflineTranslator() = default;

bool OfflineTranslator::Start(
    const std::string& mode,
    const std::string& englishToChineseModelDir,
    const std::string& chineseToEnglishModelDir,
    ResultCallback callback) {
    return impl_->Start(
        mode,
        englishToChineseModelDir,
        chineseToEnglishModelDir,
        std::move(callback));
}

void OfflineTranslator::Submit(
    const std::string& source,
    long long utteranceId,
    const std::string& text,
    bool isFinal) {
    impl_->Submit(source, utteranceId, text, isFinal);
}

void OfflineTranslator::Stop(bool drainFinals) {
    impl_->Stop(drainFinals);
}

bool OfflineTranslator::IsActive() const {
    return impl_->IsActive();
}

std::string OfflineTranslator::GetLastError() const {
    return impl_->GetLastError();
}

} // namespace meetingai::translation

#else

#include <ctranslate2/translator.h>
#include <sentencepiece_processor.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace meetingai::translation {
namespace {

constexpr auto kPartialInterval = std::chrono::milliseconds(450);

enum class Mode {
    Off,
    Auto,
    ToChinese,
    ToEnglish
};

enum class TextLanguage {
    Chinese,
    English
};

Mode ParseMode(const std::string& value) {
    if (value == "auto") return Mode::Auto;
    if (value == "to_zh") return Mode::ToChinese;
    if (value == "to_en") return Mode::ToEnglish;
    return Mode::Off;
}

TextLanguage DetectLanguage(const std::string& text) {
    int chineseCount = 0;
    int latinCount = 0;

    for (size_t i = 0; i < text.size();) {
        const unsigned char lead = static_cast<unsigned char>(text[i]);
        uint32_t cp = lead;
        size_t width = 1;

        if ((lead & 0xE0) == 0xC0 && i + 1 < text.size()) {
            cp = ((lead & 0x1F) << 6)
                | (static_cast<unsigned char>(text[i + 1]) & 0x3F);
            width = 2;
        }
        else if ((lead & 0xF0) == 0xE0 && i + 2 < text.size()) {
            cp = ((lead & 0x0F) << 12)
                | ((static_cast<unsigned char>(text[i + 1]) & 0x3F) << 6)
                | (static_cast<unsigned char>(text[i + 2]) & 0x3F);
            width = 3;
        }
        else if ((lead & 0xF8) == 0xF0 && i + 3 < text.size()) {
            cp = ((lead & 0x07) << 18)
                | ((static_cast<unsigned char>(text[i + 1]) & 0x3F) << 12)
                | ((static_cast<unsigned char>(text[i + 2]) & 0x3F) << 6)
                | (static_cast<unsigned char>(text[i + 3]) & 0x3F);
            width = 4;
        }

        const bool isCjk =
            (cp >= 0x3400 && cp <= 0x4DBF)
            || (cp >= 0x4E00 && cp <= 0x9FFF)
            || (cp >= 0xF900 && cp <= 0xFAFF);
        if (isCjk) {
            ++chineseCount;
        }
        else if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) {
            ++latinCount;
        }

        i += width;
    }

    // 中文句子里常会夹英文产品名；只要中文字符达到一定占比，仍按中文处理。
    return chineseCount > 0
        && (latinCount == 0 || chineseCount * 4 >= latinCount)
        ? TextLanguage::Chinese
        : TextLanguage::English;
}

std::string Trim(std::string value) {
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

struct DirectionModel {
    std::unique_ptr<ctranslate2::Translator> translator;
    sentencepiece::SentencePieceProcessor sourceTokenizer;
    sentencepiece::SentencePieceProcessor targetTokenizer;
    bool addSimplifiedChineseTargetToken = false;

    bool Load(
        const std::string& modelDir,
        bool addTargetToken,
        std::string& error) {
        if (translator) return true;

        const fs::path root(modelDir);
        const fs::path modelFile = root / "model.bin";
        const fs::path sourceSpm = root / "source.spm";
        const fs::path targetSpm = root / "target.spm";

        std::error_code ec;
        if (!fs::is_regular_file(modelFile, ec)
            || !fs::is_regular_file(sourceSpm, ec)
            || !fs::is_regular_file(targetSpm, ec)) {
            error = "翻译模型文件不完整: " + modelDir;
            return false;
        }

        const auto sourceStatus = sourceTokenizer.Load(sourceSpm.string());
        if (!sourceStatus.ok()) {
            error = "加载 source.spm 失败: " + sourceStatus.ToString();
            return false;
        }

        const auto targetStatus = targetTokenizer.Load(targetSpm.string());
        if (!targetStatus.ok()) {
            error = "加载 target.spm 失败: " + targetStatus.ToString();
            return false;
        }

        try {
            ctranslate2::ReplicaPoolConfig poolConfig;
            // 给 Sherpa 和音频线程留足 CPU；实时翻译以低延迟而不是吞吐量优先。
            poolConfig.num_threads_per_replica = 2;
            poolConfig.max_queued_batches = 1;

            translator = std::make_unique<ctranslate2::Translator>(
                modelDir,
                ctranslate2::Device::CPU,
                ctranslate2::ComputeType::DEFAULT,
                std::vector<int>{0},
                false,
                poolConfig);
            addSimplifiedChineseTargetToken = addTargetToken;
            return true;
        }
        catch (const std::exception& e) {
            error = std::string("加载 CTranslate2 模型失败: ") + e.what();
            translator.reset();
            return false;
        }
    }

    std::string Translate(const std::string& text) {
        std::vector<std::string> pieces;
        const auto encodeStatus = sourceTokenizer.Encode(text, &pieces);
        if (!encodeStatus.ok()) {
            throw std::runtime_error(
                "SentencePiece 编码失败: " + encodeStatus.ToString());
        }

        // opus-mt-en-zh 是多目标中文模型。MarianTokenizer 会把这个语言
        // 标记作为一个完整词元放在句首，不能让 SentencePiece 把它拆开。
        if (addSimplifiedChineseTargetToken) {
            pieces.insert(pieces.begin(), ">>cmn_Hans<<");
        }
        // MarianTokenizer 会自动在源序列末尾追加 EOS。直接使用
        // SentencePiece 时必须手工补上，否则长句容易拖尾和重复。
        pieces.push_back("</s>");

        ctranslate2::TranslationOptions options;
        options.beam_size = 1;
        options.num_hypotheses = 1;
        options.max_decoding_length = 160;
        options.repetition_penalty = 1.05f;
        options.no_repeat_ngram_size = 3;

        auto results = translator->translate_batch({pieces}, options);
        if (results.empty() || results.front().hypotheses.empty()) {
            throw std::runtime_error("CTranslate2 没有返回翻译结果");
        }

        std::string output;
        const auto decodeStatus = targetTokenizer.Decode(
            results.front().hypotheses.front(),
            &output);
        if (!decodeStatus.ok()) {
            throw std::runtime_error(
                "SentencePiece 解码失败: " + decodeStatus.ToString());
        }
        return Trim(std::move(output));
    }
};

} // namespace

class OfflineTranslator::Impl {
public:
    struct Request {
        std::string source;
        long long utteranceId = 0;
        std::string text;
        bool isFinal = false;
        uint64_t revision = 0;
        std::chrono::steady_clock::time_point due;
    };

    ~Impl() {
        Stop(false);
    }

    bool Start(
        const std::string& modeValue,
        const std::string& englishToChineseModelDir,
        const std::string& chineseToEnglishModelDir,
        ResultCallback resultCallback) {
        Stop(false);

        const Mode requestedMode = ParseMode(modeValue);
        if (requestedMode == Mode::Off) {
            std::lock_guard<std::mutex> lock(mutex_);
            mode_ = Mode::Off;
            callback_ = std::move(resultCallback);
            lastError_.clear();
            return true;
        }

        std::string loadError;
        if ((requestedMode == Mode::Auto || requestedMode == Mode::ToChinese)
            && !englishToChinese_.Load(
                englishToChineseModelDir,
                true,
                loadError)) {
            std::lock_guard<std::mutex> lock(mutex_);
            lastError_ = std::move(loadError);
            return false;
        }

        if ((requestedMode == Mode::Auto || requestedMode == Mode::ToEnglish)
            && !chineseToEnglish_.Load(
                chineseToEnglishModelDir,
                false,
                loadError)) {
            std::lock_guard<std::mutex> lock(mutex_);
            lastError_ = std::move(loadError);
            return false;
        }

        {
            std::lock_guard<std::mutex> lock(mutex_);
            mode_ = requestedMode;
            callback_ = std::move(resultCallback);
            stopping_ = false;
            active_ = true;
            busy_ = false;
            lastError_.clear();
            finals_.clear();
            partials_.clear();
            latestRevision_.clear();
            lastPublishedRevision_.clear();
            nextPartialAllowed_.clear();
        }

        worker_ = std::thread([this] { WorkerLoop(); });
        return true;
    }

    void Submit(
        const std::string& source,
        long long utteranceId,
        const std::string& text,
        bool isFinal) {
        if (text.empty()) return;

        std::lock_guard<std::mutex> lock(mutex_);
        if (!active_ || mode_ == Mode::Off) return;

        const uint64_t revision = ++latestRevision_[source];
        Request request{
            source,
            utteranceId,
            text,
            isFinal,
            revision,
            std::chrono::steady_clock::now()
        };

        if (isFinal) {
            // final 覆盖同一来源尚未处理的 partial，并且绝不被节流丢弃。
            partials_.erase(source);
            finals_.push_back(std::move(request));
        }
        else {
            const auto now = std::chrono::steady_clock::now();
            const auto nextIt = nextPartialAllowed_.find(source);
            if (nextIt != nextPartialAllowed_.end() && nextIt->second > now) {
                request.due = nextIt->second;
            }
            partials_[source] = std::move(request);
        }
        workReady_.notify_one();
    }

    void Stop(bool drainFinals) {
        std::thread threadToJoin;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            if (!worker_.joinable()) {
                active_ = false;
                callback_ = {};
                return;
            }

            // partial 是临时画面，不值得阻塞停止；final 必须完整交付。
            partials_.clear();
            if (drainFinals) {
                idle_.wait(lock, [this] {
                    return finals_.empty() && !busy_;
                });
            }
            else {
                finals_.clear();
            }

            stopping_ = true;
            active_ = false;
            workReady_.notify_all();
            threadToJoin = std::move(worker_);
        }

        if (threadToJoin.joinable()) {
            threadToJoin.join();
        }

        std::lock_guard<std::mutex> lock(mutex_);
        stopping_ = false;
        busy_ = false;
        finals_.clear();
        partials_.clear();
        callback_ = {};
    }

    bool IsActive() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return active_;
    }

    std::string GetLastError() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return lastError_;
    }

private:
    std::optional<Request> TakeNextRequest(
        std::unique_lock<std::mutex>& lock) {
        while (true) {
            if (!finals_.empty()) {
                Request request = std::move(finals_.front());
                finals_.pop_front();
                busy_ = true;
                return request;
            }

            if (stopping_) return std::nullopt;

            if (partials_.empty()) {
                workReady_.wait(lock, [this] {
                    return stopping_ || !finals_.empty() || !partials_.empty();
                });
                continue;
            }

            auto earliest = std::min_element(
                partials_.begin(),
                partials_.end(),
                [](const auto& a, const auto& b) {
                    return a.second.due < b.second.due;
                });
            const auto now = std::chrono::steady_clock::now();
            if (earliest->second.due > now) {
                workReady_.wait_until(lock, earliest->second.due);
                continue;
            }

            Request request = std::move(earliest->second);
            partials_.erase(earliest);
            nextPartialAllowed_[request.source] = now + kPartialInterval;
            busy_ = true;
            return request;
        }
    }

    void WorkerLoop() {
        while (true) {
            std::optional<Request> request;
            Mode mode;
            {
                std::unique_lock<std::mutex> lock(mutex_);
                request = TakeNextRequest(lock);
                if (!request.has_value()) break;
                mode = mode_;
            }

            TranslationEvent event;
            event.source = request->source;
            event.utteranceId = request->utteranceId;
            event.isFinal = request->isFinal;
            bool hasResult = false;
            std::string error;

            try {
                const TextLanguage language = DetectLanguage(request->text);
                DirectionModel* direction = nullptr;

                if (mode == Mode::Auto) {
                    if (language == TextLanguage::Chinese) {
                        direction = &chineseToEnglish_;
                        event.targetLanguage = "en";
                    }
                    else {
                        direction = &englishToChinese_;
                        event.targetLanguage = "zh";
                    }
                }
                else if (mode == Mode::ToChinese
                         && language == TextLanguage::English) {
                    direction = &englishToChinese_;
                    event.targetLanguage = "zh";
                }
                else if (mode == Mode::ToEnglish
                         && language == TextLanguage::Chinese) {
                    direction = &chineseToEnglish_;
                    event.targetLanguage = "en";
                }

                if (direction != nullptr) {
                    event.text = direction->Translate(request->text);
                    hasResult = !event.text.empty();
                }
            }
            catch (const std::exception& e) {
                error = e.what();
            }

            ResultCallback callback;
            bool publish = true;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (!request->isFinal) {
                    // partial 推理期间通常还会收到更长的新版本。旧实现只要
                    // latestRevision 已变化就丢弃刚翻完的结果，连续讲话时
                    // 因而几乎永远没有中文更新。现在只拦截真正倒退的结果：
                    // 已完成的中间译文可以先显示，队列仍只保留最新输入，
                    // 随后的 partial/final 会继续覆盖和校正。
                    const uint64_t lastPublished =
                        lastPublishedRevision_[request->source];
                    publish = request->revision > lastPublished;
                    if (publish && hasResult && error.empty()) {
                        lastPublishedRevision_[request->source] =
                            request->revision;
                    }
                }
                else if (hasResult && error.empty()) {
                    lastPublishedRevision_[request->source] =
                        std::max(
                            lastPublishedRevision_[request->source],
                            request->revision);
                }
                if (!error.empty()) {
                    lastError_ = error;
                }
                callback = callback_;
                busy_ = false;
                idle_.notify_all();
            }

            if (callback && publish) {
                if (!error.empty()) {
                    TranslationEvent errorEvent = event;
                    errorEvent.text.clear();
                    errorEvent.targetLanguage = "error:" + error;
                    callback(errorEvent);
                }
                else if (hasResult) {
                    callback(event);
                }
            }
        }

        std::lock_guard<std::mutex> lock(mutex_);
        busy_ = false;
        idle_.notify_all();
    }

    mutable std::mutex mutex_;
    std::condition_variable workReady_;
    std::condition_variable idle_;
    std::thread worker_;
    bool active_ = false;
    bool stopping_ = false;
    bool busy_ = false;
    Mode mode_ = Mode::Off;
    ResultCallback callback_;
    std::string lastError_;

    DirectionModel englishToChinese_;
    DirectionModel chineseToEnglish_;
    std::deque<Request> finals_;
    std::unordered_map<std::string, Request> partials_;
    std::unordered_map<std::string, uint64_t> latestRevision_;
    std::unordered_map<std::string, uint64_t> lastPublishedRevision_;
    std::unordered_map<
        std::string,
        std::chrono::steady_clock::time_point> nextPartialAllowed_;
};

OfflineTranslator::OfflineTranslator()
    : impl_(std::make_unique<Impl>()) {
}

OfflineTranslator::~OfflineTranslator() = default;

bool OfflineTranslator::Start(
    const std::string& mode,
    const std::string& englishToChineseModelDir,
    const std::string& chineseToEnglishModelDir,
    ResultCallback callback) {
    return impl_->Start(
        mode,
        englishToChineseModelDir,
        chineseToEnglishModelDir,
        std::move(callback));
}

void OfflineTranslator::Submit(
    const std::string& source,
    long long utteranceId,
    const std::string& text,
    bool isFinal) {
    impl_->Submit(source, utteranceId, text, isFinal);
}

void OfflineTranslator::Stop(bool drainFinals) {
    impl_->Stop(drainFinals);
}

bool OfflineTranslator::IsActive() const {
    return impl_->IsActive();
}

std::string OfflineTranslator::GetLastError() const {
    return impl_->GetLastError();
}

} // namespace meetingai::translation

#endif
