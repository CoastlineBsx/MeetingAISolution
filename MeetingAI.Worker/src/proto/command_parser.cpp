#include "pch.h"
#include "command_parser.h"
#include <algorithm>
#include <cstdio>
#include <iomanip>
#include <locale>
#include <nlohmann/json.hpp>
#include <sstream>
#include <unordered_set>

using json = nlohmann::json;

namespace meetingai::proto {

    std::string trim(std::string s) {
        const char* ws = " \t\r\n";
        auto a = s.find_first_not_of(ws);
        if (a == std::string::npos) return "";
        auto b = s.find_last_not_of(ws);
        return s.substr(a, b - a + 1);
    }

    std::string jsonEscape(const std::string& s) {
        std::string o;
        o.reserve(s.size() + 16);
        for (unsigned char c : s) {
            switch (c) {
            case '\"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\b': o += "\\b";  break;
            case '\f': o += "\\f";  break;
            case '\n': o += "\\n";  break;
            case '\r': o += "\\r";  break;
            case '\t': o += "\\t";  break;
            default:
                if (c < 0x20) {
                    char buf[7];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    o += buf;
                }
                else {
                    o += static_cast<char>(c);
                }
            }
        }
        return o;
    }

    bool isQuit(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"quit\"") != std::string::npos;
    }

    bool isTranscribe(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"transcribe_file\"") != std::string::npos;
    }

    bool isTranscribeOpenVINO(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"transcribe_openvino\"") != std::string::npos;
    }

    std::string extractPath(const std::string& json) {
        size_t start = json.find("\"path\":");
        if (start == std::string::npos) return "";

        start = json.find("\"", start + 7);
        if (start == std::string::npos) return "";
        start++;

        size_t end = json.find("\"", start);
        if (end == std::string::npos) return "";

        return json.substr(start, end - start);
    }

    std::string extractMode(const std::string& json) {
        // 查找 "mode":"..."
        size_t pos = json.find("\"mode\"");
        if (pos == std::string::npos) return "auto";

        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return "auto";

        size_t start = json.find("\"", colon);
        if (start == std::string::npos) return "auto";
        start++;

        size_t end = json.find("\"", start);
        if (end == std::string::npos) return "auto";

        return json.substr(start, end - start);
    }

    std::string extractLanguage(const std::string& json) {
        // 查找 "language":"..."
        size_t pos = json.find("\"language\"");
        if (pos == std::string::npos) return "auto";

        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return "auto";

        size_t start = json.find("\"", colon);
        if (start == std::string::npos) return "auto";
        start++;

        size_t end = json.find("\"", start);
        if (end == std::string::npos) return "auto";

        return json.substr(start, end - start);
    }

    // ==================== 流式转录相关 ====================

    bool isStartStream(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"start_stream\"") != std::string::npos;
    }

    bool isStreamChunk(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"stream_chunk\"") != std::string::npos;
    }

    bool isStopStream(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"stop_stream\"") != std::string::npos;
    }

    std::string extractData(const std::string& json) {
        size_t start = json.find("\"data\":");
        if (start == std::string::npos) return "";

        start = json.find("\"", start + 7);
        if (start == std::string::npos) return "";
        start++;

        size_t end = json.find("\"", start);
        if (end == std::string::npos) return "";

        return json.substr(start, end - start);
    }

    int extractSampleRate(const std::string& json) {
        // 查找 "sample_rate":16000
        size_t pos = json.find("\"sample_rate\"");
        if (pos == std::string::npos) return 16000;

        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return 16000;

        // 跳过空格
        size_t start = colon + 1;
        while (start < json.size() && (json[start] == ' ' || json[start] == '\t')) {
            start++;
        }

        // 提取数字
        size_t end = start;
        while (end < json.size() && json[end] >= '0' && json[end] <= '9') {
            end++;
        }

        if (end == start) return 16000;

        try {
            return std::stoi(json.substr(start, end - start));
        }
        catch (...) {
            return 16000;
        }
    }

    // ==================== v2: 多流 ====================
    bool isStartStream2(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos && t.find("\"start_stream2\"") != std::string::npos;
    }
    bool isStreamChunk2(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos && t.find("\"stream_chunk2\"") != std::string::npos;
    }
    bool isStopStream2(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos && t.find("\"stop_stream2\"") != std::string::npos;
    }

    static std::string extractStringField(const std::string& json, const char* key, const char* defv = "") {
        std::string k = std::string("\"") + key + "\"";
        size_t pos = json.find(k);
        if (pos == std::string::npos) return defv;
        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return defv;
        size_t start = json.find("\"", colon);
        if (start == std::string::npos) return defv;
        start++;
        size_t end = json.find("\"", start);
        if (end == std::string::npos) return defv;
        return json.substr(start, end - start);
    }

    std::string extractStreamId(const std::string& json) { return extractStringField(json, "stream_id"); }
    std::string extractSource(const std::string& json) { return extractStringField(json, "source", "unknown"); }

    long long extractTimestampMs(const std::string& json) {
        size_t pos = json.find("\"timestamp_ms\"");
        if (pos == std::string::npos) return -1;
        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return -1;
        size_t start = colon + 1;
        while (start < json.size() && (json[start] == ' ' || json[start] == '\t')) start++;
        size_t end = start;
        while (end < json.size() && json[end] >= '0' && json[end] <= '9') end++;
        if (end == start) return -1;
        try { return std::stoll(json.substr(start, end - start)); } catch (...) { return -1; }
    }

    // ==================== Granite GenAI 相关 ====================
    std::string extractPrompt(const std::string& jsonStr) {
        try {
            auto j = json::parse(jsonStr);
            return j.value("prompt", std::string(""));
        }
        catch (...) {
            return "";
        }
    }

    int extractMaxTokens(const std::string& jsonStr, int defaultValue) {
        try {
            auto j = json::parse(jsonStr);
            return j.value("max_tokens", defaultValue);
        }
        catch (...) {
            return defaultValue;
        }
    }

    float extractTemperature(const std::string& jsonStr, float defaultValue) {
        try {
            auto j = json::parse(jsonStr);
            return j.value("temperature", defaultValue);
        }
        catch (...) {
            return defaultValue;
        }
    }

    std::string extractSystemMessage(const std::string& jsonStr, const std::string& defaultValue) {
        try {
            auto j = json::parse(jsonStr);
            return j.value("system_message", defaultValue);
        }
        catch (...) {
            return defaultValue;
        }
    }

    // ==================== Sherpa-ONNX 实时流式转录相关 ====================

    bool isStartStreaming(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"start_streaming\"") != std::string::npos;
    }

    bool isStreamingAudio(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"streaming_audio\"") != std::string::npos;
    }

    bool isStopStreaming(const std::string& s) {
        auto t = trim(s);
        return t.find("\"type\"") != std::string::npos &&
            t.find("\"stop_streaming\"") != std::string::npos;
    }

    std::string extractAudioData(const std::string& json) {
        size_t start = json.find("\"audio_data\":");
        if (start == std::string::npos) return "";

        start = json.find("\"", start + 13);  // 13 = len("audio_data":")
        if (start == std::string::npos) return "";
        start++;

        size_t end = json.find("\"", start);
        if (end == std::string::npos) return "";

        return json.substr(start, end - start);
    }

    bool extractIsEnd(const std::string& json) {
        size_t pos = json.find("\"is_end\"");
        if (pos == std::string::npos) return false;

        size_t colon = json.find(":", pos);
        if (colon == std::string::npos) return false;

        // Skip whitespace
        size_t start = colon + 1;
        while (start < json.size() && (json[start] == ' ' || json[start] == '\t')) {
            start++;
        }

        // Check for "true"
        if (json.compare(start, 4, "true") == 0) {
            return true;
        }

        return false;
    }

    std::string extractTranslationMode(const std::string& json) {
        const std::string mode = extractStringField(
            json,
            "translation_mode",
            "off");
        if (mode == "auto" || mode == "to_zh" || mode == "to_en") {
            return mode;
        }
        return "off";
    }

    bool extractSummaryEnabled(const std::string& jsonStr) {
        try {
            const auto value = json::parse(jsonStr);
            return value.value("summary_enabled", true);
        }
        catch (...) {
            return true;
        }
    }

    static void replaceAll(
        std::string& text,
        const std::string& from,
        const std::string& to) {
        if (from.empty()) {
            return;
        }
        std::size_t position = 0;
        while ((position = text.find(from, position)) != std::string::npos) {
            text.replace(position, from.size(), to);
            position += to.size();
        }
    }

    static std::string normalizeSherpaHotwordText(std::string text) {
        // Sherpa 使用 ASCII 冒号分隔“热词”和“分数”。PPT 标题本身如果
        // 含有冒号（例如 "Application: useful"），底层会把冒号后的
        // 单词交给 std::stof，从而令整场流式会议启动失败。
        replaceAll(text, ":", " ");
        replaceAll(text, "\xEF\xBC\x9A", " "); // 全角中文冒号 U+FF1A

        for (char& character : text) {
            const auto value = static_cast<unsigned char>(character);
            if (value < 0x20 || value == 0x7f) {
                character = ' ';
            }
        }

        std::string normalized;
        normalized.reserve(text.size());
        bool previousWasSpace = true;
        for (const char character : text) {
            if (character == ' ') {
                if (!previousWasSpace) {
                    normalized.push_back(' ');
                    previousWasSpace = true;
                }
                continue;
            }
            normalized.push_back(character);
            previousWasSpace = false;
        }
        if (!normalized.empty() && normalized.back() == ' ') {
            normalized.pop_back();
        }
        return normalized;
    }

    MeetingContextCommand extractMeetingContext(const std::string& jsonStr) {
        MeetingContextCommand result;
        try {
            const auto value = json::parse(jsonStr);
            if (value.contains("preparation_id") &&
                value["preparation_id"].is_number_integer()) {
                result.preparationId =
                    std::max<std::int64_t>(
                        0,
                        value["preparation_id"].get<std::int64_t>());
            }

            if (value.contains("context_title") &&
                value["context_title"].is_string()) {
                result.title = trim(
                    value["context_title"].get<std::string>());
            }

            std::unordered_set<std::int64_t> seenDocumentIds;
            if (value.contains("context_document_ids") &&
                value["context_document_ids"].is_array()) {
                for (const auto& item : value["context_document_ids"]) {
                    if (!item.is_number_integer()) {
                        continue;
                    }
                    const auto documentId = item.get<std::int64_t>();
                    if (documentId <= 0 ||
                        !seenDocumentIds.insert(documentId).second) {
                        continue;
                    }
                    result.documentIds.push_back(documentId);
                    if (result.documentIds.size() == 5) {
                        break;
                    }
                }
            }

            std::unordered_set<std::string> seenHotwords;
            if (value.contains("hotwords") &&
                value["hotwords"].is_array()) {
                for (const auto& item : value["hotwords"]) {
                    if (!item.is_object() ||
                        !item.contains("text") ||
                        !item["text"].is_string()) {
                        continue;
                    }

                    std::string text = normalizeSherpaHotwordText(
                        trim(item["text"].get<std::string>()));
                    if (text.empty() || !seenHotwords.insert(text).second) {
                        continue;
                    }

                    float score = 2.0f;
                    if (item.contains("score") &&
                        item["score"].is_number()) {
                        score = item["score"].get<float>();
                    }
                    score = std::clamp(score, 1.0f, 5.0f);
                    result.hotwords.push_back({ std::move(text), score });
                    if (result.hotwords.size() == 100) {
                        break;
                    }
                }
            }

            if (!result.HasPreparation()) {
                result.title.clear();
                result.documentIds.clear();
                result.hotwords.clear();
            }
        }
        catch (...) {
            return {};
        }
        return result;
    }

    std::string buildSherpaHotwordsBuffer(
        const MeetingContextCommand& context) {
        std::ostringstream output;
        output.imbue(std::locale::classic());
        output << std::fixed << std::setprecision(2);
        for (const auto& hotword : context.hotwords) {
            const std::string text =
                normalizeSherpaHotwordText(hotword.text);
            if (text.empty()) {
                continue;
            }
            output << text << " :"
                   << std::clamp(hotword.score, 1.0f, 5.0f)
                   << '\n';
        }
        return output.str();
    }

    std::string buildMeetingContextSnapshotJson(
        const MeetingContextCommand& context) {
        json snapshot = {
            { "schema_version", 1 },
            { "preparation_id", context.preparationId },
            { "title", context.title },
            { "document_ids", context.documentIds },
            { "hotwords", json::array() }
        };
        for (const auto& hotword : context.hotwords) {
            snapshot["hotwords"].push_back({
                { "text", hotword.text },
                { "score", hotword.score }
            });
        }
        return snapshot.dump();
    }

    bool isRequestMeetingSummary(const std::string& json) {
        const auto text = trim(json);
        return text.find("\"type\"") != std::string::npos &&
            text.find("\"request_meeting_summary\"") != std::string::npos;
    }

} // namespace meetingai::proto
