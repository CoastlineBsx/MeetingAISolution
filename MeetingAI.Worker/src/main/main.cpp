//#include "pch.h"
//#include <windows.h>
//#include <sddl.h>
//#include <iostream>
//#include <string>
//#include "database.hpp"
//#include "paths.h"
//#include "sqlite3.h"
//#include <filesystem>
//#include "transcriber.hpp" // 新增：包含 whisper 封装
//#include "granite/granite_genai.hpp" // ★ 新增：Granite GenAI
//#include <shlobj.h>      // SHGetFolderPathW
//#include <codecvt>       // 宽/窄字符串转码（仅用于 Win -> UTF-8）
//#include <thread>
//#include <mutex>   // ★ 新增
//#include "paths.h"
//#include "command_parser.h"
//#include "logging.h"
//#include "pipe_security.h"


#include <windows.h>
#include <sddl.h>
#include <iostream>
#include <string>
#include <memory>      // ← 新增：unique_ptr
#include <functional>  // ← 新增：function
#include <mutex>
#include <thread>
#include <filesystem>
#include <shlobj.h>
#include <codecvt>

// 然后包含项目头文件
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"
#include "transcriber.hpp"
#include "granite/granite_genai.hpp"  // ← OpenVINO 头文件
#include "embedding/embedding_genai.hpp"  // ← 新增：Embedding GenAI
#include "command_parser.h"
#include "logging.h"
#include "pipe_security.h"

// OpenVINO Core for device enumeration
#include <openvino/openvino.hpp>


static std::once_flag g_model_once2; // ★ 新增：Worker 级只加载一次模型（Whisper）
static std::once_flag g_granite_once; // ★ 新增：Granite 模型只加载一次
static std::once_flag g_embedding_once; // ★ 新增：Embedding 模型只加载一次

// ========== Granite GenAI 全局实例 ==========
static std::unique_ptr<meetingai::granite::GraniteGenAI> g_granite;
static std::string g_system_prompt = "你是一个专业、简洁的中文助手。请用简体中文回答问题，注重逻辑性和条理性。";
static int g_max_tokens = 256;
static float g_temperature = 0.7f;

// ========== Embedding GenAI 全局实例 ==========
static std::unique_ptr<meetingai::embedding::EmbeddingGenAI> g_embedding;

// ========== 设备配置 ==========
static std::string g_device = "CPU";  // 默认使用 CPU

// ========== 工具函数：获取环境变量 ==========
static std::string GetEnvOrDefault(const char* key, const char* fallback) {
    char* buf = nullptr;
    size_t len = 0;
    if (_dupenv_s(&buf, &len, key) == 0 && buf != nullptr) {
        std::string value(buf);
        free(buf);
        if (!value.empty()) {
            return value;
        }
    }
    return std::string(fallback);
}

//
//static std::string json_escape(const std::string& s) {
//    std::string o;
//    o.reserve(s.size() + 16);
//    for (unsigned char c : s) {
//        switch (c) {
//        case '\"': o += "\\\""; break;
//        case '\\': o += "\\\\"; break;
//        case '\b': o += "\\b";  break;
//        case '\f': o += "\\f";  break;
//        case '\n': o += "\\n";  break;
//        case '\r': o += "\\r";  break;
//        case '\t': o += "\\t";  break;
//        default:
//            if (c < 0x20) {
//                char buf[7];
//                snprintf(buf, sizeof(buf), "\\u%04x", c);
//                o += buf;
//            }
//            else {
//                o += static_cast<char>(c);
//            }
//        }
//    }
//    return o;
//}


// --------- 追加：通用工具 & 退出标志 ----------
static volatile BOOL g_shutdownRequested = FALSE;
// 用于回调里把段结果写回 Host
HANDLE g_pipe_for_callback = NULL;

//// 去掉首尾空白
//static inline std::string trim(std::string s) {
//    size_t a = s.find_first_not_of(" \t\r\n");
//    size_t b = s.find_last_not_of(" \t\r\n");
//    if (a == std::string::npos) return "";
//    return s.substr(a, b - a + 1);
//}
//
//// 简单判断是否为 {"type":"quit"}（容忍空白/额外字段）
//static bool isQuitMessage(const std::string& s) {
//    auto t = trim(s);
//    // 粗判：必须包含 "type":"quit"
//    return t.find("\"type\"") != std::string::npos &&
//        t.find("\"quit\"") != std::string::npos;
//}

