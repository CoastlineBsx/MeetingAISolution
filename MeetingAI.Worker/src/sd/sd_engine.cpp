// 禁用 CRT 安全警告（stb_image_write.h 使用了 sprintf）
#define _CRT_SECURE_NO_WARNINGS

#include "sd_engine.hpp"

#include <iostream>
#include <fstream>
#include <random>
#include <chrono>
#include <filesystem>
#include <thread>
#include <atomic>

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

        std::string actual_device = device;
        bool gpu_fallback_attempted = false;

        try {
            std::cout << "[SD Engine] 🎨 Initializing Stable Diffusion on " << actual_device << "..." << std::endl;

            p_->device = actual_device;
            p_->rng.seed(std::chrono::steady_clock::now().time_since_epoch().count());

            // GPU 设备需要特殊配置
            if (actual_device == "GPU" || actual_device == "GPU.0" || actual_device == "GPU.1") {
                std::cout << "[SD Engine] Applying GPU optimizations..." << std::endl;
                // OpenVINO GPU 配置：添加设备属性
                ov::AnyMap config;
                config["GPU_THROTTLE_LEVEL"] = "0";  // 禁用节流
                config["CACHE_DIR"] = "";  // 禁用缓存以避免冲突

                std::cout << "[SD Engine] Loading Text2Image Pipeline with GPU config..." << std::endl;
                try {
                    p_->text2img_pipe = std::make_unique<ov::genai::Text2ImagePipeline>(model_path, actual_device, config);
                    std::cout << "[SD Engine] Loading Image2Image Pipeline with GPU config..." << std::endl;
                    p_->img2img_pipe = std::make_unique<ov::genai::Image2ImagePipeline>(model_path, actual_device, config);
                } catch (const std::exception& gpu_err) {
                    std::cerr << "[SD Engine] ⚠️ GPU initialization failed: " << gpu_err.what() << std::endl;
                    std::cerr << "[SD Engine] Falling back to CPU..." << std::endl;
                    actual_device = "CPU";
                    p_->device = actual_device;
                    gpu_fallback_attempted = true;

                    // 重试使用 CPU
                    p_->text2img_pipe = std::make_unique<ov::genai::Text2ImagePipeline>(model_path, actual_device);
                    p_->img2img_pipe = std::make_unique<ov::genai::Image2ImagePipeline>(model_path, actual_device);
                }
            } else {
                // CPU/NPU 直接加载
                std::cout << "[SD Engine] Loading Text2Image Pipeline..." << std::endl;
                p_->text2img_pipe = std::make_unique<ov::genai::Text2ImagePipeline>(model_path, actual_device);

                std::cout << "[SD Engine] Loading Image2Image Pipeline..." << std::endl;
                p_->img2img_pipe = std::make_unique<ov::genai::Image2ImagePipeline>(model_path, actual_device);
            }

            initialized_ = true;
            if (gpu_fallback_attempted) {
                std::cout << "[SD Engine] ✅ Initialization complete on " << actual_device << " (fallback from GPU)" << std::endl;
            } else {
                std::cout << "[SD Engine] ✅ Initialization complete on " << actual_device << std::endl;
            }

        } catch (const std::exception& e) {
            last_error_ = std::string("Initialization failed on ") + actual_device + ": " + e.what();
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

                // 启动后台进度模拟线程
                std::atomic<bool> generation_done{false};
                std::thread progress_thread;

                if (on_progress) {
                    progress_thread = std::thread([&]() {
                        int step = 0;
                        while (!generation_done && step < config.num_inference_steps) {
                            on_progress(step, config.num_inference_steps, "");
                            step += 1;
                            std::this_thread::sleep_for(std::chrono::milliseconds(200));
                        }
                        // 最后发送 100% 进度
                        if (generation_done) {
                            on_progress(config.num_inference_steps, config.num_inference_steps, "");
                        }
                    });
                }

                // 使用 OpenVINO GenAI Pipeline（在后台线程发送进度的同时生成）
                result_image = p_->text2img_pipe->generate(
                    config.prompt,
                    ov::genai::width(config.width),
                    ov::genai::height(config.height),
                    ov::genai::num_inference_steps(config.num_inference_steps),
                    ov::genai::guidance_scale(config.guidance_scale),
                    ov::genai::num_images_per_prompt(1)
                    // TODO: 如果 API 支持，添加 negative_prompt 和 seed
                );

                // 生成完成，停止进度线程
                generation_done = true;
                if (progress_thread.joinable()) {
                    progress_thread.join();
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
            
            // 从 Tensor 提取图像数据（工业标准：强断言方式）
            auto element_type = result_image.get_element_type();
            if (element_type != ov::element::u8) {
                std::cerr << "[SD Engine] ❌ ERROR: Unexpected tensor type: "
                          << element_type.to_string() << std::endl;
                std::cerr << "[SD Engine] Expected: u8" << std::endl;
                throw std::runtime_error("SD pipeline output wrong tensor type, expected u8 but got " + element_type.to_string());
            }

            const uint8_t* data = result_image.data<uint8_t>();
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

            // 直接使用 u8 数据，无需转换（已经是 0-255 范围）
            std::vector<uint8_t> image_data(img_height * img_width * 3);

            for (int i = 0; i < img_height * img_width * channels; i++) {
                image_data[i] = data[i];
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
            std::string error_msg = e.what();
            last_error_ = std::string("Generation failed: ") + error_msg;
            std::cerr << "[SD Engine] ❌ " << last_error_ << std::endl;

            // 检查是否是 OpenCL GPU 错误
            if (error_msg.find("CL_") != std::string::npos ||
                error_msg.find("GPU") != std::string::npos ||
                error_msg.find("ocl_memory") != std::string::npos) {

                std::cerr << "\n[SD Engine] 💡 GPU 错误解决建议：" << std::endl;
                std::cerr << "  1. 重新加载 SD 模型时选择 CPU 设备" << std::endl;
                std::cerr << "  2. 减少图像尺寸（如 256x256 或 384x384）" << std::endl;
                std::cerr << "  3. 减少推理步数（如 10-15 步）" << std::endl;
                std::cerr << "  4. 更新 GPU 驱动程序" << std::endl;
                std::cerr << "  5. 检查是否有其他程序占用 GPU" << std::endl;

                last_error_ += "\n\n建议：重新加载 SD 模型时选择 CPU 设备，或减少图像尺寸/步数";
            }

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
