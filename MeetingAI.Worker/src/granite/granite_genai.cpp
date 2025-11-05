//#include "pch.h"
//#include "granite_genai.hpp"
//#include <iostream>
//
//namespace meetingai::granite {
//
//    GraniteGenAI::GraniteGenAI(const std::string& model_path,
//        const std::string& device) {
//        try {
//            pipeline_ = std::make_unique<ov::genai::LLMPipeline>(model_path, device);
//            std::cout << "[Granite GenAI] ✅ Initialized on " << device << std::endl;
//        }
//        catch (const std::exception& e) {
//            std::cerr << "[Granite GenAI] ❌ Failed: " << e.what() << std::endl;
//            throw;
//        }
//    }
//
//    GraniteGenAI::~GraniteGenAI() {
//        try {
//            finishChat(); // 若处于 chat 模式，确保释放会话状态
//        }
//        catch (...) {
//            // 析构期不抛异常
//        }
//    }
//
//    // ===================== 无上下文 =====================
//
//    std::string GraniteGenAI::generate(const std::string& prompt,
//        int max_tokens,
//        float temperature) {
//        std::lock_guard<std::mutex> lk(mtx_);
//
//        ov::genai::GenerationConfig cfg;
//        cfg.max_new_tokens = max_tokens;
//        cfg.temperature = temperature;
//        cfg.do_sample = (temperature > 0.0f);
//
//        // 注意：这里不会保留 KV；每次都是“干净”的单轮生成
//        return pipeline_->generate(prompt, cfg);
//    }
//
//    void GraniteGenAI::generateStream(const std::string& prompt,
//        std::function<void(const std::string&)> on_token,
//        int max_tokens,
//        float temperature) {
//        std::lock_guard<std::mutex> lk(mtx_);
//
//        ov::genai::GenerationConfig cfg;
//        cfg.max_new_tokens = max_tokens;
//        cfg.temperature = temperature;
//        cfg.do_sample = (temperature > 0.0f);
//
//        auto streamer = [on_token](std::string token) -> bool {
//            on_token(token);
//            return false; // false = 继续生成；true = 中断
//            };
//
//        pipeline_->generate(prompt, cfg, streamer);
//    }
//
//    // ===================== 多轮上下文 =====================
//
//    void GraniteGenAI::startChat(const std::string& system_message) {
//        std::lock_guard<std::mutex> lk(mtx_);
//        if (chatting_) return;
//
//        if (system_message.empty()) {
//            pipeline_->start_chat();
//        }
//        else {
//            pipeline_->start_chat(system_message);
//        }
//        chatting_ = true;
//    }
//
//    std::string GraniteGenAI::chat(const std::string& user_msg,
//        int max_tokens,
//        float temperature) {
//        std::lock_guard<std::mutex> lk(mtx_);
//
//        if (!chatting_) {
//            // 默认无 system message 直接进入会话
//            pipeline_->start_chat();
//            chatting_ = true;
//        }
//
//        ov::genai::GenerationConfig cfg;
//        cfg.max_new_tokens = max_tokens;
//        cfg.temperature = temperature;
//        cfg.do_sample = (temperature > 0.0f);
//
//        // 处于 chat 模式时，LLMPipeline 会在内部保留 KV（历史）
//        return pipeline_->generate(user_msg, cfg);
//    }
//
//    void GraniteGenAI::chatStream(const std::string& user_msg,
//        std::function<void(const std::string&)> on_token,
//        int max_tokens,
//        float temperature) {
//        std::lock_guard<std::mutex> lk(mtx_);
//
//        if (!chatting_) {
//            pipeline_->start_chat();
//            chatting_ = true;
//        }
//
//        ov::genai::GenerationConfig cfg;
//        cfg.max_new_tokens = max_tokens;
//        cfg.temperature = temperature;
//        cfg.do_sample = (temperature > 0.0f);
//
//        auto streamer = [on_token](std::string token) -> bool {
//            on_token(token);
//            return false;
//            };
//
//        pipeline_->generate(user_msg, cfg, streamer);
//    }
//
//    void GraniteGenAI::finishChat() {
//        std::lock_guard<std::mutex> lk(mtx_);
//        if (!chatting_) return;
//
//        try {
//            pipeline_->finish_chat(); // 通知管线结束会话并清掉会话态
//        }
//        catch (const std::exception& e) {
//            std::cerr << "[Granite GenAI] ⚠ finish_chat failed: " << e.what() << std::endl;
//        }
//        chatting_ = false;
//    }
//
//} // namespace meetingai::granite

