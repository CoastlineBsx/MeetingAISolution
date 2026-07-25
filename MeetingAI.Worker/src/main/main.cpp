
#include <windows.h>
#include <sddl.h>
#include <iostream>
#include <string>
#include <memory>      // ← 新增：unique_ptr
#include <functional>  // ← 新增：function
#include <mutex>
#include <thread>
#include <chrono>
#include <filesystem>
#include <shlobj.h>
#include <codecvt>

// 然后包含项目头文件
#include "database.hpp"
#include "paths.h"
#include "sqlite3.h"
#include "transcriber.hpp"
#include "whisper_openvino_transcriber.hpp"  // ← 新增：OpenVINO Whisper
#include "granite/granite_genai.hpp"  // ← OpenVINO 头文件
#include "embedding/embedding_genai.hpp"  // ← 新增：Embedding GenAI
#include "llava/llava_genai.h"  // ← 新增：LLaVA GenAI
#include "sd/sd_engine.hpp"  // ← 新增：Stable Diffusion
#include "sherpa_streaming_transcriber.h"  // ← 新增：Sherpa 流式转录
#include "punctuator.hpp"                  // ← 新增：中英标点恢复
#include "transcript_text_normalizer.hpp"
#include "base64.h"  // ← 新增：Base64 解码
#include "command_parser.h"
#include "logging.h"
#include "pipe_security.h"

// OpenVINO Core for device enumeration
#include <openvino/openvino.hpp>

// Whisper context 外部声明（定义在 whisper_transcriber.cpp 中）
extern struct whisper_context* g_whisper_ctx;
extern void CleanupWhisper();  // Whisper 清理函数

// ========== 热拔插支持：使用 mutex + bool 替代 once_flag ==========
static std::mutex g_whisper_mutex;
static bool g_whisper_loaded = false;

static std::mutex g_granite_mutex;
static bool g_granite_loaded = false;

static std::mutex g_embedding_mutex;
static bool g_embedding_loaded = false;

static std::mutex g_llava_mutex;
static bool g_llava_loaded = false;

static std::mutex g_sd_mutex;
static bool g_sd_loaded = false;

static std::mutex g_sherpa_mutex;
static bool g_sherpa_loaded = false;

// ========== Granite GenAI 全局实例 ==========
static std::unique_ptr<meetingai::granite::GraniteGenAI> g_granite;
static std::string g_system_prompt = "你是一个专业、简洁的中文助手。请用简体中文回答问题，注重逻辑性和条理性。";
static int g_max_tokens = 256;
static float g_temperature = 0.7f;

// ========== Embedding GenAI 全局实例 ==========
static std::unique_ptr<meetingai::embedding::EmbeddingGenAI> g_embedding;

// ========== LLaVA GenAI 全局实例 ==========
static std::unique_ptr<llava::LLaVAGenAI> g_llava;

// ========== Stable Diffusion 全局实例 ==========
static std::unique_ptr<meetingai::sd::SDEngine> g_sd;

// ========== Sherpa-ONNX 流式转录全局实例 ==========
static std::unique_ptr<meetingai::transcribe::SherpaStreamingTranscriber> g_sherpa;
// 标点模型：可选，缺失时转录照常工作，只是不加标点（受 g_sherpa_mutex 保护）
static std::unique_ptr<meetingai::transcribe::Punctuator> g_punct;
static bool g_punct_attempted = false;
// Sherpa endpoint 只是声学分段，不等于一句话。保留尚未获得足够语义前瞻的
// 原始文本，直到标点模型确认句界或检测到长静音/停止。
static std::string g_streaming_pending_raw;

// ========== 设备配置 ==========
static std::string g_granite_device = "GPU";   // Granite LLM 使用的设备
static std::string g_embedding_device = "GPU"; // Embedding 使用的设备
static std::string g_llava_device = "NPU";     // LLaVA 使用的设备
static std::string g_sd_device = "NPU";        // Stable Diffusion 使用的设备

// ========== 工具函数：解码 JSON Unicode 转义序列 ==========
static std::string decodeJsonUnicode(const std::string& str) {
    std::string result;
    result.reserve(str.length());

    for (size_t i = 0; i < str.length(); i++) {
        if (str[i] == '\\' && i + 5 < str.length() && str[i + 1] == 'u') {
            // 解析 \uXXXX
            std::string hex = str.substr(i + 2, 4);
            try {
                int code_point = std::stoi(hex, nullptr, 16);

                // 将 Unicode 码点转换为 UTF-8
                if (code_point <= 0x7F) {
                    result += static_cast<char>(code_point);
                } else if (code_point <= 0x7FF) {
                    result += static_cast<char>(0xC0 | ((code_point >> 6) & 0x1F));
                    result += static_cast<char>(0x80 | (code_point & 0x3F));
                } else {
                    result += static_cast<char>(0xE0 | ((code_point >> 12) & 0x0F));
                    result += static_cast<char>(0x80 | ((code_point >> 6) & 0x3F));
                    result += static_cast<char>(0x80 | (code_point & 0x3F));
                }
                i += 5; // 跳过 \uXXXX
            } catch (...) {
                result += str[i]; // 解析失败，保留原字符
            }
        } else {
            result += str[i];
        }
    }

    return result;
}

// ========== 工具函数：获取环境变量 ==========
static std::string GetEnvOrDefault(const char* key, const std::string& fallback) {
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


// --------- 追加：通用工具 & 退出标志 ----------
static volatile BOOL g_shutdownRequested = FALSE;
// 用于回调里把段结果写回 Host
HANDLE g_pipe_for_callback = NULL;


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
            meetingai::util::resolveModelFileUtf8(L"granite-3.3-2b-npu")
        );

        g_granite = std::make_unique<meetingai::granite::GraniteGenAI>(model_dir, device);
        std::wcout << L"[Worker] Granite GenAI ✅ 初始化成功: " << device.c_str() << L"\n";

        // 通知 Host 模型已就绪
        std::string ready = "{\"type\":\"granite_ready\",\"device\":\"" + device + "\"}\n";
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
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
        devices_msg += "  将使用: " + device + "\"}";

        DWORD written;
        WriteFile(hPipe, devices_msg.data(), (DWORD)devices_msg.size(), &written, nullptr);

        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_EMBEDDING_MODEL",
            meetingai::util::resolveModelFileUtf8(L"bge-m3-npu")
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

