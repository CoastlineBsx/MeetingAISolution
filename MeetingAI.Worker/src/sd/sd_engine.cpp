// 禁用 CRT 安全警告（stb_image_write.h 使用了 sprintf）
#define _CRT_SECURE_NO_WARNINGS

#include "sd_engine.hpp"

#include <iostream>
#include <fstream>
#include <random>
#include <chrono>
#include <filesystem>
#include <thread>

// OpenVINO GenAI
#include <openvino/genai/image_generation/text2image_pipeline.hpp>
#include <openvino/genai/image_generation/image2image_pipeline.hpp>

// 使用 stb_image 进行图片读写
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "../util/stb_image_write.h"

namespace fs = std::filesystem;

namespace meetingai::sd {

    // 内部实现
    struct SDEngine::Impl {
        std::unique_ptr<ov::genai::Text2ImagePipeline> text2img_pipe;
        std::unique_ptr<ov::genai::Image2ImagePipeline> img2img_pipe;
        std::string device;
        std::mt19937 rng;
    };

    SDEngine::SDEngine(const std::string& model_path, const std::string& device)
        : p_(std::make_unique<Impl>()) {
        
        try {
            std::cout << "[SD Engine] 🎨 Initializing Stable Diffusion on " << device << "..." << std::endl;
            
            p_->device = device;
            p_->rng.seed(std::chrono::steady_clock::now().time_since_epoch().count());
            
            // 使用 OpenVINO GenAI Pipeline（一行搞定！）
            std::cout << "[SD Engine] Loading Text2Image Pipeline..." << std::endl;
            p_->text2img_pipe = std::make_unique<ov::genai::Text2ImagePipeline>(model_path, device);
            
            std::cout << "[SD Engine] Loading Image2Image Pipeline..." << std::endl;
            p_->img2img_pipe = std::make_unique<ov::genai::Image2ImagePipeline>(model_path, device);
            
            initialized_ = true;
            std::cout << "[SD Engine] ✅ Initialization complete!" << std::endl;
            
        } catch (const std::exception& e) {
            last_error_ = std::string("Initialization failed: ") + e.what();
            std::cerr << "[SD Engine] ❌ " << last_error_ << std::endl;
            initialized_ = false;
        }
    }

    SDEngine::~SDEngine() {
        std::cout << "[SD Engine] 🛑 Shutting down..." << std::endl;
    }

    std::string SDEngine::generateTextToImage(
        const GenerationConfig& config,
        ProgressCallback on_progress) {
        
        return generateInternal(GenerationMode::TEXT_TO_IMAGE, config, on_progress);
    }

    std::string SDEngine::generateImageToImage(
        const GenerationConfig& config,
        ProgressCallback on_progress) {
        
        if (config.input_image_path.empty()) {
            last_error_ = "Input image path is required for img2img";
            return "";
        }
        
        return generateInternal(GenerationMode::IMAGE_TO_IMAGE, config, on_progress);
    }

