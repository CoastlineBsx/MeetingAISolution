#pragma once

#include <string>
#include <memory>
#include <functional>
#include <vector>

namespace meetingai::sd {

    // SD 生成模式
    enum class GenerationMode {
        TEXT_TO_IMAGE,  // 文生图
        IMAGE_TO_IMAGE  // 图生图
    };

    // 生成参数
    struct GenerationConfig {
        std::string prompt;                    // 提示词
        std::string negative_prompt;           // 反向提示词
        int width = 512;                       // 宽度
        int height = 512;                      // 高度
        int num_inference_steps = 20;          // 推理步数
        float guidance_scale = 7.5f;           // CFG Scale
        int seed = -1;                         // 随机种子 (-1=随机)
        
        // Image-to-Image 专用
        std::string input_image_path;          // 输入图片路径
        float strength = 0.75f;                // 修改强度 (0.0-1.0)
    };

    // 进度回调: (当前步数, 总步数, 中间图片路径)
    using ProgressCallback = std::function<void(int current, int total, const std::string& preview_path)>;

    class SDEngine {
    public:
        // 构造函数
        // model_path: 模型根目录 (例如: "models/stable-deffusion-1.5")
        // device: 设备 ("CPU", "GPU", "NPU")
        SDEngine(const std::string& model_path, const std::string& device);
        ~SDEngine();

        // 禁止拷贝
        SDEngine(const SDEngine&) = delete;
        SDEngine& operator=(const SDEngine&) = delete;

        // Text-to-Image 生成
        // 返回生成的图片路径
        std::string generateTextToImage(
            const GenerationConfig& config,
            ProgressCallback on_progress = nullptr
        );

        // Image-to-Image 生成
        // 返回生成的图片路径
        std::string generateImageToImage(
            const GenerationConfig& config,
            ProgressCallback on_progress = nullptr
        );

        // 检查是否已初始化
        bool isInitialized() const { return initialized_; }

        // 获取最后一次错误信息
        std::string getLastError() const { return last_error_; }

    private:
        struct Impl;
        std::unique_ptr<Impl> p_;
        bool initialized_ = false;
        std::string last_error_;

        // 保存图片到文件
        std::string saveImage(const std::vector<uint8_t>& image_data, 
                             int width, int height, 
                             const std::string& prefix);

        // 生成核心逻辑
        std::string generateInternal(
            GenerationMode mode,
            const GenerationConfig& config,
            ProgressCallback on_progress
        );
    };

} // namespace meetingai::sd
