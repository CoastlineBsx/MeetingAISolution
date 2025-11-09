#include "llava_genai.h"

#include <iostream>
#include <openvino/genai/visual_language/pipeline.hpp>
#include <openvino/genai/generation_config.hpp>
#include <openvino/core/core.hpp>

// stb_image for loading images (implementation is in image_processor.cpp)
#include "stb_image.h"

namespace llava {

// ========== Pimpl 实现 ==========
struct LLaVAGenAI::Impl {
    std::unique_ptr<ov::genai::VLMPipeline> pipeline;
    bool chatting = false;
    std::string cached_image_path;
};

// ========== 辅助函数：加载图片到 ov::Tensor ==========
static ov::Tensor loadImageAsTensor(const std::string& image_path) {
    int width, height, channels;
    unsigned char* image_data = stbi_load(image_path.c_str(), &width, &height, &channels, 3);

    if (!image_data) {
        throw std::runtime_error("Failed to load image: " + image_path);
    }

    // 创建 HWC 格式的 tensor [H, W, C]
    ov::Tensor tensor(ov::element::u8, {static_cast<size_t>(height), static_cast<size_t>(width), 3});
    std::memcpy(tensor.data<uint8_t>(), image_data, height * width * 3);

    stbi_image_free(image_data);

    return tensor;
}

// ========== 构造/析构 ==========
LLaVAGenAI::LLaVAGenAI(const std::string& model_path, const std::string& device)
    : p_(std::make_unique<Impl>()) {
    try {
        std::cout << "[LLaVA] 初始化 VLMPipeline..." << std::endl;
        std::cout << "[LLaVA] 模型路径: " << model_path << std::endl;
        std::cout << "[LLaVA] 设备: " << device << std::endl;

        p_->pipeline = std::make_unique<ov::genai::VLMPipeline>(model_path, device);

        std::cout << "[LLaVA] ✅ 初始化完成" << std::endl;
    }
    catch (const std::exception& e) {
        std::cerr << "[LLaVA] ❌ 初始化失败: " << e.what() << std::endl;
        throw;
    }
}

LLaVAGenAI::~LLaVAGenAI() {
    try {
        finishChat(); // 若处于 chat 模式，确保释放会话状态
    }
    catch (...) {
        // 析构期不抛异常
    }
}

// ========== 单轮模式 ==========
void LLaVAGenAI::generateStream(
    const std::string& image_path,
    const std::string& prompt,
    std::function<void(const std::string&)> on_token,
    int max_tokens,
    float temperature
) {
    std::lock_guard<std::mutex> lk(mtx_);

    std::cout << "[LLaVA] 单轮模式生成..." << std::endl;
    std::cout << "[LLaVA] 图片: " << image_path << std::endl;
    std::cout << "[LLaVA] 问题: " << prompt << std::endl;

    try {
        // 加载图片
        auto image_tensor = loadImageAsTensor(image_path);

        // 配置生成参数
        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);

        // 流式回调 - VLMPipeline 使用 StreamerVariant
        auto streamer = [on_token](std::string token) -> bool {
            on_token(token);
            return false; // false = 继续生成；true = 中断
        };

        // 调用 VLMPipeline 生成（单轮模式：每次传入图片）
        auto results = p_->pipeline->generate(prompt, image_tensor, cfg, streamer);

        std::cout << "[LLaVA] ✅ 单轮生成完成" << std::endl;
    }
    catch (const std::exception& e) {
        std::cerr << "[LLaVA] ❌ 单轮生成失败: " << e.what() << std::endl;
        throw;
    }
}

// ========== 多轮模式 ==========
void LLaVAGenAI::startChat(const std::string& image_path) {
    std::lock_guard<std::mutex> lk(mtx_);

    if (p_->chatting) {
        std::cout << "[LLaVA] ⚠️ 已在对话中，先结束旧会话" << std::endl;
        try {
            p_->pipeline->finish_chat();
        } catch (...) {}
    }

    std::cout << "[LLaVA] 开始多轮对话..." << std::endl;
    std::cout << "[LLaVA] 图片: " << image_path << std::endl;

    try {
        // 开始对话
        p_->pipeline->start_chat();
        p_->chatting = true;
        p_->cached_image_path = image_path;

        // 多轮模式需要先发送图片 (发送空prompt + 图片)
        auto image_tensor = loadImageAsTensor(image_path);
        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = 1;  // 只是为了编码图片，不需要生成文本

        // 发送一个初始化消息来编码图片
        auto results = p_->pipeline->generate("", image_tensor, cfg, [](std::string) -> bool { return false; });

        std::cout << "[LLaVA] ✅ 多轮会话已启动，图片特征已缓存" << std::endl;
    }
    catch (const std::exception& e) {
        std::cerr << "[LLaVA] ❌ 启动会话失败: " << e.what() << std::endl;
        p_->chatting = false;
        throw;
    }
}

void LLaVAGenAI::chatStream(
    const std::string& prompt,
    std::function<void(const std::string&)> on_token,
    int max_tokens,
    float temperature
) {
    std::lock_guard<std::mutex> lk(mtx_);

    if (!p_->chatting) {
        throw std::runtime_error("Not in chat mode, call startChat() first");
    }

    std::cout << "[LLaVA] 多轮模式生成（复用缓存）..." << std::endl;
    std::cout << "[LLaVA] 问题: " << prompt << std::endl;

    try {
        // 配置生成参数
        ov::genai::GenerationConfig cfg;
        cfg.max_new_tokens = max_tokens;
        cfg.temperature = temperature;
        cfg.do_sample = (temperature > 0.0f);

        // 流式回调
        auto streamer = [on_token](std::string token) -> bool {
            on_token(token);
            return false;
        };

        // 多轮模式：不传图片（VLMPipeline 会使用之前缓存的图片特征）
        // 使用空的tensor vector表示不传图片
        std::vector<ov::Tensor> empty_images;
        auto results = p_->pipeline->generate(prompt, empty_images, cfg, streamer);

        std::cout << "[LLaVA] ✅ 多轮生成完成" << std::endl;
    }
    catch (const std::exception& e) {
        std::cerr << "[LLaVA] ❌ 多轮生成失败: " << e.what() << std::endl;
        throw;
    }
}

void LLaVAGenAI::finishChat() {
    std::lock_guard<std::mutex> lk(mtx_);

    if (!p_->chatting) return;

    std::cout << "[LLaVA] 结束多轮对话" << std::endl;

    try {
        p_->pipeline->finish_chat();
        p_->chatting = false;
        p_->cached_image_path.clear();

        std::cout << "[LLaVA] ✅ 多轮会话已结束" << std::endl;
    }
    catch (const std::exception& e) {
        std::cerr << "[LLaVA] ⚠️ 结束会话失败: " << e.what() << std::endl;
        p_->chatting = false;
        p_->cached_image_path.clear();
    }
}

bool LLaVAGenAI::isChatting() const {
    std::lock_guard<std::mutex> lk(mtx_);
    return p_->chatting;
}

} // namespace llava