//// 新增：简单判断是否为转录命令
//static bool isTranscribeMessage(const std::string& s) {
//    auto t = trim(s);
//    return t.find("\"type\"") != std::string::npos &&
//        t.find("\"transcribe_file\"") != std::string::npos;
//}

//// 新增：从简单 JSON 中提取文件路径（简化版解析）
//static std::string extractFilePath(const std::string& json) {
//    size_t start = json.find("\"path\":");
//    if (start == std::string::npos) return "";
//    
//    start = json.find("\"", start + 7);
//    if (start == std::string::npos) return "";
//    start++;
//    
//    size_t end = json.find("\"", start);
//    if (end == std::string::npos) return "";
//    
//    return json.substr(start, end - start);
//}
//
//static std::string ResolveModelFileUtf8(const wchar_t* filename) {
//    namespace fs = std::filesystem;
//
//    // ★ 调试期固定路径
//    fs::path baseDir = L"D:\\Microsoft\\Microsoft Visual Studio Projects\\MeetingAISolution\\WorkerNative\\models";
//
//    // ★ 交付时改成：
//    // wchar_t commonAppData[MAX_PATH]{};
//    // if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, 0, commonAppData))) {
//    //     baseDir = fs::path(commonAppData) / L"MeetingAI" / L"models";
//    // }
//
//    fs::path fullPath = baseDir / filename;
//
//    // 转 UTF-8
//    int n = WideCharToMultiByte(CP_UTF8, 0, fullPath.c_str(), -1, nullptr, 0, nullptr, nullptr);
//    std::string out(n - 1, '\0');
//    WideCharToMultiByte(CP_UTF8, 0, fullPath.c_str(), -1, out.data(), n, nullptr, nullptr);
//
//    return out;
//}

