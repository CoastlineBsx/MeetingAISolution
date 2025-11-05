// main.cpp 集成 OpenVINO GenAI 版本（支持多轮对话 + 管道）

#include "pch.h"
#include "granite/granite_genai.hpp"
#include "embedding/embedding_genai.hpp"
#include "rag/text_chunker.hpp"
#include "database.hpp"
#include <nlohmann/json.hpp>
#include <iostream>
#include <windows.h>
#include <cstdlib>
#include <sstream>
#include <string>
#include <memory>

void* g_pipe_for_callback = nullptr;

// ========== 全局实例 ==========
static std::unique_ptr<meetingai::granite::GraniteGenAI> g_granite;
static std::unique_ptr<meetingai::embedding::EmbeddingGenAI> g_embedding;

// ========== 运行时可调参数（聊天/单轮共用） ==========
static std::string  g_system_prompt = "你是一个谨慎、简洁的中文助手。回答使用简体中文，分点列出要点。";
static int          g_max_tokens = 256;
static float        g_temperature = 0.7f;

// ========== 工具函数 ==========
std::string GetEnvOrDefault(const char* key, const char* fallback) {
    char* buf = nullptr;
    size_t len = 0;

    // _dupenv_s 自动分配内存，线程安全
    if (_dupenv_s(&buf, &len, key) == 0 && buf != nullptr) {
        std::string value(buf);
        free(buf); // 别忘记释放
        if (!value.empty()) {
            return value;
        }
    }
    return std::string(fallback);
}


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
            }
            else {
                o += static_cast<char>(c);
            }
        }
    }
    return o;
}

// ========== 初始化 ==========
void InitializeGraniteGenAI(const std::string& device = "CPU") {
    try {
        const std::string model_dir =
            GetEnvOrDefault("MEETINGAI_GRANITE_MODEL",
                "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/granite-3.3-2b-npu");
        g_granite = std::make_unique<meetingai::granite::GraniteGenAI>(model_dir, device);
        std::wcout << L"[Main] Granite GenAI ✅ Initialized: " << device.c_str() << L"\n";
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Main] Granite GenAI 初始化失败: " << e.what() << L"\n";
    }
}

void InitializeEmbeddingGenAI(const std::string& device = "CPU") {
    try {
        const std::string default_model_dir =
            "C:/VisualStudio/MeetingAISolution/MeetingAI.Worker/models/bge-m3-npu";
        const std::string model_dir =
            GetEnvOrDefault("MEETINGAI_EMBEDDING_MODEL", default_model_dir.c_str());
        g_embedding = std::make_unique<meetingai::embedding::EmbeddingGenAI>(model_dir, device);
        std::wcout << L"[Main] Embedding GenAI ✅ Initialized: " << device.c_str() << L"\n";
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Main] Embedding GenAI 初始化失败: " << e.what() << L"\n";
    }
}

