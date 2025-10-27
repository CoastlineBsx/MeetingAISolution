#include "pch.h"
#include "command_parser.h"
#include <cstdio>

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

} // namespace meetingai::proto
