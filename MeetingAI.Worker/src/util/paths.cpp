#include "pch.h"
#include "paths.h"
#include <windows.h>
#include <filesystem>
#include <cstdlib>
#include <shlobj.h>

namespace fs = std::filesystem;

namespace {
    std::string wToUtf8(const std::wstring& ws) {
        if (ws.empty()) return {};
        int n = ::WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), nullptr, 0, nullptr, nullptr);
        std::string s(n, 0);
        ::WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), (int)ws.size(), s.data(), n, nullptr, nullptr);
        return s;
    }
} // anon

namespace meetingai::util {

    std::wstring utf8ToW(const std::string& s) {
        if (s.empty()) return L"";
        int n = ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
        std::wstring ws(n, 0);
        ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), ws.data(), n);
        return ws;
    }

    std::string getExeDir() {
        wchar_t buf[MAX_PATH]{};
        ::GetModuleFileNameW(nullptr, buf, MAX_PATH);
        fs::path p(buf);
        return wToUtf8(p.parent_path().wstring());
    }

    std::string getDataRoot() {
        // 1) 环境变量 MEETINGAI_DATA_DIR
        char* envUtf8 = nullptr; size_t len = 0;
        if (_dupenv_s(&envUtf8, &len, "MEETINGAI_DATA_DIR") == 0 && envUtf8) {
            std::string v(envUtf8); free(envUtf8);
            if (!v.empty()) {
                fs::create_directories(v);
                return fs::path(v).string();
            }
        }

        // 2) 便携模式：exe 同目录存在 portable.flag → ./data
        auto exe = getExeDir();
        if (fs::exists(fs::path(exe) / "portable.flag")) {
            auto p = fs::path(exe) / "data";
            fs::create_directories(p);
            return p.string();
        }

        // 3) 默认：%LOCALAPPDATA%\MeetingAI
        wchar_t* wlap = nullptr; len = 0;
        if (_wdupenv_s(&wlap, &len, L"LOCALAPPDATA") == 0 && wlap) {
            fs::path root(wlap); free(wlap);
            root /= L"MeetingAI";
            fs::create_directories(root);
            return wToUtf8(root.wstring());
        }

        // 4) 兜底：exe\data
        auto fb = fs::path(exe) / "data";
        fs::create_directories(fb);
        return fb.string();
    }

    std::string getDatabasePath() {
        return (fs::path(getDataRoot()) / "meeting.db").string();
    }

    std::string resolveModelFileUtf8(const wchar_t* filename) {
        // 调试期固定路径（按你当前项目）
        fs::path baseDir = L"D:\\Microsoft\\Microsoft Visual Studio Projects\\MeetingAISolution\\MeetingAI.Worker\\models";

        // 交付期可改为 ProgramData：
        // wchar_t commonAppData[MAX_PATH]{};
        // if (SUCCEEDED(::SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, 0, commonAppData))) {
        //     baseDir = fs::path(commonAppData) / L"MeetingAI" / L"models";
        // }

        fs::path full = baseDir / filename;
        return wToUtf8(full.wstring());
    }

} // namespace meetingai::util