// ========== LLaVA GenAI 初始化 ==========
static void InitializeLLaVAGenAI(HANDLE hPipe, const std::string& device = "NPU") {
    std::wcout << L"[Worker] 初始化 LLaVA GenAI...\n";
    try {
        // 使用模型目录路径（包含所有 LLaVA 模型文件）
        const std::string model_path = GetEnvOrDefault(
            "MEETINGAI_LLAVA_MODEL",
            meetingai::util::resolveModelFileUtf8(L"llava")
        );

        // 使用 VLMPipeline API，直接传入模型目录和设备
        g_llava = std::make_unique<llava::LLaVAGenAI>(model_path, device);

        std::wcout << L"[Worker] LLaVA GenAI ✅ 初始化成功: " << device.c_str() << L"\n";

        // 通知 Host 模型已就绪
        std::string ready = std::string("{\"type\":\"llava_ready\",\"device\":\"") + device + "\"}\n";
        DWORD written;
        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] LLaVA GenAI ❌ 初始化失败: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"LLaVA 初始化失败: ") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== LLaVA GenAI 命令处理 ==========
static void handleLLaVACommand(HANDLE hPipe, const std::string& command) {
    try {
        // ========== 新增：独立的 load_llava 命令处理 ==========
        if (command.find("\"type\":\"load_llava\"") != std::string::npos) {
            DWORD written;

            std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 收到 load_llava 命令\"}\n";
            WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

            // 解析设备参数
            std::string device = "GPU";  // 默认使用 GPU
            auto devicePos = command.find("\"device\":\"");
            if (devicePos != std::string::npos) {
                auto start = devicePos + 10;
                auto end = command.find("\"", start);
                if (end != std::string::npos) {
                    device = command.substr(start, end - start);
                }
            }

            std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker] LLaVA 设备: " + device + "\"}\n";
            WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

            std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker] 开始加载 LLaVA 模型（这可能需要 30-60 秒）...\"}\n";
            WriteFile(hPipe, debug3.data(), (DWORD)debug3.size(), &written, nullptr);

            // 调用初始化函数（支持热拔插）
            {
                std::lock_guard<std::mutex> lock(g_llava_mutex);
                if (!g_llava_loaded) {
                    InitializeLLaVAGenAI(hPipe, device);
                    g_llava_loaded = true;
                }
            }

            std::string debug4 = "{\"type\":\"info\",\"message\":\"[Worker] LLaVA 加载完成\"}\n";
            WriteFile(hPipe, debug4.data(), (DWORD)debug4.size(), &written, nullptr);

            return;
        }

        // 检查模型是否已加载
        if (!g_llava) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ LLaVA 模型未加载，请先点击'加载LLaVA模型'按钮\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        auto write_json = [&](const std::string& payload) {
            DWORD written;
            WriteFile(hPipe, payload.data(), (DWORD)payload.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 辅助函数：提取 image_path 字段
        auto extractImagePath = [](const std::string& json) -> std::string {
            size_t pos = json.find("\"image_path\"");
            if (pos == std::string::npos) return "";
            size_t colonPos = json.find(":", pos);
            if (colonPos == std::string::npos) return "";
            size_t quoteStart = json.find("\"", colonPos);
            if (quoteStart == std::string::npos) return "";
            size_t quoteEnd = json.find("\"", quoteStart + 1);
            if (quoteEnd == std::string::npos) return "";
            return json.substr(quoteStart + 1, quoteEnd - quoteStart - 1);
        };

        // -------- 单轮模式：生成 --------
        if (command.find("\"llava_generate\"") != std::string::npos) {
            std::string image_path = extractImagePath(command);
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, 512);
            float temp = meetingai::proto::extractTemperature(command, 0.7f);

            std::wcout << L"[LLaVA] 单轮生成: " << image_path.c_str() << L"\n";

            g_llava->generateStream(image_path, prompt, [&](const std::string& token) {
                std::string chunk = "{\"type\":\"llava_token\",\"token\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"llava_complete\"}\n");
        }
        // -------- 多轮模式：开始会话 --------
        else if (command.find("\"llava_start_chat\"") != std::string::npos) {
            std::string image_path = extractImagePath(command);
            g_llava->startChat(image_path);
            write_json("{\"type\":\"llava_chat_status\",\"status\":\"started\"}\n");
            std::wcout << L"[LLaVA] 多轮会话已开始\n";
        }
        // -------- 多轮模式：流式对话 --------
        else if (command.find("\"llava_chat_stream\"") != std::string::npos) {
            std::string prompt = meetingai::proto::extractPrompt(command);
            int maxTokens = meetingai::proto::extractMaxTokens(command, 512);
            float temp = meetingai::proto::extractTemperature(command, 0.7f);

            std::wcout << L"[LLaVA] 多轮对话: " << prompt.c_str() << L"\n";

            g_llava->chatStream(prompt, [&](const std::string& token) {
                std::string chunk = "{\"type\":\"llava_token\",\"token\":\"" +
                    meetingai::proto::jsonEscape(token) + "\"}\n";
                write_json(chunk);
            }, maxTokens, temp);

            write_json("{\"type\":\"llava_complete\"}\n");
        }
        // -------- 多轮模式：结束会话 --------
        else if (command.find("\"llava_finish_chat\"") != std::string::npos) {
            g_llava->finishChat();
            write_json("{\"type\":\"llava_chat_status\",\"status\":\"finished\"}\n");
            std::wcout << L"[LLaVA] 多轮会话已结束\n";
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[LLaVA] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Granite GenAI 命令处理 ==========
static void handleGraniteCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 检查模型是否已加载（不再自动懒加载）
        if (!g_granite) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Granite 模型未加载，请先点击'开始加载模型'按钮\"}\n";
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
        // 检查模型是否已加载（不再自动懒加载）
        if (!g_embedding) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Embedding 模型未加载，请先点击'开始加载模型'按钮\"}\n";
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
        else if (command.find("\"embedding_test_similarity\"") != std::string::npos) {
            // 诊断测试：测试多组文本对的相似度
            std::wcout << L"[Embedding] 开始相似度诊断测试...\n";

            // 测试用的文本对（覆盖不同类型）
            std::vector<std::pair<std::string, std::string>> test_pairs = {
                // === 组1：简单问候 vs 专业内容 ===
                {"你好", "量子力学的基本原理包括波粒二象性和不确定性原理"},
                {"你好", "白少雄在伦敦大学学院攻读法学硕士"},
                {"早上好", "神经网络的反向传播算法是深度学习的核心"},
                {"谢谢", "区块链技术采用分布式账本来确保数据安全"},

                // === 组2：日常对话 vs 技术内容 ===
                {"今天天气如何", "深度学习模型使用反向传播算法进行训练"},
                {"吃饭了吗", "自然语言处理需要大量的语料库进行训练"},
                {"周末愉快", "卷积神经网络广泛应用于图像识别领域"},

                // === 组3：单个词 vs 长文本 ===
                {"苹果", "TCP/IP协议是互联网通信的基础协议栈"},
                {"学习", "人工智能的发展需要数学、统计学和计算机科学的结合"},
                {"电脑", "量子计算机利用量子叠加态进行并行计算"},

                // === 组4：人名相关（应该高相似度）===
                {"白少雄", "白少雄是一个计算机科学博士生"},
                {"白少雄", "白少雄在伦敦大学学院学习"},
                {"白少雄研究", "白少雄的研究方向是人工智能与法律"},

                // === 组5：语义相关（应该中等相似度）===
                {"机器学习", "人工智能是计算机科学的一个重要分支"},
                {"深度学习", "神经网络是模拟人脑工作的计算模型"},
                {"算法", "数据结构是计算机程序设计的基础"},

                // === 组6：完全无关 ===
                {"猫", "火箭发射需要精确的轨道计算"},
                {"音乐", "化学反应的速率取决于温度和催化剂"},
                {"颜色", "经济学研究资源的稀缺性和配置效率"},

                // === 组7：抽象概念 vs 具体描述 ===
                {"爱情", "心理学家认为人际关系建立在相互理解的基础上"},
                {"自由", "政治哲学探讨个人权利与社会责任的平衡"},
                {"科学", "实验方法是验证假设的重要手段"},

                // === 组8：短语 vs 相关内容 ===
                {"人工智能应用", "机器学习在医疗诊断中发挥重要作用"},
                {"数据分析", "统计学方法帮助我们从数据中提取有价值的信息"},
                {"编程语言", "Python因其简洁的语法而受到数据科学家的青睐"},

                // === 组9：短语 vs 不相关内容 ===
                {"编程学习", "美食烹饪需要掌握火候和调味技巧"},
                {"数学公式", "旅游景点的选择应考虑季节和交通便利性"},
                {"计算机网络", "园艺爱好者应该了解植物的生长习性"}
            };

            std::string result = "{\"type\":\"similarity_test_result\",\"pairs\":[";

            for (size_t i = 0; i < test_pairs.size(); i++) {
                const auto& pair = test_pairs[i];

                // 计算两个文本的 embedding
                auto emb1 = g_embedding->encode(pair.first);
                auto emb2 = g_embedding->encode(pair.second);

                // 计算余弦相似度
                float dot = 0.0f, norm1 = 0.0f, norm2 = 0.0f;
                for (size_t j = 0; j < emb1.size(); j++) {
                    dot += emb1[j] * emb2[j];
                    norm1 += emb1[j] * emb1[j];
                    norm2 += emb2[j] * emb2[j];
                }
                float similarity = dot / (sqrtf(norm1) * sqrtf(norm2));

                // 构建 JSON
                result += "{\"text1\":\"" + meetingai::proto::jsonEscape(pair.first) + "\",";
                result += "\"text2\":\"" + meetingai::proto::jsonEscape(pair.second) + "\",";
                result += "\"similarity\":" + std::to_string(similarity) + "}";

                if (i < test_pairs.size() - 1) result += ",";

                std::wcout << L"[Test] '" << pair.first.c_str() << L"' vs '"
                          << pair.second.c_str() << L"' = " << similarity << L"\n";
            }

            result += "]}\n";
            write_json(result);
            std::wcout << L"[Embedding] ✅ 诊断测试完成\n";
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

// ========== Stable Diffusion 初始化 ==========
static void InitializeSDEngine(HANDLE hPipe, const std::string& device = "NPU") {
    std::wcout << L"[Worker] 初始化 Stable Diffusion 引擎...\n";
    try {
        const std::string model_dir = GetEnvOrDefault(
            "MEETINGAI_SD_MODEL",
            meetingai::util::resolveModelFileUtf8(L"stable-deffusion-1.5")
        );

        std::string info_msg = "{\"type\":\"info\",\"message\":\"[SD] 正在加载模型: " + model_dir + " (" + device + ")\"}\n";
        DWORD written;
        WriteFile(hPipe, info_msg.data(), (DWORD)info_msg.size(), &written, nullptr);

        g_sd = std::make_unique<meetingai::sd::SDEngine>(model_dir, device);

        if (g_sd->isInitialized()) {
            std::wcout << L"[Worker] ✅ Stable Diffusion 初始化成功\n";
            std::string success = "{\"type\":\"sd_ready\",\"message\":\"✅ SD 引擎已就绪\"}\n";
            WriteFile(hPipe, success.data(), (DWORD)success.size(), &written, nullptr);
        } else {
            throw std::runtime_error("SD Engine initialization failed");
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] ❌ SD 初始化失败: " << e.what() << L"\n";
        std::string error = std::string("{\"type\":\"error\",\"message\":\"SD 初始化失败: ") +
                           meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), (DWORD)error.size(), &written, nullptr);
    }
}

// ========== Stable Diffusion 命令处理 ==========
static void handleSDCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理 SD 生成命令\n";
    
    try {
        // 确保 SD 引擎已加载
        if (!g_sd) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ SD 引擎未加载\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 解析命令参数
        meetingai::sd::GenerationConfig config;
        
        // 提取 mode (text2img / img2img)
        std::string mode = "text2img";
        size_t mode_pos = command.find("\"mode\":\"");
        if (mode_pos != std::string::npos) {
            size_t start = mode_pos + 8;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                mode = command.substr(start, end - start);
            }
        }

        // 提取 prompt（解码 Unicode 转义序列）
        size_t prompt_pos = command.find("\"prompt\":\"");
        if (prompt_pos != std::string::npos) {
            size_t start = prompt_pos + 10;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                std::string raw_prompt = command.substr(start, end - start);
                config.prompt = decodeJsonUnicode(raw_prompt);
            }
        }

        // 提取 negative_prompt（解码 Unicode 转义序列）
        size_t neg_pos = command.find("\"negative_prompt\":\"");
        if (neg_pos != std::string::npos) {
            size_t start = neg_pos + 19;
            size_t end = command.find("\"", start);
            if (end != std::string::npos) {
                std::string raw_neg = command.substr(start, end - start);
                config.negative_prompt = decodeJsonUnicode(raw_neg);
            }
        }

        // 提取数值参数
        auto extract_int = [&](const std::string& key, int& value) {
            size_t pos = command.find("\"" + key + "\":");
            if (pos != std::string::npos) {
                size_t start = pos + key.length() + 3;
                size_t end = command.find_first_of(",}", start);
                if (end != std::string::npos) {
                    value = std::stoi(command.substr(start, end - start));
                }
            }
        };

        auto extract_float = [&](const std::string& key, float& value) {
            size_t pos = command.find("\"" + key + "\":");
            if (pos != std::string::npos) {
                size_t start = pos + key.length() + 3;
                size_t end = command.find_first_of(",}", start);
                if (end != std::string::npos) {
                    value = std::stof(command.substr(start, end - start));
                }
            }
        };

        extract_int("width", config.width);
        extract_int("height", config.height);
        extract_int("steps", config.num_inference_steps);
        extract_float("cfg_scale", config.guidance_scale);
        extract_int("seed", config.seed);

        // img2img 专用参数
        if (mode == "img2img") {
            size_t img_pos = command.find("\"input_image\":\"");
            if (img_pos != std::string::npos) {
                size_t start = img_pos + 15;
                size_t end = command.find("\"", start);
                if (end != std::string::npos) {
                    config.input_image_path = command.substr(start, end - start);
                }
            }
            extract_float("strength", config.strength);
        }

        // 进度回调
        auto progress_callback = [hPipe](int current, int total, const std::string& preview_path) {
            std::string progress = "{\"type\":\"sd_progress\",\"current\":" +
                                 std::to_string(current) +
                                 ",\"total\":" + std::to_string(total);
            
            if (!preview_path.empty()) {
                progress += ",\"preview\":\"" + meetingai::proto::jsonEscape(preview_path) + "\"";
            }
            progress += "}\n";

            DWORD written;
            WriteFile(hPipe, progress.data(), (DWORD)progress.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
        };

        // 生成图片
        std::string output_path;
        if (mode == "img2img") {
            output_path = g_sd->generateImageToImage(config, progress_callback);
        } else {
            output_path = g_sd->generateTextToImage(config, progress_callback);
        }

        // 发送结果
        if (!output_path.empty()) {
            std::string result = "{\"type\":\"sd_complete\",\"image_path\":\"" +
                               meetingai::proto::jsonEscape(output_path) + "\"}\n";
            DWORD written;
            std::cout << "[Worker] 正在发送 sd_complete 消息: " << result << std::flush;
            WriteFile(hPipe, result.data(), (DWORD)result.size(), &written, nullptr);
            FlushFileBuffers(hPipe);
            std::cout << "[Worker] sd_complete 消息已发送, written=" << written << " bytes" << std::endl;

            std::wcout << L"[Worker] ✅ SD 生成完成: " << output_path.c_str() << L"\n";
        } else {
            std::string error = "{\"type\":\"error\",\"message\":\"生成失败: " +
                              meetingai::proto::jsonEscape(g_sd->getLastError()) + "\"}\n";
            DWORD written;
            WriteFile(hPipe, error.data(), (DWORD)error.size(), &written, nullptr);
        }
    }
    catch (const std::exception& e) {
        std::wcerr << L"[Worker] SD 命令处理异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
                         meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// ========== Token 计数命令处理 ==========
static void handleCountTokensCommand(HANDLE hPipe, const std::string& command) {
    try {
        // 检查 Embedding 模型是否已加载（使用 Embedding 的 tokenizer）
        if (!g_embedding) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ Embedding 模型未加载，无法计算 token 数\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 提取文本内容
        std::string text = meetingai::proto::extractPrompt(command);

        if (text.empty()) {
            std::string err = "{\"type\":\"error\",\"message\":\"❌ 文本内容为空\"}\n";
            DWORD written;
            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            return;
        }

        // 使用 Embedding 的 tokenizer 计算 token 数
        auto token_count = g_embedding->countTokens(text);

        // 构建响应
        std::string response = "{\"type\":\"token_count_result\",\"count\":" +
                               std::to_string(token_count) +
                               ",\"text_length\":" + std::to_string(text.length()) + "}\n";

        DWORD written;
        WriteFile(hPipe, response.data(), (DWORD)response.size(), &written, nullptr);
        FlushFileBuffers(hPipe);

        std::wcout << L"[TokenCount] ✅ 计算完成: " << token_count << L" tokens (文本长度: " << text.length() << L" 字符)\n";
    }
    catch (const std::exception& e) {
        std::wcerr << L"[TokenCount] 处理命令异常: " << e.what() << L"\n";
        std::string err = std::string("{\"type\":\"error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
}

// 新增：处理转录命令
static void handleTranscribeCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理转录命令\n";

    // ★ 仅初始化一次模型（支持热拔插）
    {
        std::lock_guard<std::mutex> lock(g_whisper_mutex);
        if (!g_whisper_loaded) {
            std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
            if (!InitWhisperOnce(modelPathOnce)) {
                std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
                DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
            }
            else {
                g_whisper_loaded = true;
                const char* ok = "{\"type\":\"stage\",\"name\":\"model_ready\"}\n";
                DWORD written; WriteFile(hPipe, ok, (DWORD)strlen(ok), &written, nullptr);
            }
        }
    }


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

// 新增：处理 OpenVINO Whisper 转录命令
static void handleTranscribeOpenVINOCommand(HANDLE hPipe, const std::string& command) {
    std::wcout << L"[Worker] 处理 OpenVINO Whisper 转录命令\n";

    // 检查模型是否已加载
    if (!meetingai::transcribe::IsWhisperOpenVINOModelLoaded()) {
        std::string error = "{\"type\":\"error\",\"message\":\"OpenVINO Whisper 模型未加载。请先在 Startup 页面加载模型。\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        std::wcerr << L"[Worker] 错误：模型未加载\n";
        return;
    }

    // 提取文件路径
    std::string audioPath = meetingai::proto::extractPath(command);
    if (audioPath.empty()) {
        std::string error = "{\"type\":\"error\",\"message\":\"无法解析音频文件路径\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }

    std::wcout << L"[Worker] 音频文件路径: " << audioPath.c_str() << L"\n";

    // 提取language参数
    std::string language = meetingai::proto::extractLanguage(command);
    std::cout << "[Worker] 语言设置: " << language << std::endl;

    // OpenVINO 模型路径（从已加载的模型获取）
    std::string modelPath = meetingai::util::resolveModelFileUtf8(L"whisper_large_v3");
    std::cout << "[Worker] 使用已加载的模型\n";

    // 定义进度回调函数
    auto progressCallback = [hPipe](int progress) {
        std::string progressMsg = "{\"type\":\"progress\",\"value\":" +
            std::to_string(progress) + "}\n";
        DWORD written;
        WriteFile(hPipe, progressMsg.data(), static_cast<DWORD>(progressMsg.size()), &written, nullptr);
    };

    // 执行转录
    std::vector<meetingai::transcribe::WhisperOpenVINOSegment> segments;
    bool success = meetingai::transcribe::TranscribeAudioFileOpenVINO(
        modelPath,
        audioPath,
        segments,
        language,
        progressCallback
    );

    if (!success) {
        std::string error = "{\"type\":\"error\",\"message\":\"转录失败\"}\n";
        DWORD written;
        WriteFile(hPipe, error.data(), static_cast<DWORD>(error.size()), &written, nullptr);
        return;
    }

    // 发送每个转录片段
    for (const auto& segment : segments) {
        // 插入数据库
        InsertTranscript("Unknown", segment.text, segment.start_ts);

        // 发送给 Host（使用与whisper.cpp相同的格式保持兼容）
        std::string response = std::string("{\"type\":\"asr_segment\",\"text\":\"") +
            meetingai::proto::jsonEscape(segment.text) +
            "\",\"t0_ms\":" + std::to_string((int)(segment.start_ts * 1000)) +
            ",\"t1_ms\":" + std::to_string((int)(segment.end_ts * 1000)) + "}\n";

        DWORD written;
        WriteFile(hPipe, response.data(), static_cast<DWORD>(response.size()), &written, nullptr);

        std::wcout << L"[Worker] 发送片段: " << segment.text.c_str() << L"\n";
    }

    // 发送完成信号
    std::string complete = "{\"type\":\"transcribe_complete\",\"segments\":" +
        std::to_string(segments.size()) + "}\n";
    DWORD written;
    WriteFile(hPipe, complete.data(), static_cast<DWORD>(complete.size()), &written, nullptr);

    std::wcout << L"[Worker] OpenVINO Whisper 转录完成\n";
}

// 新增：处理 Sherpa-ONNX 流式转录命令
static void handleSherpaStreamingCommand(HANDLE hPipe, const std::string& command) {
    auto sendTranscript = [hPipe](
        const char* type,
        const std::string& text) {
        if (text.empty()) {
            return;
        }
        std::string response = "{\"type\":\"" + std::string(type) +
            "\",\"text\":\"" + meetingai::proto::jsonEscape(text) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, response.data(), (DWORD)response.size(), &written, nullptr);
    };

    auto flushPendingTranscript = [&sendTranscript]() {
        if (g_streaming_pending_raw.empty()) {
            return;
        }

        std::string text = g_punct
            ? g_punct->AddPunctuation(g_streaming_pending_raw)
            : meetingai::transcribe::NormalizeBilingualTranscript(
                g_streaming_pending_raw);
        sendTranscript("streaming_final", text);
        std::cout << "[Worker] semantic final: " << text << std::endl;
        g_streaming_pending_raw.clear();
    };

    try {
        // ==================== start_streaming ====================
        if (meetingai::proto::isStartStreaming(command)) {
            // 注意：这条路径统一用 std::cout（窄字符 UTF-8）。
            // std::wcout 遇到中文会置 failbit，之后该流的所有输出被静默丢弃，
            // 排查时等于完全失明。
            std::cout << "[Worker] recv start_streaming" << std::endl;

            // 进度也通过管道回传一份，否则 Host 只能干等，看不出卡在哪一步
            auto notify = [hPipe](const std::string& text) {
                std::string msg = "{\"type\":\"info\",\"message\":\"" +
                    meetingai::proto::jsonEscape(text) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, msg.data(), (DWORD)msg.size(), &written, nullptr);
            };

            // 初始化 Sherpa 模型（仅一次）
            {
                std::lock_guard<std::mutex> lock(g_sherpa_mutex);
                if (!g_sherpa_loaded) {
                    g_sherpa = std::make_unique<meetingai::transcribe::SherpaStreamingTranscriber>();

                    // 模型路径（用户手动下载并解压）。必须走 models 目录解析：
                    // 相对路径会按 Worker 的 CWD 找，而 CWD 继承自 Host 的输出目录。
                    std::string modelDir = meetingai::util::resolveModelFileUtf8(
                        L"sherpa\\sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20");
                    std::string tokensPath = modelDir + "\\tokens.txt";
                    int sampleRate = meetingai::proto::extractSampleRate(command);

                    // sherpa 创建失败只会回一句没信息量的错误，这里先自己报清楚缺什么
                    {
                        std::error_code ec;
                        const char* missing = nullptr;
                        if (!std::filesystem::is_directory(modelDir, ec)) missing = "模型目录不存在";
                        else if (!std::filesystem::exists(tokensPath, ec)) missing = "tokens.txt 不存在";
                        else if (!std::filesystem::exists(modelDir + "\\encoder-epoch-99-avg-1.onnx", ec))
                            missing = "encoder-epoch-99-avg-1.onnx 不存在";
                        if (missing) {
                            std::string err = std::string("{\"type\":\"streaming_error\",\"message\":\"") +
                                missing + ": " + meetingai::proto::jsonEscape(modelDir) + "\"}\n";
                            DWORD written;
                            WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                            g_sherpa.reset();
                            return;
                        }
                    }

                    std::cout << "[Worker] sherpa model dir: " << modelDir << std::endl;
                    notify("[Sherpa] 正在加载模型（首次约需数十秒）: " + modelDir);

                    const auto t0 = std::chrono::steady_clock::now();
                    bool ok = g_sherpa->Initialize(modelDir, tokensPath, sampleRate);
                    const auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - t0).count();

                    if (!ok) {
                        std::cout << "[Worker] sherpa init FAILED after " << elapsedMs
                                  << "ms: " << g_sherpa->GetLastError() << std::endl;
                        std::string err = "{\"type\":\"streaming_error\",\"message\":\"Sherpa 模型初始化失败: " +
                            meetingai::proto::jsonEscape(g_sherpa->GetLastError()) + "\"}\n";
                        DWORD written;
                        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                        g_sherpa.reset();
                        return;
                    }

                    g_sherpa_loaded = true;
                    std::cout << "[Worker] sherpa model loaded in " << elapsedMs << "ms" << std::endl;
                    notify("[Sherpa] 模型加载完成，耗时 " + std::to_string(elapsedMs) + " ms");
                }

                // 标点模型（可选）。只尝试一次，失败后不再重试，也不影响转录。
                if (!g_punct_attempted) {
                    g_punct_attempted = true;

                    const std::string punctDir = meetingai::util::resolveModelFileUtf8(
                        L"sherpa\\sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12-int8");

                    auto punct = std::make_unique<meetingai::transcribe::Punctuator>();
                    const auto p0 = std::chrono::steady_clock::now();
                    if (punct->Initialize(punctDir)) {
                        const auto punctMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() - p0).count();
                        g_punct = std::move(punct);
                        std::cout << "[Worker] punctuation model loaded in " << punctMs << "ms" << std::endl;
                        notify("[Sherpa] 标点模型已加载，耗时 " + std::to_string(punctMs) + " ms");
                    }
                    else {
                        std::cout << "[Worker] punctuation disabled: " << punct->GetLastError() << std::endl;
                        notify("[Sherpa] 未启用标点（" + punct->GetLastError() + "），转录不受影响");
                    }
                }
            }

            // 启动流式会话
            if (!g_sherpa->StartSession()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"" +
                    meetingai::proto::jsonEscape(g_sherpa->GetLastError()) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }
            g_streaming_pending_raw.clear();

            // 发送成功响应
            const char* ok = "{\"type\":\"streaming_started\"}\n";
            DWORD written;
            WriteFile(hPipe, ok, (DWORD)strlen(ok), &written, nullptr);
            std::cout << "[Worker] streaming session started" << std::endl;
        }

        // ==================== streaming_audio ====================
        else if (meetingai::proto::isStreamingAudio(command)) {
            if (!g_sherpa || !g_sherpa->IsRunning()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"流式会话未启动\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // 提取 Base64 音频数据
            std::string audioData = meetingai::proto::extractAudioData(command);
            if (audioData.empty()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"音频数据为空\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // Base64 解码为 float32 样本
            std::vector<float> samples = meetingai::util::Base64DecodeToFloat(audioData);
            if (samples.empty()) {
                // 把实际收到的内容报出来，否则"解码失败"没有任何可查线索
                std::string head = audioData.substr(0, 48);
                std::cout << "[Worker] base64 decode failed."
                          << " cmd_len=" << command.size()
                          << " b64_len=" << audioData.size()
                          << " head=[" << head << "]" << std::endl;

                std::string err = "{\"type\":\"streaming_error\",\"message\":\"音频解码失败 b64_len=" +
                    std::to_string(audioData.size()) + " head=" +
                    meetingai::proto::jsonEscape(head) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // 发送音频到转录器
            std::vector<meetingai::transcribe::SherpaStreamResult> results;
            if (!g_sherpa->AcceptWaveform(samples.data(), (int)samples.size(), results)) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"" +
                    meetingai::proto::jsonEscape(g_sherpa->GetLastError()) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // Partial 始终实时显示，但只做轻量规则大小写，不在 100ms
            // 热路径里调用标点模型。Sherpa 的 final 先视为“声学稳定片段”，
            // 缓存并等待下一片段提供语义前瞻后才提交到 UI。
            for (const auto& result : results) {
                if (!result.is_final) {
                    const std::string combined =
                        meetingai::transcribe::JoinTranscriptFragments(
                            g_streaming_pending_raw,
                            result.text);
                    sendTranscript(
                        "streaming_partial",
                        meetingai::transcribe::NormalizeBilingualTranscript(combined));
                    continue;
                }

                if (!result.text.empty()) {
                    g_streaming_pending_raw =
                        meetingai::transcribe::JoinTranscriptFragments(
                            g_streaming_pending_raw,
                            result.text);

                    // 没有标点模型时无法可靠判断语义边界，保持旧的声学
                    // endpoint 行为，但仍进行大小写归一化。
                    if (!g_punct) {
                        flushPendingTranscript();
                        continue;
                    }

                    const std::string punctuated =
                        g_punct->AddPunctuation(g_streaming_pending_raw);
                    meetingai::transcribe::StableTranscriptPrefix stable;
                    if (meetingai::transcribe::TryExtractStableTranscriptPrefix(
                        g_streaming_pending_raw,
                        punctuated,
                        stable)) {
                        sendTranscript("streaming_final", stable.finalizedText);
                        std::cout << "[Worker] semantic final: "
                                  << stable.finalizedText << std::endl;
                        g_streaming_pending_raw =
                            std::move(stable.remainingRawText);

                        // final 会替换当前 partial；若还有尚未定稿的后半句，
                        // 立即作为下一条 partial 显示，画面不会丢字。
                        if (!g_streaming_pending_raw.empty()) {
                            sendTranscript(
                                "streaming_partial",
                                meetingai::transcribe::NormalizeBilingualTranscript(
                                    g_streaming_pending_raw));
                        }
                    }
                    continue;
                }

                // reset 后再次触发空 endpoint，表示已经持续静音约
                // rule1 的时长；此时没有更多前瞻，强制提交剩余文本。
                if (result.endpoint_detected) {
                    flushPendingTranscript();
                }
            }
        }

        // ==================== stop_streaming ====================
        else if (meetingai::proto::isStopStreaming(command)) {
            std::wcout << L"[Worker] 收到 stop_streaming 命令\n";

            if (!g_sherpa || !g_sherpa->IsRunning()) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"流式会话未启动\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // 结束会话并获取最终结果
            std::vector<meetingai::transcribe::SherpaStreamResult> finalResults;
            if (!g_sherpa->EndSession(finalResults)) {
                std::string err = "{\"type\":\"streaming_error\",\"message\":\"" +
                    meetingai::proto::jsonEscape(g_sherpa->GetLastError()) + "\"}\n";
                DWORD written;
                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                return;
            }

            // 把停止前仍在当前 Sherpa stream 中的文字并入语义缓冲。
            for (const auto& result : finalResults) {
                if (!result.text.empty()) {
                    g_streaming_pending_raw =
                        meetingai::transcribe::JoinTranscriptFragments(
                            g_streaming_pending_raw,
                            result.text);
                }
            }
            flushPendingTranscript();

            // 发送完成信号
            const char* complete = "{\"type\":\"streaming_stopped\"}\n";
            DWORD written;
            WriteFile(hPipe, complete, (DWORD)strlen(complete), &written, nullptr);
            std::wcout << L"[Worker] 流式会话已停止\n";
        }
    }
    catch (const std::exception& e) {
        std::string err = std::string("{\"type\":\"streaming_error\",\"message\":\"") +
            meetingai::proto::jsonEscape(e.what()) + "\"}\n";
        DWORD written;
        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
    }
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



int wmain() {
    // ★ 设置控制台 UTF-8 编码（修复中文显示问题）
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);

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

    // 解析命令行参数：--ppid, --granite-device, --embedding-device
    DWORD parentPid = 0;
    for (int i = 1; i < __argc; i++) {
        if (std::wstring(__wargv[i]) == L"--ppid" && i + 1 < __argc) {
            parentPid = std::wcstoul(__wargv[++i], nullptr, 10);
        }
        else if (std::wstring(__wargv[i]) == L"--granite-device" && i + 1 < __argc) {
            std::wstring wdevice = __wargv[++i];
            g_granite_device = std::string(wdevice.begin(), wdevice.end());
            std::wcout << L"[Worker] Granite 设备: " << wdevice.c_str() << L"\n";
        }
        else if (std::wstring(__wargv[i]) == L"--embedding-device" && i + 1 < __argc) {
            std::wstring wdevice = __wargv[++i];
            g_embedding_device = std::string(wdevice.begin(), wdevice.end());
            std::wcout << L"[Worker] Embedding 设备: " << wdevice.c_str() << L"\n";
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
        // 退出码 2 让 Host 能把这种情况和正常退出区分开（见 MainWindow.Pipe.cs）
        std::wcerr << L"[Worker] another instance is already running, exiting.\n";
        std::wcerr.flush();
        return 2;
    }

    // 父进程看门狗。
    // 主循环顶部那次 hParent 检查只在两次连接之间才轮得到，而 ConnectNamedPipe
    // 是无超时的阻塞调用，客户端进程死掉并不会让它返回。Host 退出时 Worker 往往
    // 正卡在那一行，于是变成占着互斥量和管道的僵尸进程。这里单开一个线程专门等
    // 父进程句柄，不受主循环阻塞影响。
    if (hParent) {
        std::thread([hParent] {
            ::WaitForSingleObject(hParent, INFINITE);
            std::cerr << "[Worker] host process exited, terminating.\n";
            std::cerr.flush();
            ::ExitProcess(3);
        }).detach();
    }
    else if (parentPid) {
        std::cerr << "[Worker] warning: OpenProcess(" << parentPid
                  << ") failed, parent watchdog disabled.\n";
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
            // 流式音频每包约 4.3KB（100ms PCM 经 base64 膨胀），原来的 4096 比单个包还小，
            // 每次写入都要等对端边读边腾地方。放大到 256KB 让收发都不必来回阻塞。
            256 * 1024, 256 * 1024, 0,
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

                // ---- 新增：预加载模型命令 ----
                if (buffer.find("\"preload_models\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到预加载模型命令\n";

                    DWORD written;

                    // 发送调试消息
                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 收到 preload_models 命令\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 解析设备选择
                    std::string graniteDeviceCmd = g_granite_device;
                    std::string embeddingDeviceCmd = g_embedding_device;
                    std::string llavaDeviceCmd = g_llava_device;

                    auto granitePos = buffer.find("\"granite_device\":\"");
                    if (granitePos != std::string::npos) {
                        auto start = granitePos + 18;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            graniteDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    auto embeddingPos = buffer.find("\"embedding_device\":\"");
                    if (embeddingPos != std::string::npos) {
                        auto start = embeddingPos + 20;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            embeddingDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    auto llavaPos = buffer.find("\"llava_device\":\"");
                    if (llavaPos != std::string::npos) {
                        auto start = llavaPos + 16;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            llavaDeviceCmd = buffer.substr(start, end - start);
                        }
                    }

                    std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 设备参数: Granite=" + graniteDeviceCmd + ", Embedding=" + embeddingDeviceCmd + ", LLaVA=" + llavaDeviceCmd + "\"}\n";
                    WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

                    // 发送确认消息
                    std::string ack = "{\"type\":\"preload_started\"}\n";
                    WriteFile(hPipe, ack.data(), (DWORD)ack.size(), &written, nullptr);

                    // 直接在主线程加载模型（支持热拔插）
                    std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载Granite...\"}\n";
                    WriteFile(hPipe, debug3.data(), (DWORD)debug3.size(), &written, nullptr);

                    {
                        std::lock_guard<std::mutex> lock(g_granite_mutex);
                        if (!g_granite_loaded) {
                            InitializeGraniteGenAI(hPipe, graniteDeviceCmd);
                            g_granite_loaded = true;
                        }
                    }

                    std::string debug4 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载Embedding...\"}\n";
                    WriteFile(hPipe, debug4.data(), (DWORD)debug4.size(), &written, nullptr);

                    {
                        std::lock_guard<std::mutex> lock(g_embedding_mutex);
                        if (!g_embedding_loaded) {
                            InitializeEmbeddingGenAI(hPipe, embeddingDeviceCmd);
                            g_embedding_loaded = true;
                        }
                    }

                    // ---- 临时注释：测试 LLaVA 加载问题 ----
                    // std::string debug4_5 = "{\"type\":\"info\",\"message\":\"[Worker Debug] 开始加载LLaVA...\"}\n";
                    // WriteFile(hPipe, debug4_5.data(), (DWORD)debug4_5.size(), &written, nullptr);
                    //
                    // std::call_once(g_llava_once, [hPipe, llavaDeviceCmd]() {
                    //     InitializeLLaVAGenAI(hPipe, llavaDeviceCmd);
                    // });

                    std::string debug5 = "{\"type\":\"info\",\"message\":\"[Worker Debug]预加载完成\"}\n";
                    WriteFile(hPipe, debug5.data(), (DWORD)debug5.size(), &written, nullptr);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Whisper 加载命令 ----
                if (buffer.find("\"load_whisper\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 load_whisper 命令\n";
                    DWORD written;

                    // 解析设备选择
                    std::string device = "GPU";  // 默认使用 GPU
                    auto devicePos = buffer.find("\"device\":\"");
                    if (devicePos != std::string::npos) {
                        auto start = devicePos + 10;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            device = buffer.substr(start, end - start);
                        }
                    }

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] Whisper 设备: " + device + "\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker] 开始加载 Whisper 模型...\"}\n";
                    WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

                    // 加载 Whisper 模型（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_whisper_mutex);
                        if (!g_whisper_loaded) {
                            std::string modelPath = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
                            if (!InitWhisperOnce(modelPath)) {
                                std::string err = "{\"type\":\"whisper_error\",\"message\":\"Whisper 模型加载失败\"}\n";
                                WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                            } else {
                                g_whisper_loaded = true;
                                std::string ready = "{\"type\":\"whisper_ready\",\"device\":\"" + device + "\"}\n";
                                WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
                            }
                        } else {
                            // 已经加载过，直接发送 ready 消息
                            std::string ready = "{\"type\":\"whisper_ready\",\"device\":\"" + device + "\"}\n";
                            WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
                        }
                    }

                    buffer.clear();
                    continue;
                }

                // ---- 新增：Whisper 卸载命令 ----
                if (buffer.find("\"unload_whisper\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_whisper 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 Whisper 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 Whisper 资源（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_whisper_mutex);
                        CleanupWhisper();
                        g_whisper_loaded = false;
                    }

                    std::string unloaded = "{\"type\":\"whisper_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] Whisper 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 加载命令 ----
                if (buffer.find("\"load_whisper_openvino\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 load_whisper_openvino 命令\n";
                    DWORD written;

                    // 解析模型路径
                    std::string modelPath = meetingai::util::resolveModelFileUtf8(L"whisper_large_v3");
                    auto pathPos = buffer.find("\"model_path\":\"");
                    if (pathPos != std::string::npos) {
                        auto start = pathPos + 14;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            modelPath = buffer.substr(start, end - start);
                        }
                    }

                    // 命令里给的相对路径会按进程 CWD 解析，而 Worker 的 CWD 继承自 Host，
                    // 不是模型所在目录。统一折算到 models 目录下，绝对路径则原样使用。
                    if (!modelPath.empty() && std::filesystem::path(modelPath).is_relative()) {
                        const auto leaf = std::filesystem::path(modelPath).filename().wstring();
                        modelPath = meetingai::util::resolveModelFileUtf8(leaf.c_str());
                    }

                    // 解析设备选择
                    std::string device = "CPU";  // 默认使用 CPU
                    auto devicePos = buffer.find("\"device\":\"");
                    if (devicePos != std::string::npos) {
                        auto start = devicePos + 10;
                        auto end = buffer.find("\"", start);
                        if (end != std::string::npos) {
                            device = buffer.substr(start, end - start);
                        }
                    }

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] OpenVINO Whisper 模型路径: " + modelPath + "\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    std::string debug2 = "{\"type\":\"info\",\"message\":\"[Worker] OpenVINO Whisper 设备: " + device + "\"}\n";
                    WriteFile(hPipe, debug2.data(), (DWORD)debug2.size(), &written, nullptr);

                    std::string debug3 = "{\"type\":\"info\",\"message\":\"[Worker] 开始加载 OpenVINO Whisper 模型...\"}\n";
                    WriteFile(hPipe, debug3.data(), (DWORD)debug3.size(), &written, nullptr);

                    // 加载 OpenVINO Whisper 模型（支持热拔插）
                    bool success = meetingai::transcribe::LoadWhisperOpenVINOModel(modelPath, device);
                    if (success) {
                        std::string ready = "{\"type\":\"whisper_openvino_ready\",\"model_path\":\"" + modelPath + "\",\"device\":\"" + device + "\"}\n";
                        WriteFile(hPipe, ready.data(), (DWORD)ready.size(), &written, nullptr);
                        std::wcout << L"[Worker] OpenVINO Whisper 模型加载成功\n";
                    }
                    else {
                        std::string err = "{\"type\":\"whisper_openvino_error\",\"message\":\"模型加载失败\"}\n";
                        WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                        std::wcerr << L"[Worker] OpenVINO Whisper 加载失败\n";
                    }

                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 卸载命令 ----
                if (buffer.find("\"unload_whisper_openvino\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_whisper_openvino 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 OpenVINO Whisper 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 OpenVINO Whisper 资源（支持热拔插）
                    meetingai::transcribe::UnloadWhisperOpenVINOModel();

                    std::string unloaded = "{\"type\":\"whisper_openvino_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] OpenVINO Whisper 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：LLaVA 卸载命令 ----
                if (buffer.find("\"unload_llava\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_llava 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 LLaVA 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 LLaVA 资源（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_llava_mutex);
                        g_llava.reset();
                        g_llava_loaded = false;
                    }

                    std::string unloaded = "{\"type\":\"llava_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] LLaVA 模型已卸载\n";
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Granite & Embedding 卸载命令 ----
                if (buffer.find("\"unload_granite_embedding\"") != std::string::npos) {
                    std::wcout << L"[Worker] 收到 unload_granite_embedding 命令\n";
                    DWORD written;

                    std::string debug1 = "{\"type\":\"info\",\"message\":\"[Worker] 正在卸载 Granite & Embedding 模型...\"}\n";
                    WriteFile(hPipe, debug1.data(), (DWORD)debug1.size(), &written, nullptr);

                    // 释放 Granite 和 Embedding 资源（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_granite_mutex);
                        g_granite.reset();
                        g_granite_loaded = false;
                    }
                    {
                        std::lock_guard<std::mutex> lock(g_embedding_mutex);
                        g_embedding.reset();
                        g_embedding_loaded = false;
                    }

                    std::string unloaded = "{\"type\":\"granite_embedding_unloaded\"}\n";
                    WriteFile(hPipe, unloaded.data(), (DWORD)unloaded.size(), &written, nullptr);

                    std::wcout << L"[Worker] Granite & Embedding 模型已卸载\n";
                    buffer.clear();
                    continue;
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

                // ---- 新增：LLaVA 命令处理 ----
                if (buffer.find("\"llava_") != std::string::npos || buffer.find("\"load_llava\"") != std::string::npos) {
                    handleLLaVACommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Stable Diffusion 命令处理 ----
                if (buffer.find("\"sd_") != std::string::npos || buffer.find("\"load_sd\"") != std::string::npos) {
                    // 如果是加载命令
                    if (buffer.find("\"load_sd\"") != std::string::npos) {
                        std::string device = "NPU";  // 默认使用 NPU
                        auto devicePos = buffer.find("\"device\":\"");
                        if (devicePos != std::string::npos) {
                            auto start = devicePos + 10;
                            auto end = buffer.find("\"", start);
                            if (end != std::string::npos) {
                                device = buffer.substr(start, end - start);
                            }
                        }

                        // 支持热拔插
                        {
                            std::lock_guard<std::mutex> lock(g_sd_mutex);
                            if (!g_sd_loaded) {
                                InitializeSDEngine(hPipe, device);
                                g_sd_loaded = true;
                            }
                        }
                    } else {
                        // 其他 SD 命令
                        handleSDCommand(hPipe, buffer);
                    }
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Token 计数命令处理 ----
                if (buffer.find("\"count_tokens\"") != std::string::npos) {
                    handleCountTokensCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：转录命令处理 ----
                if (meetingai::proto::isTranscribe(buffer)) {
                    handleTranscribeCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：OpenVINO Whisper 转录命令处理 ----
                if (meetingai::proto::isTranscribeOpenVINO(buffer)) {
                    handleTranscribeOpenVINOCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：Sherpa-ONNX 实时流式转录命令处理 ----
                if (meetingai::proto::isStartStreaming(buffer) ||
                    meetingai::proto::isStreamingAudio(buffer) ||
                    meetingai::proto::isStopStreaming(buffer)) {
                    handleSherpaStreamingCommand(hPipe, buffer);
                    buffer.clear();
                    continue;
                }

                // ---- 新增：流式转录命令处理 ----
                if (meetingai::proto::isStartStream(buffer)) {
                    std::wcout << L"[Worker] 处理 start_stream 命令\n";

                    // 确保模型已加载（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_whisper_mutex);
                        if (!g_whisper_loaded) {
                            std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
                            if (!InitWhisperOnce(modelPathOnce)) {
                                std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
                                DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                            } else {
                                g_whisper_loaded = true;
                            }
                        }
                    }

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
                    // 确保模型已加载（支持热拔插）
                    {
                        std::lock_guard<std::mutex> lock(g_whisper_mutex);
                        if (!g_whisper_loaded) {
                            std::string modelPathOnce = meetingai::util::resolveModelFileUtf8(L"ggml-large-v3.bin");
                            if (!InitWhisperOnce(modelPathOnce)) {
                                std::string err = "{\"type\":\"error\",\"message\":\"模型加载失败\"}\n";
                                DWORD written; WriteFile(hPipe, err.data(), (DWORD)err.size(), &written, nullptr);
                            } else {
                                g_whisper_loaded = true;
                            }
                        }
                    }
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