#include "granite_genai.hpp"

#include <iostream>
// 只在 .cpp 引入 OpenVINO 的头
#include <openvino/genai/llm_pipeline.hpp>
#include <openvino/genai/generation_config.hpp>   // GenerationConfig

namespace meetingai::granite {

    // 真实实现细节都放到 Impl 里
    struct GraniteGenAI::Impl {
        std::unique_ptr<ov::genai::LLMPipeline> pipeline;
        bool chatting = false;
    };

    GraniteGenAI::GraniteGenAI(const std::string& model_path,
        const std::string& device)
        : p_(std::make_unique<Impl>()) {
        try {
            p_->pipeline = std::make_unique<ov::genai::LLMPipeline>(model_path, device);
            std::cout << "[Granite GenAI] ✅ Initialized on " << device << std::endl;
        }
        catch (const std::exception& e) {
            std::cerr << "[Granite GenAI] ❌ Failed: " << e.what() << std::endl;
            throw;
        }
    }

    GraniteGenAI::~GraniteGenAI() {
        try {
            finishChat(); // 若处于 chat 模式，确保释放会话状态
        }
        catch (...) {
            // 析构期不抛异常
        }
    }

    // ===================== 无上下文 =====================

    std::string GraniteGenAI::generate(const std::string& prompt,
        int max_tokens,
        float temperature) {
        std::lock_guard<std::mutex> lk(mtx_);

        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);
        // cfg.stop_strings = {"<|end_of_text|>"};  // 暂时注释掉，可能导致问题

        return p_->pipeline->generate(prompt, cfg);
    }

    void GraniteGenAI::generateStream(const std::string& prompt,
        std::function<void(const std::string&)> on_token,
        int max_tokens,
        float temperature) {
        std::lock_guard<std::mutex> lk(mtx_);

        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);
        // cfg.stop_strings = {"<|end_of_text|>"};  // 暂时注释掉，可能导致问题

        auto streamer = [on_token](std::string token) -> bool {
            on_token(token);
            return false; // false = 继续生成；true = 中断
            };

        p_->pipeline->generate(prompt, cfg, streamer);
    }

    // ===================== 多轮上下文 =====================

    void GraniteGenAI::startChat(const std::string& system_message) {
        std::lock_guard<std::mutex> lk(mtx_);
        if (p_->chatting) return;

        if (system_message.empty()) {
            p_->pipeline->start_chat();
        }
        else {
            p_->pipeline->start_chat(system_message);
        }
        p_->chatting = true;
    }

    std::string GraniteGenAI::chat(const std::string& user_msg,
        int max_tokens,
        float temperature) {
        std::lock_guard<std::mutex> lk(mtx_);

        if (!p_->chatting) {
            p_->pipeline->start_chat();
            p_->chatting = true;
        }

        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);
        // cfg.stop_strings = {"<|end_of_text|>"};  // 暂时注释掉，可能导致问题

        return p_->pipeline->generate(user_msg, cfg);
    }

    void GraniteGenAI::chatStream(const std::string& user_msg,
        std::function<void(const std::string&)> on_token,
        int max_tokens,
        float temperature) {
        std::lock_guard<std::mutex> lk(mtx_);

        if (!p_->chatting) {
            p_->pipeline->start_chat();
            p_->chatting = true;
        }

        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);
        // cfg.stop_strings = {"<|end_of_text|>"};  // 暂时注释掉，可能导致问题

        auto streamer = [on_token](std::string token) -> bool {
            on_token(token);
            return false;
            };

        p_->pipeline->generate(user_msg, cfg, streamer);
    }

    void GraniteGenAI::finishChat() {
        std::lock_guard<std::mutex> lk(mtx_);
        if (!p_->chatting) return;

        try {
            p_->pipeline->finish_chat();
        }
        catch (const std::exception& e) {
            std::cerr << "[Granite GenAI] ⚠ finish_chat failed: " << e.what() << std::endl;
        }
        p_->chatting = false;
    }

    bool GraniteGenAI::isChatting() const {
        std::lock_guard<std::mutex> lk(mtx_);
        return p_->chatting;
    }

} // namespace meetingai::granite

