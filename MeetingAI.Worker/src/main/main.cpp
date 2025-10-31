#include "pch.h"
#include <windows.h>
#include <sddl.h>
#include <iostream>
#include <string>
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h" 
#include <filesystem> 
#include "transcriber.hpp" // 新增：包含 whisper 封装
#include <shlobj.h>      // SHGetFolderPathW
#include <codecvt>       // 宽/窄字符串转码（仅用于 Win -> UTF-8）
#include <thread>
#include <mutex>   // ★ 新增
#include "paths.h"
#include "command_parser.h"
#include "logging.h"
#include "pipe_security.h"



static std::once_flag g_model_once2; // ★ 新增：Worker 级只加载一次模型

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

    // 检查是否传入 --ppid 参数（Host PID）
    DWORD parentPid = 0;
    for (int i = 1; i < __argc; i++) {
        if (std::wstring(__wargv[i]) == L"--ppid" && i + 1 < __argc) {
            parentPid = std::wcstoul(__wargv[++i], nullptr, 10);
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
                    // 处理音频块
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
