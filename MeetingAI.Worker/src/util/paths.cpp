

#include "pch.h"          
#include "paths.h"

#include <windows.h>
#include <filesystem>
#include <cstdlib>   // _dupenv_s / _wdupenv_s
#include <string>

static std::string WToUtf8(const std::wstring& ws) {
    if (ws.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring Utf8ToW(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring ws(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), ws.data(), n);
    return ws;
}

std::string GetExeDir() {
    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::filesystem::path p(buf);
    return WToUtf8(p.parent_path().wstring());
}

std::string GetDataRoot() {
    // 1) 环境变量 MEETINGAI_DATA_DIR（安全读取）
    char* envUtf8 = nullptr; size_t len = 0;
    if (_dupenv_s(&envUtf8, &len, "MEETINGAI_DATA_DIR") == 0 && envUtf8) {
        std::string v(envUtf8);
        free(envUtf8);
        if (!v.empty()) {
            std::filesystem::create_directories(v);
            return std::filesystem::path(v).string();
        }
    }

    // 2) 便携模式：exe 同目录有 portable.flag → ./data
    auto exe = GetExeDir();
    if (std::filesystem::exists(std::filesystem::path(exe) / "portable.flag")) {
        auto p = std::filesystem::path(exe) / "data";
        std::filesystem::create_directories(p);
        return p.string();
    }

    // 3) 默认：%LOCALAPPDATA%\MeetingAI（安全读取宽环境变量）
    wchar_t* wlap = nullptr; len = 0;
    if (_wdupenv_s(&wlap, &len, L"LOCALAPPDATA") == 0 && wlap) {
        std::filesystem::path root(wlap); free(wlap);
        root /= L"MeetingAI";
        std::filesystem::create_directories(root);
        return WToUtf8(root.wstring());
    }

    // 4) 兜底：exe\data
    auto fb = std::filesystem::path(exe) / "data";
    std::filesystem::create_directories(fb);
    return fb.string();
}

std::string GetDatabasePath() {
    return (std::filesystem::path(GetDataRoot()) / "meeting.db").string();
}