// ========== Granite GenAI 初始化 ==========
static void InitializeGraniteGenAI(HANDLE hPipe, const std::string& device = "CPU") {
    std::wcout << L"[Worker] 初始化 Granite GenAI...\n";
    try {
        // 枚举可用设备并通过管道发送
        ov::Core core;
        auto available_devices = core.get_available_devices();

        std::string devices_msg = "{\"type\":\"info\",\"message\":\"[OpenVINO] 可用设备:\\n";
        for (const auto& dev : available_devices) {
            devices_msg += "  - " + dev;
            try {
                auto full_name = core.get_property(dev, ov::device::full_name);
                devices_msg += " (" + full_name + ")";
            } catch (...) {}
            devices_msg += "\\n";
        }
        devices_msg += "  将使用: " + device + "\"}\n";

        DWORD written;
        WriteFile(hPipe, devices_msg.data(), (DWORD)devices_msg.size(), &written, nullptr);

        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_GRANITE_MODEL",
            "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/granite-3.3-2b-npu"
        );

        g_granite = std::make_unique<meetingai::granite::GraniteGenAI>(model_dir, device);
        std::wcout << L"[Worker] Granite GenAI ✅ 初始化成功: " << device.c_str() << L"\n";

        // 通知 Host 模型已就绪
        const char* ready = "{\"type\":\"granite_ready\",\"device\":\"CPU\"}\n";
        WriteFile(hPipe, ready, (DWORD)strlen(ready), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] Granite GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"Granite 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Embedding GenAI 初始化 ==========
static void InitializeEmbeddingGenAI(HANDLE hPipe, const std::string& device = "CPU") {
    std::wcout << L"[Worker] 初始化 Embedding GenAI...\n";
    try {
        // 枚举可用设备并通过管道发送
        ov::Core core;
        auto available_devices = core.get_available_devices();

        std::string devices_msg = "{\"type\":\"info\",\"message\":\"[OpenVINO] 可用设备:\\n";
        for (const auto& dev : available_devices) {
            devices_msg += "  - " + dev;
            try {
                auto full_name = core.get_property(dev, ov::device::full_name);
                devices_msg += " (" + full_name + ")";
            } catch (...) {}
            devices_msg += "\\n";
        }
        devices_msg += "  将使用: " + device + "\"}\n";

        DWORD written;
        WriteFile(hPipe, devices_msg.data(), (DWORD)devices_msg.size(), &written, nullptr);

        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_EMBEDDING_MODEL",
            "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/bge-m3-npu"
        );

        g_embedding = std::make_unique<meetingai::embedding::EmbeddingGenAI>(model_dir, device);
        std::wcout << L"[Worker] Embedding GenAI ✅ 初始化成功: " << device.c_str()
                   << L" (dim=" << g_embedding->embedding_dim() << L")\n";

        // 通知 Host 模型已就绪
        std::string ready = std::string("{\"type\":\"embedding_ready\",\"device\":\"") +
                           device + "\",\"dim\":" + std::to_string(g_embedding->embedding_dim()) + "}\n";
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] Embedding GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"Embedding 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Granite GenAI 命令处理 ==========
static void handleGraniteCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 确保模型已加载（懒加载）
        std::call_once(g_granite_once, [&] {
            InitializeGraniteGenAI(hPipe, g_device);
        });

        if (!g_granite) {
            std::string err = "{\"type\":\"error\",\"message\":\"Granite 未初始化\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 解析命令类型
        size_t typePos = command.find("\"type\"");
        if (typePos == std::string::npos) return;

        // -------- 单轮流式生成 --------
        if (command.find("\"granite_generate_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, g_max_tokens);
            float temp = meetingai::proto::extractTemperature(command, g_temperature);

            std::wcout << L"[Granite] 单轮生成: " << prompt.c_str() << L"\n";

            g_granite->generateStream(prompt, [&](const std::string& token) {
                // ===== DEBUG: 打印 token 的原始字节 =====
                std::wcout << L"[DEBUG] Token length: " << token.size() << L" bytes" << std::endl;
                std::wcout << L"[DEBUG] Token hex: ";
                for (unsigned char c : token) {
                    std::wcout << std::hex << std::setw(2) << std::setfill(L'0') << (int)c << L" ";
                }
                std::wcout << std::dec << std::endl;
                std::wcout << L"[DEBUG] Token string: \"" << token.c_str() << L"\"" << std::endl;
                // ===== END DEBUG =====

                std::string chunk = "{\"type\":\"token\",\"text\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"done\"}\n");
        }
        // -------- 多轮：开始会话 --------
        else if (command.find("\"granite_start_chat\"") != std::string::npos) {
            std::string sysMsg = meetingai::proto::extractSystemMessage(command, g_system_prompt);
            g_granite->startChat(sysMsg);
            write_json("{\"type\":\"granite_chat_status\",\"status\":\"started\"}\n");
            std::wcout << L"[Granite] 多轮会话已开始\n";
        }
        // -------- 多轮：流式对话 --------
        else if (command.find("\"granite_chat_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, g_max_tokens);
            float temp = meetingai::proto::extractTemperature(command, g_temperature);

            std::wcout << L"[Granite] 多轮对话: " << prompt.c_str() << L"\n";

            g_granite->chatStream(prompt, [&](const std::string& token) {
                // ===== DEBUG: 打印 token 的原始字节 =====
                std::wcout << L"[DEBUG] Token length: " << token.size() << L" bytes" << std::endl;
                std::wcout << L"[DEBUG] Token hex: ";
                for (unsigned char c : token) {
                    std::wcout << std::hex << std::setw(2) << std::setfill(L'0') << (int)c << L" ";
                }
                std::wcout << std::dec << std::endl;
                std::wcout << L"[DEBUG] Token string: \"" << token.c_str() << L"\"" << std::endl;
                // ===== END DEBUG =====

                std::string chunk = "{\"type\":\"token\",\"text\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"done\"}\n");
        }
        // -------- 多轮：结束会话 --------
        else if (command.find("\"granite_finish_chat\"") != std::string::npos) {
            g_granite->finishChat();
            write_json("{\"type\":\"granite_chat_status\",\"status\":\"finished\"}\n");
            std::wcout << L"[Granite] 多轮会话已结束\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Granite] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Embedding GenAI 命令处理 ==========
static void handleEmbeddingCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 确保模型已加载（懒加载）
        std::call_once(g_embedding_once, [&] {
            InitializeEmbeddingGenAI(hPipe, g_device);
        });

        if (!g_embedding) {
            std::string err = "{\"type\":\"error\",\"message\":\"Embedding 未初始化\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 解析命令类型
        if (command.find("\"embedding_encode\"") != std::string::npos) {
            std::string text = meetingai::proto::extractPrompt(command);

            std::wcout << L"[Embedding] 编码文本: " << text.substr(0, 50).c_str() << L"...\n";

            // 生成向量
            auto embedding = g_embedding->encode(text);

            // 构建 JSON 响应（向量转为数组）
            std::string response = "{\"type\":\"embedding_result\",\"embedding\":[";
            for (size_t i = 0; i < embedding.size(); i++) {
                response += std::to_string(embedding[i]);
                if (i < embedding.size() - 1) response += ",";
            }
            response += "]}\n";

            write_json(response);
            std::wcout << L"[Embedding] ✅ 编码完成 (dim=" << embedding.size() << L")\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Embedding] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// 新增：处理转录命令
static void handleTranscribeCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理转录命令\n";

    // ★ 仅初始化一次模型（工业做法A）
    std::call_once(g_model_once2, [&] {
        std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
        if (!InitWhisperOnce(modelPathOnce)) {
            std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
            DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
        }
        else {
            const char* ok = "{\"type\":\"stage\",\"name\":\"model_ready\"}\n";
            DWORD written; WriteFile(hPipe, ok, (DWORD)strlen(ok), &written, nullptr);
        }
        });


    // 提取文件路径
    std::string audioPath = meetingai::proto::extractPath(command);
    if (audioPath.empty()) {
        std::string error = "{\"type\":\"error\",\"message\":\"无法解析音频文件路径\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }
    
    std::wcout << L"[Worker] 音频文件路径: " << audioPath.c_str() << L"\n";
    
    // 假设模型路径（你需要根据实际情况调整）
    //std::string modelPath = "models\\ggml-small.bin";  
    std::string modelPath = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
    g_pipe_for_callback = hPipe;
    
    // 提取mode参数
    std::string sceneMode = meetingai::proto::extractMode(command);
    std::cout << "[Worker] 转录模式: " << sceneMode << std::endl;

    // 提取language参数
    std::string language = meetingai::proto::extractLanguage(command);
    std::cout << "[Worker] 语言设置: " << language << std::endl;

    // 执行转录
    std::vector<WhisperSegment> segments;
    bool success = TranscribeAudioFile(modelPath, audioPath, segments, sceneMode, language);
    g_pipe_for_callback = NULL; // 清理
    if (!success) {
        std::string error = "{\"type\":\"error\",\"message\":\"转录失败\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }
    
    // 发送每个转录片段
    for (const auto& segment : segments) {
        // 插入数据库
        InsertTranscript("Unknown", segment.text, segment.start_time);
        
        // 发送给 Host
        std::string response = std::string("{\"type\":\"asr_segment\",\"text\":\"") +
            meetingai::proto::jsonEscape(segment.text) +
            "\",\"t0_ms\":" + std::to_string((int)(segment.start_time * 1000)) +
            ",\"t1_ms\":" + std::to_string((int)(segment.end_time * 1000)) + "}\n";


            
        DWORD written;
        WriteFile(hPipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr);
        
        std::wcout << L"[Worker] 发送片段: " << segment.text.c_str() << L"\n";
    }
    
    // 发送完成信号
    std::string complete = "{\"type\":\"transcribe_complete\",\"segments\":" + 
        std::to_string(segments.size()) + "}\n";
    DWORD written;
    WriteFile(hPipe, complete.data(), static_cast<DWORD>(complete.size()), &written, nullptr);
}

// 处理控制台关闭/注销/关机等信号，优雅退出
static BOOL WINAPI ConsoleCtrlHandler(DWORD dwCtrlType) {
    switch (dwCtrlType) {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
    case CTRL_LOGOFF_EVENT:
    case CTRL_SHUTDOWN_EVENT:
        g_shutdownRequested = TRUE;
        return TRUE; // 我们处理了
    }
    return FALSE;
}


//
//
//static void logLastError(const wchar_t* msg) {
//    DWORD err = GetLastError();
//    std::wcerr << msg << L" (code: " << err << L")\n";
//}
//
//bool createPipeSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& pSD) {
//    // 调试期：允许 AppContainer 和 Everyone 访问
//    LPCWSTR sddl = L"D:(A;;GA;;;AC)(A;;GA;;;WD)";
//    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
//        sddl, SDDL_REVISION_1, &pSD, nullptr)) {
//        logLastError(L"[Worker] SDDL parse failed");
//        return false;
//    }
//    sa.nLength = sizeof(sa);
//    sa.bInheritHandle = FALSE;
//    sa.lpSecurityDescriptor = pSD;
//    return true;
//}

int wmain() {
    // ★ 新增 1: 初始化数据库
    if (!InitDatabaseOnce()) {
        std::wcerr << L"[Worker] 数据库初始化失败！\n";
        return 1;
    }

    // ★ 新增 2: 插入一条测试记录
    InsertTranscript("system", "worker started", 0.0);
    bool ok = InsertTranscript("system", "worker started", 0.0);
    std::cout << "[DB] insert result = " << (ok ? "ok" : "fail") << "\n";

    // ★ 新增 3：打印 DB 路径、是否存在、记录总数（ASCII 输出，避免中文编码问题）
    {
        std::string dbPath = meetingai::util::getDatabasePath();
        std::cout << "[DB] path = " << dbPath << "\n";

        bool exists = std::filesystem::exists(dbPath);
        std::cout << "[DB] exists = " << (exists ? "true" : "false") << "\n";

        sqlite3* db = nullptr;
        if (sqlite3_open(dbPath.c_str(), &db) == SQLITE_OK) {   // 用 UTF-8 版本打开
            sqlite3_stmt* st = nullptr;
            if (sqlite3_prepare_v2(db, "SELECT COUNT(*) FROM transcripts;", -1, &st, nullptr) == SQLITE_OK) {
                if (sqlite3_step(st) == SQLITE_ROW) {
                    int cnt = sqlite3_column_int(st, 0);
                    std::cout << "[DB] count = " << cnt << "\n";
                }
                else {
                    std::cout << "[DB] step() failed\n";
                }
                sqlite3_finalize(st);
            }
            else {
                std::cout << "[DB] prepare() failed: " << sqlite3_errmsg(db) << "\n";
            }
            sqlite3_close(db);
        }
        else {
            std::cout << "[DB] open failed\n";
        }
    }

    // 检查是否传入 --ppid 和 --device 参数
    DWORD parentPid = 0;
    for (int i = 1; i < __argc; i++) {
        if (std::wstring(__wargv[i]) == L"--ppid" && i + 1 < __argc) {
            parentPid = std::wcstoul(__wargv[++i], nullptr, 10);
        }
        else if (std::wstring(__wargv[i]) == L"--device" && i + 1 < __argc) {
            // 解析 --device 参数（CPU/GPU/NPU）
            std::wstring wdevice = __wargv[++i];
            g_device = std::string(wdevice.begin(), wdevice.end());
            std::wcout << L"[Worker] Device 参数: " << wdevice.c_str() << L"\n";
        }
    }

    HANDLE hParent = nullptr;
    if (parentPid) {
        hParent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
    }

    const wchar_t* pipeName = L"\\\\.\\pipe\\MeetingAI_Pipe";

    // ★ 注册控制台控制事件
    SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);
    // --- 单实例互斥量（当前会话） ---
    HANDLE hMutex = CreateMutexW(nullptr, TRUE, L"Local\\MeetingAI_Worker_Singleton");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        std::wcerr << L"[Worker] another instance is running. exit.\n";
        return 0;
    }

    std::wcout << L"[Worker PID " << GetCurrentProcessId() << L"] starting pipe server...\n";

    SECURITY_ATTRIBUTES sa{};
    PSECURITY_DESCRIPTOR pSD = nullptr;
    if (!meetingai::ipc::createPipeSecurity(sa, pSD)) return 1;

    bool shutdownRequested = false;

    while (!shutdownRequested && !g_shutdownRequested) {
        // 如果 Host 已退出，直接标记结束
        if (hParent && WaitForSingleObject(hParent, 0) == WAIT_OBJECT_0) {
            std::wcout << L"[Worker] Host exited, shutting down\n";
            shutdownRequested = true;
            break; // 直接跳出循环
        }

        // 1) 创建管道（单实例；客户端断开后再循环创建）
        HANDLE hPipe = CreateNamedPipeW(
            pipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,              // 单实例即可；需要并发再开多实例
            4096, 4096, 0,
            &sa
        );
        if (hPipe == INVALID_HANDLE_VALUE) {
            meetingai::util::logLastError(L"[IPC] CreateNamedPipe failed");
            break;
        }

        std::wcout << L"[Worker] pipe created, waiting for client...\n";

        // 2) 等待客户端
        BOOL connected = ConnectNamedPipe(hPipe, nullptr) ? TRUE :
            (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected) {
            meetingai::util::logLastError(L"[Worker] ConnectNamedPipe failed");
            CloseHandle(hPipe);
            continue; // 重新创建实例
        }

        std::wcout << L"[Worker] client connected\n";

        // 3) 连接存活期间，循环读取“按行”的多条消息
        std::string buffer;
        DWORD read = 0;
        char ch = 0;

        while (true) {
            // ReadFile 会阻塞直到有数据或对端关闭
            if (!ReadFile(hPipe, &ch, 1, &read, nullptr)) {
                DWORD err = GetLastError();
                if (err == ERROR_BROKEN_PIPE) {
                    std::wcout << L"[Worker] client disconnected\n";
                }
                else {
                    meetingai::util::logLastError(L"[Worker] ReadFile failed");
                }
                break; // 退出连接循环，去清理并等待下一个客户端
            }
            if (read == 0) {
                // 对端优雅关闭
                std::wcout << L"[Worker] client closed\n";
                break;
            }

            // ★ 新增：全局退出检查
            if (g_shutdownRequested) {
                std::wcout << L"[Worker] global shutdown requested\n";
                break; // 跳出连接循环
            }

            if (ch == '\n') {
                // 收到一整行，处理并回复
                std::wcout << L"[Worker] received: " << buffer.c_str() << L"\n";

                // ---- 退出命令（容忍空白/额外字段）----
                if (meetingai::proto::isQuit(buffer)) {
                    std::wcout << L"[Worker] quit requested\n";
                    shutdownRequested = true; // 进程级退出
                    // 回个确认（可选）
                    std::string bye = "{\"type\":\"bye\"}\n";
                    DWORD w = 0; WriteFile(hPipe, bye.data(), (DWORD)bye.size(), &w, nullptr);
                    break;
                }

                // ---- 新增：Granite 命令处理 ----
                if (buffer.find("\"granite_") != std::string::npos) {
                    handleGraniteCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Embedding 命令处理 ----
                if (buffer.find("\"embedding_") != std::string::npos) {
                    handleEmbeddingCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：转录命令处理 ----
                if (meetingai::proto::isTranscribe(buffer)) {
                    handleTranscribeCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：流式转录命令处理 ----
                if (meetingai::proto::isStartStream(buffer)) {
                    std::wcout << L"[Worker] 处理 start_stream 命令\n";

                    // 确保模型已加载
                    std::call_once(g_model_once2, [&] {
                        std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
                        if (!InitWhisperOnce(modelPathOnce)) {
                            std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
                            DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                        }
                    });

                    std::string mode = meetingai::proto::extractMode(buffer);
                    std::string lang = meetingai::proto::extractLanguage(buffer);

                    bool success = StartStream(mode, lang);
                    if (success) {
                        std::string resp = "{\"type\":\"stream_started\",\"mode\":\"" + mode + "\",\"language\":\"" + lang + "\"}\n";
                        DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                    } else {
                        std::string err = "{\"type\":\"error\",\"message\":\"启动流式转录失败\"}\n";
                        DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    }
                    buffer.clear();
                    continue;
                }

                if (meetingai::proto::isStreamChunk(buffer)) {
                    // 处理音频块（v1 单流）
                    std::string audioData = meetingai::proto::extractData(buffer);
                    std::vector<WhisperSegment> segments;

                    bool success = ProcessStreamChunk(audioData, segments);
                    if (success) {
                        // 发送新识别的段落
                        for (const auto& seg : segments) {
                            std::string resp = std::string("{\"type\":\"stream_segment\",\"text\":\"") +
                                meetingai::proto::jsonEscape(seg.text) +
                                "\",\"t0_ms\":" + std::to_string((int)(seg.start_time * 1000)) +
                                ",\"t1_ms\":" + std::to_string((int)(seg.end_time * 1000)) + "}\n";
                            DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                        }
                    }
                    buffer.clear();
                    continue;
                }

                if (meetingai::proto::isStopStream(buffer)) {
                    std::wcout << L"[Worker] 处理 stop_stream 命令\n";
                    StopStream();
                    std::string resp = "{\"type\":\"stream_stopped\"}\n";
                    DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                    buffer.clear();
                    continue;
                }

                // ---- v2 多流：start_stream2 / stream_chunk2 / stop_stream2 ----
                if (meetingai::proto::isStartStream2(buffer)) {
                    std::wcout << L"[Worker] 处理 start_stream2 命令\n";
                    std::call_once(g_model_once2, [&] {
                        std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
                        if (!InitWhisperOnce(modelPathOnce)) {
                            std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
                            DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                        }
                    });
                    std::string streamId = meetingai::proto::extractStreamId(buffer);
                    std::string source   = meetingai::proto::extractSource(buffer);
                    std::string mode     = meetingai::proto::extractMode(buffer);
                    std::string lang     = meetingai::proto::extractLanguage(buffer);
                    bool ok = StartStream2(streamId, source, mode, lang);
                    if (ok) {
                        std::string resp = std::string("{\"type\":\"stream_started2\",\"stream_id\":\"") + streamId +
                            "\",\"source\":\"" + source + "\",\"mode\":\"" + mode + "\",\"language\":\"" + lang + "\"}\n";
                        DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                    } else {
                        std::string err = std::string("{\"type\":\"error\",\"message\":\"start_stream2 失败\",\"stream_id\":\"") + streamId + "\"}\n";
                        DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    }
                    buffer.clear();
                    continue;
                }
                if (meetingai::proto::isStreamChunk2(buffer)) {
                    std::string streamId = meetingai::proto::extractStreamId(buffer);
                    std::string audioData = meetingai::proto::extractData(buffer);
                    int sr = meetingai::proto::extractSampleRate(buffer);
                    long long ts = meetingai::proto::extractTimestampMs(buffer);
                    std::vector<WhisperSegment> segments;
                    bool ok = ProcessStreamChunk2(streamId, audioData, segments, sr, ts);
                    if (ok) {
                        std::string source = GetStreamSource2(streamId);
                        for (const auto& seg : segments) {
                            std::string resp = std::string("{\"type\":\"stream_segment2\",\"stream_id\":\"") + streamId + "\",\"source\":\"" + source +
                                "\",\"text\":\"" + meetingai::proto::jsonEscape(seg.text) + "\",\"t0_ms\":" + std::to_string((int)(seg.start_time*1000)) +
                                ",\"t1_ms\":" + std::to_string((int)(seg.end_time*1000)) + "}\n";
                            DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                        }
                    } else {
                        std::string err = std::string("{\"type\":\"error\",\"message\":\"stream_chunk2 失败\",\"stream_id\":\"") + streamId + "\"}\n";
                        DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    }
                    buffer.clear();
                    continue;
                }
                if (meetingai::proto::isStopStream2(buffer)) {
                    std::string streamId = meetingai::proto::extractStreamId(buffer);
                    StopStream2(streamId);
                    std::string resp = std::string("{\"type\":\"stream_stopped2\",\"stream_id\":\"") + streamId + "\"}\n";
                    DWORD written; WriteFile(hPipe, resp.data(), (DWORD)resp.size(), &written, nullptr);
                    buffer.clear();
                    continue;
                }

                // 正常回显
                std::string resp = "{\"type\":\"pong\",\"echo\":\"" + buffer + "\"}\n";
                DWORD written = 0;
                if (!WriteFile(hPipe, resp.data(), static_cast<DWORD>(resp.size()), &written, nullptr)) {
                    meetingai::util::logLastError(L"[Worker] WriteFile failed");
                    break; // 写失败也结束本次连接
                }
                else {
                    std::wcout << L"[Worker] response sent to client\n";
                }

                // 清空缓冲，继续等待下一条消息
                buffer.clear();
            }
            else {
                buffer.push_back(ch);
            }
        }

        // 4) 清理当前连接，回到外层 while 等待下一个客户端或退出
        FlushFileBuffers(hPipe);
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }

    if (pSD) LocalFree(pSD);
    if (hMutex) CloseHandle(hMutex);
    std::wcout << L"[Worker] exit.\n";
    return 0;
}
