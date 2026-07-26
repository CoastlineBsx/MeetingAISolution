//#pragma once
//#include <openvino/genai/llm_pipeline.hpp>
//#include <functional>
//#include <memory>
//#include <mutex>
//#include <string>
//
//namespace meetingai::granite {
//
//    class GraniteGenAI {
//    public:
//        // model_path: OpenVINO IR 目录或 XML 文件路径
//        // device:    "CPU" / "GPU" / "NPU" / "AUTO" 等
//        GraniteGenAI(const std::string& model_path, const std::string& device);
//        ~GraniteGenAI();
//
//        // —— 无上下文的一次性问答 —— //
//        std::string generate(const std::string& prompt,
//            int max_tokens = 256,
//            float temperature = 0.7f);
//
//        // —— 无上下文的流式输出 —— //
//        void generateStream(const std::string& prompt,
//            std::function<void(const std::string&)> on_token,
//            int max_tokens = 256,
//            float temperature = 0.7f);
//
//        // —— 多轮聊天：在多轮之间保留 KV cache（上下文） —— //
//        // 进入对话模式；可选 system_message（提示风格、语言等）
//        void startChat(const std::string& system_message = "");
//
//        // 带上下文的同步回复（保留历史）
//        std::string chat(const std::string& user_msg,
//            int max_tokens = 256,
//            float temperature = 0.7f);
//
//        // 带上下文的流式回复（保留历史）
//        void chatStream(const std::string& user_msg,
//            std::function<void(const std::string&)> on_token,
//            int max_tokens = 256,
//            float temperature = 0.7f);
//
//        // 结束对话并清空会话内的 KV/history
//        void finishChat();
//
//        // 是否处于对话模式（上下文保留）
//        bool isChatting() const { return chatting_; }
//
//    private:
//        std::unique_ptr<ov::genai::LLMPipeline> pipeline_;
//        bool chatting_ = false;
//        mutable std::mutex mtx_;  // 简单并发保护（同一时间只跑一次生成）
//    };
//
//} // namespace meetingai::granite

#pragma once
#include <functional>
#include <memory>
#include <mutex>
#include <string>

namespace meetingai::granite {

    class GraniteGenAI {
    public:
        // model_path: OpenVINO IR 目录或 XML 文件路径
        // device:    "CPU" / "GPU" / "NPU" / "AUTO" 等
        GraniteGenAI(const std::string& model_path, const std::string& device);
        ~GraniteGenAI();

        GraniteGenAI(const GraniteGenAI&) = delete;
        GraniteGenAI& operator=(const GraniteGenAI&) = delete;

        // —— 无上下文的一次性问答 —— //
        std::string generate(const std::string& prompt,
            int max_tokens = 256,
            float temperature = 0.7f);

        // —— 无上下文的流式输出 —— //
        void generateStream(const std::string& prompt,
            std::function<void(const std::string&)> on_token,
            int max_tokens = 256,
            float temperature = 0.7f);

        // —— 使用 OpenVINO 结构化解码的一次性指令调用 —— //
        std::string generateStructuredInstruct(
            const std::string& system_message,
            const std::string& user_prompt,
            const std::string& json_schema,
            int max_tokens = 256,
            float temperature = 0.0f);

        // —— 多轮聊天：在多轮之间保留 KV cache（上下文） —— //
        void startChat(const std::string& system_message = "");

        // 带上下文的同步回复（保留历史）
        std::string chat(const std::string& user_msg,
            int max_tokens = 256,
            float temperature = 0.7f);

        // 带上下文的流式回复（保留历史）
        void chatStream(const std::string& user_msg,
            std::function<void(const std::string&)> on_token,
            int max_tokens = 256,
            float temperature = 0.7f);

        // 结束对话并清空会话内的 KV/history
        void finishChat();

        // 是否处于对话模式（上下文保留）
        bool isChatting() const;

    private:
        struct Impl;                     // 前向声明，隐藏 OpenVINO 细节
        std::unique_ptr<Impl> p_;        // 实现指针
        mutable std::mutex mtx_;         // 简单并发保护
    };

} // namespace meetingai::granite

