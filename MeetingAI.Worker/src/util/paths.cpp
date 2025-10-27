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

    //std::string getExeDir() {
    //    wchar_t buf[MAX_PATH]{};
    //    ::GetModuleFileNameW(nullptr, buf, MAX_PATH);
    //    fs::path p(buf);
    //    return wToUtf8(p.parent_path().wstring());
    //}
    std::string getExeDir() {
        std::wstring buf(512, L'\0');
        for (;;) {
            DWORD n = ::GetModuleFileNameW(nullptr, buf.data(), (DWORD)buf.size());
            if (n == 0) return {};
            if (n < buf.size() - 1) { // 成功
                buf.resize(n);
                break;
            }
            buf.resize(buf.size() * 2); // 缓冲区不够继续扩容
        }
        fs::path p(buf);
        return wToUtf8(p.parent_path().wstring());
    }


    std::string getDataRoot() {
        // 1) »·¾³±äÁ¿ MEETINGAI_DATA_DIR
        char* envUtf8 = nullptr; size_t len = 0;
        if (_dupenv_s(&envUtf8, &len, "MEETINGAI_DATA_DIR") == 0 && envUtf8) {
            std::string v(envUtf8); free(envUtf8);
            if (!v.empty()) {
                fs::create_directories(v);
                return fs::path(v).string();
            }
        }

        // 2) ±ãÐ¯Ä£Ê½£ºexe Í¬Ä¿Â¼´æÔÚ portable.flag ¡ú ./data
        auto exe = getExeDir();
        if (fs::exists(fs::path(exe) / "portable.flag")) {
            auto p = fs::path(exe) / "data";
            fs::create_directories(p);
            return p.string();
        }

        // 3) Ä¬ÈÏ£º%LOCALAPPDATA%\MeetingAI
        wchar_t* wlap = nullptr; len = 0;
        if (_wdupenv_s(&wlap, &len, L"LOCALAPPDATA") == 0 && wlap) {
            fs::path root(wlap); free(wlap);
            root /= L"MeetingAI";
            fs::create_directories(root);
            return wToUtf8(root.wstring());
        }

        // 4) ¶µµ×£ºexe\data
        auto fb = fs::path(exe) / "data";
        fs::create_directories(fb);
        return fb.string();
    }

    std::string getDatabasePath() {
        return (fs::path(getDataRoot()) / "meeting.db").string();
    }

    //std::string resolveModelFileUtf8(const wchar_t* filename) {
    //    fs::path baseDir = L"C:\\VisualStudioSource\\MeetingAI.Worker\\models";
    //    fs::path full = baseDir / filename;
    //    return wToUtf8(full.wstring());
    //}

 
    std::string resolveModelFileUtf8(const wchar_t* filename) {
        static std::wstring cachedModelDirW; // 进程级缓存

        auto ensureExists = [](const fs::path& dir) -> bool {
            std::error_code ec;
            return fs::exists(dir, ec) && fs::is_directory(dir, ec);
            };

        // 1) 如已缓存，直接拼接返回
        if (!cachedModelDirW.empty()) {
            fs::path full = fs::path(cachedModelDirW) / filename;
            std::error_code ec;
            if (fs::exists(full, ec) && fs::is_regular_file(full, ec)) {
                return wToUtf8(full.wstring());
            }
        }

        // 2) 从 exe 目录开始，向上查找 MeetingAI.Worker\models
        const fs::path exe = meetingai::util::utf8ToW(getExeDir());
        fs::path cur = exe;
        for (int i = 0; i < 8; ++i) { // 最多向上 8 层，够覆盖常见构建/安装层级
            fs::path tryDir = cur / L"MeetingAI.Worker" / L"models";
            if (ensureExists(tryDir)) {
                cachedModelDirW = tryDir.wstring();
                fs::path full = tryDir / filename;
                std::error_code ec;
                if (fs::exists(full, ec) && fs::is_regular_file(full, ec)) {
                    return wToUtf8(full.wstring());
                }
                // 找到 models 但未必有该文件，仍然返回拼接后的路径（让上层决定是否拉取/报错）
                return wToUtf8(full.wstring());
            }
            if (!cur.has_parent_path()) break;
            cur = cur.parent_path();
        }

        // 3) 兜底：exe\models
        fs::path fallback = exe / L"models";
        if (ensureExists(fallback)) {
            cachedModelDirW = fallback.wstring();
            fs::path full = fallback / filename;
            return wToUtf8(full.wstring());
        }

        // 4) 最终兜底：直接拼接 exe\models\filename（即便目录未创建）
        fs::path full = fallback / filename;
        return wToUtf8(full.wstring());
    }
}