// ========== 管道命令处理 ==========
void HandlePipeCommands(HANDLE hPipe) {
    InitializeGraniteGenAI("GPU");
    InitializeEmbeddingGenAI("GPU");

    char buffer[4096];
    DWORD bytesRead;

    while (true) {
        if (!ReadFile(hPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr)) break;
        if (bytesRead == 0) continue;
        buffer[bytesRead] = '\0';

        try {
            auto json = nlohmann::json::parse(buffer);
            std::string type = json.value("type", "");

            auto write_json = [&](const std::string& payload) {
                DWORD written;
                WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
                };

            if (!g_granite && (type.rfind("granite", 0) == 0)) {
                write_json("{\"type\":\"error\",\"message\":\"Granite not initialized\"}\n");
                continue;
            }

            // -------- 单轮（无上下文）--------
            if (type == "granite_generate") {
                std::string prompt = json.value("prompt", "");
                int   max_tokens = json.value("max_tokens", g_max_tokens);
                float temperature = json.value("temperature", g_temperature);

                std::string result = g_granite->generate(prompt, max_tokens, temperature);
                std::string resp = "{\"type\":\"granite_response\",\"text\":\"" + jsonEscape(result) + "\"}\n";
                write_json(resp);
            }
            else if (type == "granite_generate_stream") {
                std::string prompt = json.value("prompt", "");
                int   max_tokens = json.value("max_tokens", g_max_tokens);
                float temperature = json.value("temperature", g_temperature);

                g_granite->generateStream(prompt, [&](const std::string& token) {
                    std::string chunk = "{\"type\":\"token\",\"text\":\"" + jsonEscape(token) + "\"}\n";
                    write_json(chunk);
                    FlushFileBuffers(hPipe);
                    }, max_tokens, temperature);
                write_json("{\"type\":\"done\"}\n");
            }

            // -------- 多轮：开始/结束会话 --------
            else if (type == "granite_start_chat") {
                std::string sys = json.value("system_message", g_system_prompt);
                g_system_prompt = sys;
                g_granite->startChat(sys);
                write_json("{\"type\":\"granite_chat_status\",\"status\":\"started\"}\n");
            }
            else if (type == "granite_finish_chat") {
                g_granite->finishChat();
                write_json("{\"type\":\"granite_chat_status\",\"status\":\"finished\"}\n");
            }

            // -------- 多轮：同步/流式 --------
            else if (type == "granite_chat") {
                std::string user = json.value("prompt", "");
                int   max_tokens = json.value("max_tokens", g_max_tokens);
                float temp = json.value("temperature", g_temperature);

                std::string result = g_granite->chat(user, max_tokens, temp);
                std::string resp = "{\"type\":\"granite_response\",\"text\":\"" + jsonEscape(result) + "\"}\n";
                write_json(resp);
            }
            else if (type == "granite_chat_stream") {
                std::string user = json.value("prompt", "");
                int   max_tokens = json.value("max_tokens", g_max_tokens);
                float temp = json.value("temperature", g_temperature);

                g_granite->chatStream(user, [&](const std::string& tok) {
                    std::string chunk = "{\"type\":\"token\",\"text\":\"" + jsonEscape(tok) + "\"}\n";
                    write_json(chunk);
                    FlushFileBuffers(hPipe);
                    }, max_tokens, temp);
                write_json("{\"type\":\"done\"}\n");
            }

            // -------- 向量 --------
            else if (type == "get_embedding") {
                if (!g_embedding) {
                    write_json("{\"type\":\"error\",\"message\":\"Embedding not initialized\"}\n");
                    continue;
                }
                std::string text = json.value("text", "");
                auto emb = g_embedding->encode(text);

                std::ostringstream oss;
                oss << "{\"type\":\"embedding_response\",\"embedding\":[";
                for (size_t i = 0; i < emb.size(); ++i) {
                    oss << emb[i];
                    if (i + 1 < emb.size()) oss << ",";
                }
                oss << "]}\n";
                write_json(oss.str());
            }

            // -------- RAG 查询 --------
            else if (type == "rag_query") {
                if (!g_embedding || !g_granite) {
                    write_json("{\"type\":\"error\",\"message\":\"Models not initialized\"}\n");
                    continue;
                }

                std::string query = json.value("query", "");
                int top_k = json.value("top_k", 3);
                int max_tokens = json.value("max_tokens", g_max_tokens);
                float temp = json.value("temperature", g_temperature);

                // 1. 生成 query embedding
                auto qvec = g_embedding->encode(query);

                // 2. 检索相关文档
                auto chunks = RetrieveTopK(qvec, top_k);

                if (chunks.empty()) {
                    write_json("{\"type\":\"error\",\"message\":\"No documents found in database\"}\n");
                    continue;
                }

                // 3. 构建 RAG Prompt
                std::ostringstream prompt;
                prompt << "参考以下文档内容回答问题：\n\n";
                for (size_t i = 0; i < chunks.size(); i++) {
                    prompt << "[文档片段 " << (i + 1) << "]\n" << chunks[i].text << "\n\n";
                }
                prompt << "问题：" << query << "\n\n请基于上述文档内容回答：";

                // 4. 流式生成
                g_granite->generateStream(prompt.str(), [&](const std::string& tok) {
                    std::string chunk = "{\"type\":\"token\",\"text\":\"" + jsonEscape(tok) + "\"}\n";
                    write_json(chunk);
                    FlushFileBuffers(hPipe);
                    }, max_tokens, temp);

                write_json("{\"type\":\"done\"}\n");
            }
            else {
                write_json("{\"type\":\"error\",\"message\":\"Unknown command type\"}\n");
            }
        }
        catch (const std::exception& e) {
            std::string err = std::string("{\"type\":\"error\",\"message\":\"") + jsonEscape(e.what()) + "\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
        }
    }
}

// ========== 命令行多轮对话 ==========
void PrintHelp() {
    std::cout << "Commands:\n"
        << "  /new                 - reset conversation (clear history)\n"
        << "  /sys <text>          - set system prompt and reset\n"
        << "  /temp <0..1>         - set temperature\n"
        << "  /max <n>             - set max_new_tokens\n"
        << "  /once <question>     - single-turn generation (no context)\n"
        << "  clear / exit / quit  - clear screen / leave\n\n";
}

