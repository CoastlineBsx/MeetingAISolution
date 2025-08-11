
#include "pch.h"
#include <windows.h>
#include <sddl.h>
#include <iostream>
#include <string>
#include "db.h"
#include "paths.h"
#include "sqlite3.h" 
#include <filesystem> 
// --------- 追加：通用工具 & 退出标志 ----------
static volatile BOOL g_shutdownRequested = FALSE;

// 去掉首尾空白
static inline std::string trim(std::string s) {
    size_t a = s.find_first_not_of(" \t\r\n");
    size_t b = s.find_last_not_of(" \t\r\n");
    if (a == std::string::npos) return "";
    return s.substr(a, b - a + 1);
}

// 简单判断是否为 {"type":"quit"}（容忍空白/额外字段）
static bool isQuitMessage(const std::string& s) {
    auto t = trim(s);
    // 粗判：必须包含 "type":"quit"
    return t.find("\"type\"") != std::string::npos &&
        t.find("\"quit\"") != std::string::npos;
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




static void logLastError(const wchar_t* msg) {
    DWORD err = GetLastError();
    std::wcerr << msg << L" (code: " << err << L")\n";
}

bool createPipeSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& pSD) {
    // 调试期：允许 AppContainer 和 Everyone 访问
    LPCWSTR sddl = L"D:(A;;GA;;;AC)(A;;GA;;;WD)";
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        sddl, SDDL_REVISION_1, &pSD, nullptr)) {
        logLastError(L"[Worker] SDDL parse failed");
        return false;
    }
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = FALSE;
    sa.lpSecurityDescriptor = pSD;
    return true;
}

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

    //// ★ 新增 3: 统计当前记录数
    //{
    //    std::wstring dbw = Utf8ToW(GetDatabasePath());
    //    sqlite3* db = nullptr;
    //    if (sqlite3_open16(dbw.c_str(), &db) == SQLITE_OK) {
    //        sqlite3_stmt* st = nullptr;
    //        if (sqlite3_prepare_v2(db, "SELECT COUNT(*) FROM transcripts;", -1, &st, nullptr) == SQLITE_OK) {
    //            if (sqlite3_step(st) == SQLITE_ROW) {
    //                int cnt = sqlite3_column_int(st, 0);
    //                std::wcout << L"[DB] 当前总记录数: " << cnt << L"\n";
    //            }
    //            sqlite3_finalize(st);
    //        }
    //        sqlite3_close(db);
    //    }
    //    else {
    //        std::wcerr << L"[Worker] 无法打开数据库！\n";
    //    }
    //}
// ★ 新增 3：打印 DB 路径、是否存在、记录总数（ASCII 输出，避免中文编码问题）
    {
        std::string dbPath = GetDatabasePath();
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
    if (!createPipeSecurity(sa, pSD)) return 1;

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
            logLastError(L"[Worker] CreateNamedPipe failed");
            break;
        }

        std::wcout << L"[Worker] pipe created, waiting for client...\n";

        // 2) 等待客户端
        BOOL connected = ConnectNamedPipe(hPipe, nullptr) ? TRUE :
            (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected) {
            logLastError(L"[Worker] ConnectNamedPipe failed");
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
                    logLastError(L"[Worker] ReadFile failed");
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
                if (isQuitMessage(buffer)) {
                    std::wcout << L"[Worker] quit requested\n";
                    shutdownRequested = true; // 进程级退出
                    // 回个确认（可选）
                    std::string bye = "{\"type\":\"bye\"}\n";
                    DWORD w = 0; WriteFile(hPipe, bye.data(), (DWORD)bye.size(), &w, nullptr);
                    break;
                }


                // 正常回显
                std::string resp = "{\"type\":\"pong\",\"echo\":\"" + buffer + "\"}\n";
                DWORD written = 0;
                if (!WriteFile(hPipe, resp.data(), static_cast<DWORD>(resp.size()), &written, nullptr)) {
                    logLastError(L"[Worker] WriteFile failed");
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
