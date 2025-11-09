#include "image_processor.h"
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <algorithm>

// 使用 stb_image 加载图片（轻量级，无需 OpenCV）
#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

namespace llava {

// CLIP 标准化参数
constexpr float MEAN_R = 0.48145466f;
constexpr float MEAN_G = 0.4578275f;
constexpr float MEAN_B = 0.40821073f;

constexpr float STD_R = 0.26862954f;
constexpr float STD_G = 0.26130258f;
constexpr float STD_B = 0.27577711f;

ov::Tensor ImageProcessor::preprocess(
    const std::string& image_path,
    int target_width,
    int target_height
) {
    // 1. 加载图片
    int width, height, channels;
    unsigned char* image_data = stbi_load(
        image_path.c_str(),
        &width,
        &height,
        &channels,
        3  // 强制转换为 RGB
    );

    if (!image_data) {
        throw std::runtime_error("Failed to load image: " + image_path);
    }

    std::cout << "[ImageProcessor] 加载图片: " << width << "x" << height
              << " channels=" << channels << std::endl;

    // 2. Resize with padding
    auto resized_data = std::make_unique<unsigned char[]>(target_width * target_height * 3);
    resizeWithPadding(
        image_data, width, height, 3,
        resized_data.get(), target_width, target_height
    );

    stbi_image_free(image_data);

    // 3. Normalize
    auto normalized_data = std::make_unique<float[]>(target_width * target_height * 3);
    normalize(
        resized_data.get(),
        normalized_data.get(),
        target_width,
        target_height,
        3
    );

    // 4. 转换为 OpenVINO Tensor (NCHW 格式)
    ov::Shape shape = {1, 3, static_cast<size_t>(target_height), static_cast<size_t>(target_width)};
    ov::Tensor tensor(ov::element::f32, shape);

    float* tensor_data = tensor.data<float>();

    // HWC -> NCHW
    for (int c = 0; c < 3; ++c) {
        for (int h = 0; h < target_height; ++h) {
            for (int w = 0; w < target_width; ++w) {
                int src_idx = (h * target_width + w) * 3 + c;
                int dst_idx = c * (target_height * target_width) + h * target_width + w;
                tensor_data[dst_idx] = normalized_data[src_idx];
            }
        }
    }

    std::cout << "[ImageProcessor] ✅ 预处理完成: " << target_width << "x" << target_height << std::endl;

    return tensor;
}

void ImageProcessor::resizeWithPadding(
    const unsigned char* src_data,
    int src_width,
    int src_height,
    int src_channels,
    unsigned char* dst_data,
    int dst_width,
    int dst_height
) {
    // 计算缩放比例（保持宽高比）
    float scale = std::min(
        static_cast<float>(dst_width) / src_width,
        static_cast<float>(dst_height) / src_height
    );

    int new_width = static_cast<int>(src_width * scale);
    int new_height = static_cast<int>(src_height * scale);

    // 计算 padding
    int pad_x = (dst_width - new_width) / 2;
    int pad_y = (dst_height - new_height) / 2;

    // 初始化为黑色
    std::fill_n(dst_data, dst_width * dst_height * src_channels, 0);

    // 简单最近邻插值 resize + padding
    for (int dst_y = 0; dst_y < new_height; ++dst_y) {
        for (int dst_x = 0; dst_x < new_width; ++dst_x) {
            int src_x = static_cast<int>(dst_x / scale);
            int src_y = static_cast<int>(dst_y / scale);

            // 边界检查
            src_x = std::min(src_x, src_width - 1);
            src_y = std::min(src_y, src_height - 1);

            int src_idx = (src_y * src_width + src_x) * src_channels;
            int dst_idx = ((pad_y + dst_y) * dst_width + (pad_x + dst_x)) * src_channels;

            for (int c = 0; c < src_channels; ++c) {
                dst_data[dst_idx + c] = src_data[src_idx + c];
            }
        }
    }

    std::cout << "[ImageProcessor] Resize: " << src_width << "x" << src_height
              << " -> " << new_width << "x" << new_height
              << " (padded to " << dst_width << "x" << dst_height << ")" << std::endl;
}

void ImageProcessor::normalize(
    const unsigned char* src_data,
    float* dst_data,
    int width,
    int height,
    int channels
) {
    const float means[3] = {MEAN_R, MEAN_G, MEAN_B};
    const float stds[3] = {STD_R, STD_G, STD_B};

    for (int i = 0; i < width * height; ++i) {
        for (int c = 0; c < channels; ++c) {
            int idx = i * channels + c;
            // 转换到 [0, 1]
            float pixel = src_data[idx] / 255.0f;
            // Normalize
            dst_data[idx] = (pixel - means[c]) / stds[c];
        }
    }
}

} // namespace llava