void RunChatMode() {
    SetConsoleOutputCP(CP_UTF8);

    std::cout << "\n========================================\n";
    std::cout << "  Granite AI Chat Mode (with context)\n";
    std::cout << "========================================\n\n";
    std::cout << "[Initializing] Loading Granite model...\n";
    std::cout.flush();

    InitializeGraniteGenAI("CPU");
    if (!g_granite) {
        std::cout << "\n[ERROR] Granite initialization failed!\n";
        return;
    }

    // 进入会话并设置默认 system prompt
    g_granite->startChat(g_system_prompt);

    std::cout << "\n[SUCCESS] Model loaded! Let's chat~\n";
    std::cout << "Default system prompt: " << g_system_prompt << "\n";
    std::cout << "Temperature: " << g_temperature << " | Max tokens: " << g_max_tokens << "\n\n";
    PrintHelp();

    while (true) {
        std::cout << "========================================\nYou: ";
        std::cout.flush();

        std::string input;
        if (!std::getline(std::cin, input)) break;

        // trim
        auto l = input.find_first_not_of(" \t\r\n");
        auto r = input.find_last_not_of(" \t\r\n");
        if (l == std::string::npos) continue;
        input = input.substr(l, r - l + 1);

        if (input == "exit" || input == "quit") {
            std::cout << "\nGoodbye!\n";
            break;
        }
        if (input == "clear") {
            system("cls");
            PrintHelp();
            continue;
        }

        // 命令处理
        if (input.rfind("/new", 0) == 0) {
            g_granite->finishChat();
            g_granite->startChat(g_system_prompt);
            std::cout << "[ok] conversation reset.\n";
            continue;
        }
        if (input.rfind("/sys ", 0) == 0) {
            g_system_prompt = input.substr(5);
            g_granite->finishChat();
            g_granite->startChat(g_system_prompt);
            std::cout << "[ok] system prompt set and conversation reset.\n";
            continue;
        }
        if (input.rfind("/temp ", 0) == 0) {
            try {
                g_temperature = std::stof(input.substr(6));
                std::cout << "[ok] temperature = " << g_temperature << "\n";
            }
            catch (...) { std::cout << "[err] invalid value.\n"; }
            continue;
        }
        if (input.rfind("/max ", 0) == 0) {
            try {
                g_max_tokens = std::stoi(input.substr(5));
                std::cout << "[ok] max_new_tokens = " << g_max_tokens << "\n";
            }
            catch (...) { std::cout << "[err] invalid value.\n"; }
            continue;
        }
        if (input.rfind("/once ", 0) == 0) {
            std::string q = input.substr(6);
            std::cout << "Granite: ";
            try {
                // 单轮（无上下文）
                std::string res = g_granite->generate(q, g_max_tokens, g_temperature);
                std::cout << res << "\n";
            }
            catch (const std::exception& e) {
                std::cout << "\n[ERROR] Generation failed: " << e.what() << "\n";
            }
            continue;
        }

        // 多轮流式
        std::cout << "Granite: ";
        try {
            g_granite->chatStream(input, [](const std::string& t) {
                std::cout << t << std::flush;
                }, g_max_tokens, g_temperature);
            std::cout << "\n";
        }
        catch (const std::exception& e) {
            std::cout << "\n[ERROR] Generation failed: " << e.what() << "\n";
        }
    }

    // 退出前收尾
    try { g_granite->finishChat(); }
    catch (...) {}
}

