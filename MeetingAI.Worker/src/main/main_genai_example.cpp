// main.cpp 集成 OpenVINO GenAI 版本

#include "pch.h"
#include "granite/granite_genai.hpp"
#include "embedding/embedding_genai.hpp"
#include <nlohmann/json.hpp>
#include <iostream>
#include <windows.h>

// 全局实例
static std::unique_ptr<meetingai::granite::GraniteGenAI> g_granite;
static std::unique_ptr<meetingai::embedding::EmbeddingGenAI> g_embedding;

// Whisper 转录回调需要的全局管道句柄
void* g_pipe_for_callback = nullptr;

// 初始化函数
void InitializeGraniteGenAI() {
    try {
        g_granite = std::make_unique<meetingai::granite::GraniteGenAI>(
            "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/granite-3.3-2b-npu", "NPU");
    } catch (const std::exception& e) {
        std::wcerr << L"[Main] Granite GenAI 初始化失败: " << e.what() << L"\n";
    }
}

void InitializeEmbeddingGenAI() {
    try {
        g_embedding = std::make_unique<meetingai::embedding::EmbeddingGenAI>(
            "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/bge-m3-npu", "NPU");
    } catch (const std::exception& e) {
        std::wcerr << L"[Main] Embedding GenAI 初始化失败: " << e.what() << L"\n";
    }
}

// JSON 转义辅助函数
std::string jsonEscape(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 16);
    for (unsigned char c : s) {
        switch (c) {
        case '\"': o += "\\\""; break;
        case '\\': o += "\\\\"; break;
        case '\b': o += "\\b"; break;
        case '\f': o += "\\f"; break;
        case '\n': o += "\\n"; break;
        case '\r': o += "\\r"; break;
        case '\t': o += "\\t"; break;
        default:
            if (c < 0x20) {
                char buf[7];
                std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                o += buf;
            } else {
                o += static_cast<char>(c);
            }
        }
    }
    return o;
}

// Pipe 命令处理
void HandlePipeCommands(HANDLE hPipe) {
    // 初始化
    InitializeGraniteGenAI();
    InitializeEmbeddingGenAI();
    
    char buffer[4096];
    DWORD bytesRead;
    
    while (true) {
        // 读取命令
        if (!ReadFile(hPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr)) {
            break;
        }
        
        if (bytesRead == 0) continue;
        buffer[bytesRead] = '\0';
        
        std::string cmd(buffer);
        
        try {
            auto json = nlohmann::json::parse(cmd);
            std::string type = json["type"];
            
            // ========== Granite 普通生成 ==========
            if (type == "granite_generate") {
                if (!g_granite) {
                    std::string err = "{\"type\":\"error\",\"message\":\"Granite not initialized\"}\n";
                    DWORD written;
                    WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    continue;
                }
                
                std::string prompt = json["prompt"];
                int max_tokens = json.value("max_tokens", 128);
                float temperature = json.value("temperature", 0.7f);
                
                std::string result = g_granite->generate(prompt, max_tokens, temperature);
                
                std::string response = "{\"type\":\"granite_response\",\"text\":\"" + 
                                      jsonEscape(result) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, response.data(), (DWORD)response.size(), &written, nullptr);
            }
            
            // ========== Granite 流式生成 ==========
            else if (type == "granite_generate_stream") {
                if (!g_granite) {
                    std::string err = "{\"type\":\"error\",\"message\":\"Granite not initialized\"}\n";
                    DWORD written;
                    WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    continue;
                }
                
                std::string prompt = json["prompt"];
                int max_tokens = json.value("max_tokens", 128);
                float temperature = json.value("temperature", 0.7f);
                
                g_granite->generateStream(prompt, [&](const std::string& token) {
                    std::string chunk = "{\"type\":\"token\",\"text\":\"" + 
                                       jsonEscape(token) + "\"}\n";
                    DWORD written;
                    WriteFile(hPipe, chunk.data(), (DWORD)chunk.size(), &written, nullptr);
                    FlushFileBuffers(hPipe);
                }, max_tokens, temperature);
                
                std::string done = "{\"type\":\"done\"}\n";
                DWORD written;
                WriteFile(hPipe, done.data(), (DWORD)done.size(), &written, nullptr);
            }
            
            // ========== Embedding ==========
            else if (type == "get_embedding") {
                if (!g_embedding) {
                    std::string err = "{\"type\":\"error\",\"message\":\"Embedding not initialized\"}\n";
                    DWORD written;
                    WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                    continue;
                }
                
                std::string text = json["text"];
                auto embedding = g_embedding->encode(text);
                
                std::ostringstream oss;
                oss << "{\"type\":\"embedding_response\",\"embedding\":[";
                for (size_t i = 0; i < embedding.size(); ++i) {
                    oss << embedding[i];
                    if (i < embedding.size() - 1) oss << ",";
                }
                oss << "]}\n";
                
                std::string response = oss.str();
                DWORD written;
                WriteFile(hPipe, response.data(), (DWORD)response.size(), &written, nullptr);
            }
            
        } catch (const std::exception& e) {
            std::string err = std::string("{\"type\":\"error\",\"message\":\"") + 
                             jsonEscape(e.what()) + "\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
        }
    }
}

// main 函数
int main() {
    std::wcout << L"[Main] MeetingAI Worker (GenAI) 启动中...\n";
    
    // 创建 Named Pipe
    HANDLE hPipe = CreateNamedPipeW(
        L"\\\\.\\pipe\\MeetingAI_Pipe",
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
        1, 4096, 4096, 0, nullptr
    );
    
    if (hPipe == INVALID_HANDLE_VALUE) {
        std::wcerr << L"[Main] ❌ 创建管道失败\n";
        return 1;
    }
    
    std::wcout << L"[Main] 等待客户端连接...\n";
    
    if (ConnectNamedPipe(hPipe, nullptr)) {
        std::wcout << L"[Main] ✅ 客户端已连接\n";
        HandlePipeCommands(hPipe);
    }
    
    CloseHandle(hPipe);
    return 0;
}