    std::string SDEngine::generateInternal(
        GenerationMode mode,
        const GenerationConfig& config,
        ProgressCallback on_progress) {
        
        if (!initialized_) {
            last_error_ = "Engine not initialized";
            return "";
        }

        try {
            std::cout << "\n[SD Engine] 🎨 Starting generation..." << std::endl;
            std::cout << "  Prompt: " << config.prompt << std::endl;
            std::cout << "  Size: " << config.width << "x" << config.height << std::endl;
            std::cout << "  Steps: " << config.num_inference_steps << std::endl;
            std::cout << "  CFG: " << config.guidance_scale << std::endl;
            
            // 设置随机种子
            int seed = config.seed;
            if (seed < 0) {
                seed = static_cast<int>(p_->rng());
            }
            std::cout << "  Seed: " << seed << std::endl;
            
            ov::Tensor result_image;
            
            if (mode == GenerationMode::TEXT_TO_IMAGE) {
                // ========== Text-to-Image ==========
                std::cout << "[SD Engine] Mode: Text-to-Image" << std::endl;
                
                // 使用 OpenVINO GenAI Pipeline（超简单！）
                result_image = p_->text2img_pipe->generate(
                    config.prompt,
                    ov::genai::width(config.width),
                    ov::genai::height(config.height),
                    ov::genai::num_inference_steps(config.num_inference_steps),
                    ov::genai::guidance_scale(config.guidance_scale),
                    ov::genai::num_images_per_prompt(1)
                    // TODO: 如果 API 支持，添加 negative_prompt 和 seed
                );
                
                // 模拟进度回调（因为 Pipeline 没有暴露中间步骤）
                if (on_progress) {
                    for (int step = 0; step <= config.num_inference_steps; step += 5) {
                        on_progress(step, config.num_inference_steps, "");
                        if (step < config.num_inference_steps) {
                            std::this_thread::sleep_for(std::chrono::milliseconds(100));
                        }
                    }
                }
                
            } else {
                // ========== Image-to-Image ==========
                std::cout << "[SD Engine] Mode: Image-to-Image" << std::endl;
                std::cout << "  Input image: " << config.input_image_path << std::endl;
                std::cout << "  Strength: " << config.strength << std::endl;
                
                // TODO: 加载输入图片并转换为 ov::Tensor
                // 这里需要实现图片加载逻辑
                
                // 暂时抛出错误
                throw std::runtime_error("Image-to-Image mode not yet implemented");
            }
            
            // 从 Tensor 提取图像数据
            const float* data = result_image.data<float>();
            auto shape = result_image.get_shape();
            
            // shape 通常是 [1, height, width, 3] 或 [1, 3, height, width]
            int img_height = static_cast<int>(shape[1]);
            int img_width = static_cast<int>(shape[2]);
            int channels = static_cast<int>(shape[3]);
            
            // 如果是 CHW 格式，需要转置
            bool is_chw = (channels != 3);
            if (is_chw) {
                channels = static_cast<int>(shape[1]);
                img_height = static_cast<int>(shape[2]);
                img_width = static_cast<int>(shape[3]);
            }
            
            // 转换为 uint8 [0, 255]
            std::vector<uint8_t> image_data(img_height * img_width * 3);
            
            for (int i = 0; i < img_height * img_width * channels; i++) {
                float val = data[i];
                // 假设输出范围是 [0, 1] 或 [-1, 1]
                if (val < 0) val = (val + 1.0f) * 0.5f;  // [-1, 1] -> [0, 1]
                val = std::clamp(val * 255.0f, 0.0f, 255.0f);
                image_data[i] = static_cast<uint8_t>(val);
            }
            
            // 如果是 CHW 格式，需要转置为 HWC
            if (is_chw) {
                std::vector<uint8_t> hwc_data(img_height * img_width * 3);
                for (int h = 0; h < img_height; h++) {
                    for (int w = 0; w < img_width; w++) {
                        for (int c = 0; c < 3; c++) {
                            hwc_data[(h * img_width + w) * 3 + c] = 
                                image_data[c * img_height * img_width + h * img_width + w];
                        }
                    }
                }
                image_data = hwc_data;
            }
            
            // 保存图片
            std::string output_path = saveImage(
                image_data,
                img_width,
                img_height,
                (mode == GenerationMode::TEXT_TO_IMAGE ? "txt2img" : "img2img")
            );
            
            std::cout << "[SD Engine] ✅ Generated: " << output_path << std::endl;
            
            return output_path;
            
        } catch (const std::exception& e) {
            last_error_ = std::string("Generation failed: ") + e.what();
            std::cerr << "[SD Engine] ❌ " << last_error_ << std::endl;
            return "";
        }
    }

    std::string SDEngine::saveImage(
        const std::vector<uint8_t>& image_data,
        int width, int height,
        const std::string& prefix) {
        
        // 创建输出目录
        fs::path output_dir = "C:\\Temp\\MeetingAI_SD";
        fs::create_directories(output_dir);
        
        // 生成文件名（时间戳）
        auto now = std::chrono::system_clock::now();
        auto timestamp = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()
        ).count();
        
        std::string filename = prefix + "_" + std::to_string(timestamp) + ".png";
        fs::path output_path = output_dir / filename;
        
        // 保存为 PNG
        int result = stbi_write_png(
            output_path.string().c_str(),
            width, height, 3,
            image_data.data(),
            width * 3
        );
        
        if (result == 0) {
            last_error_ = "Failed to write image file";
            return "";
        }
        
        return output_path.string();
    }

} // namespace meetingai::sd
