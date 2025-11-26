#include "pch.h"
#include "command_parser.h"
#include <cstdio>
#include <nlohmann/json.hpp>

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

} // namespace meetingai::proto