// ========== main ==========
int main(int argc, char** argv) {
    // ===== 测试 Granite =====
    if (argc > 1 && std::string(argv[1]) == "--test-granite") {
        SetConsoleOutputCP(CP_UTF8);
        std::cout << "[Test] Initializing Granite model...\n";

        InitializeGraniteGenAI("GPU");
        if (!g_granite) {
            std::cout << "❌ Granite initialization failed!\n";
            return 1;
        }

        std::cout << "[Test] Generating text...\n";
        std::string prompt = "What is OpenVINO?";
        std::string response;

        g_granite->generateStream(prompt, [&response](const std::string& token) {
            std::cout << token << std::flush;
            response += token;
        }, 100, 0.7f);

        std::cout << "\n\n✅ Generation complete, length: " << response.size() << "\n";
        return 0;
    }

    // ===== 测试 Embedding =====
    if (argc > 1 && std::string(argv[1]) == "--test-embedding") {
        SetConsoleOutputCP(CP_UTF8);
        std::cout << "[Test] Initializing Embedding model...\n";

        InitializeEmbeddingGenAI("GPU");
        if (!g_embedding) {
            std::cout << "❌ Embedding initialization failed!\n";
            return 1;
        }

        std::cout << "[Test] Encoding text...\n";
        auto vec = g_embedding->encode("测试文本：OpenVINO GenAI is awesome!");

        std::cout << "✅ Embedding size: " << vec.size() << "\n";
        std::cout << "First 10 values: ";
        for (int i = 0; i < 10 && i < vec.size(); i++) {
            std::cout << vec[i] << " ";
        }
        std::cout << "\n";
        return 0;
    }

    // ===== 测试 RAG 入库 =====
    if (argc > 1 && std::string(argv[1]) == "--test-ingest") {
        SetConsoleOutputCP(CP_UTF8);
        std::cout << "[Test] RAG Document Ingestion Test\n";

        if (!InitDatabaseOnce()) {
            std::cout << "❌ Database init failed!\n";
            return 1;
        }

        InitializeEmbeddingGenAI("CPU");
        if (!g_embedding) {
            std::cout << "❌ Embedding init failed!\n";
            return 1;
        }

        // 测试文档
        std::string test_doc =
            "OpenVINO 是 Intel 开发的深度学习推理优化工具包。"
            "它支持多种硬件加速，包括 CPU、GPU、NPU 等。"
            "Granite 是 IBM 推出的开源大语言模型系列。"
            "Granite 3.3 2B 模型支持 128K 上下文长度。"
            "BGE-M3 是优秀的多语言嵌入模型，支持中英文检索。";

        std::cout << "[Test] Chunking text...\n";
        auto chunks = meetingai::rag::chunkText(test_doc, 100);  // 每块最多100字
        std::cout << "✅ Created " << chunks.size() << " chunks\n";

        std::cout << "[Test] Generating embeddings...\n";
        std::vector<std::string> chunk_texts;
        std::vector<std::vector<float>> embeddings;

        for (const auto& chunk : chunks) {
            chunk_texts.push_back(chunk.text);
            auto emb = g_embedding->encode(chunk.text);
            embeddings.push_back(emb);
            std::cout << "  Chunk " << chunk_texts.size() << ": " << chunk.text.substr(0, 50) << "...\n";
        }

        std::cout << "[Test] Inserting into database...\n";
        int doc_id = InsertDocument("测试文档", "txt", "test.txt", chunk_texts, embeddings);

        if (doc_id > 0) {
            std::cout << "✅ Document inserted with id=" << doc_id << "\n";
        } else {
            std::cout << "❌ Insert failed!\n";
        }

        return 0;
    }

    // ===== 测试 RAG 查询 =====
    if (argc > 1 && std::string(argv[1]) == "--test-rag") {
        SetConsoleOutputCP(CP_UTF8);
        std::cout << "[Test] RAG Query Test\n";

        if (!InitDatabaseOnce()) {
            std::cout << "❌ Database init failed!\n";
            return 1;
        }

        InitializeEmbeddingGenAI("CPU");
        InitializeGraniteGenAI("CPU");

        if (!g_embedding || !g_granite) {
            std::cout << "❌ Model init failed!\n";
            return 1;
        }

        std::string query = "Granite 模型的上下文长度是多少？";
        std::cout << "[Test] Query: " << query << "\n\n";

        // 1. 生成 query embedding
        auto qvec = g_embedding->encode(query);
        std::cout << "[Test] Query embedding size: " << qvec.size() << "\n";

        // 2. 检索
        auto chunks = RetrieveTopK(qvec, 3);
        std::cout << "[Test] Retrieved " << chunks.size() << " chunks:\n";
        for (const auto& c : chunks) {
            std::cout << "  [相似度=" << c.similarity << "] " << c.text.substr(0, 60) << "...\n";
        }

        // 3. 构建 Prompt
        std::ostringstream prompt;
        prompt << "参考以下文档内容回答问题：\n\n";
        for (size_t i = 0; i < chunks.size(); i++) {
            prompt << "[文档片段 " << (i + 1) << "]\n" << chunks[i].text << "\n\n";
        }
        prompt << "问题：" << query << "\n\n请基于上述文档内容回答：";

        std::cout << "\n[Test] Generating answer...\n";
        std::cout << "Answer: ";

        g_granite->generateStream(prompt.str(), [](const std::string& tok) {
            std::cout << tok << std::flush;
            }, 512, 0.7f);

        std::cout << "\n\n✅ RAG test complete!\n";
        return 0;
    }

    bool chatMode = false;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--chat" || a == "-c") chatMode = true;
    }

    if (chatMode) {
        RunChatMode();
        return 0;
    }

    std::wcout << L"[Main] MeetingAI Worker (GenAI) 启动中...\n";

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
