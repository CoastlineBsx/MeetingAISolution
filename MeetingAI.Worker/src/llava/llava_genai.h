#pragma once

#include <string>
#include <functional>
#include <memory>
#include <mutex>

namespace llava {

/**
 * @brief LLaVA 多模态推理引擎 (基于 OpenVINO GenAI VLMPipeline)
 *
 * 支持图文问答，包含单轮和多轮对话模式
 */
class LLaVAGenAI {
public:
    /**
     * @brief 构造函数
     * @param model_path 模型目录路径（包含所有 LLaVA 模型文件）
     * @param device 设备 ("NPU", "CPU", "GPU")
     */
    LLaVAGenAI(const std::string& model_path, const std::string& device);
    ~LLaVAGenAI();

    /**
     * @brief 单轮模式：生成回答（每次重新编码图片）
     * @param image_path 图片路径
     * @param prompt 用户问题
     * @param on_token 流式回调
     * @param max_tokens 最大生成长度
     * @param temperature 温度参数
     */
    void generateStream(
        const std::string& image_path,
        const std::string& prompt,
        std::function<void(const std::string&)> on_token,
        int max_tokens = 512,
        float temperature = 0.7f
    );

    /**
     * @brief 多轮模式：开始对话（编码图片并缓存）
     * @param image_path 图片路径
     */
    void startChat(const std::string& image_path);

    /**
     * @brief 多轮模式：发送问题（复用缓存的图片特征）
     * @param prompt 用户问题
     * @param on_token 流式回调
     * @param max_tokens 最大生成长度
     * @param temperature 温度参数
     */
    void chatStream(
        const std::string& prompt,
        std::function<void(const std::string&)> on_token,
        int max_tokens = 512,
        float temperature = 0.7f
    );

    /**
     * @brief 多轮模式：结束对话（清除缓存）
     */
    void finishChat();

    /**
     * @brief 检查是否正在多轮对话
     */
    bool isChatting() const;

private:
    struct Impl;
    std::unique_ptr<Impl> p_;
    mutable std::mutex mtx_;
};

} // namespace llava
