#pragma once

#include <openvino/openvino.hpp>
#include <string>

namespace llava {

/**
 * @brief 图片预处理工具
 *
 * 负责加载、Resize、Normalize 图片
 */
class ImageProcessor {
public:
    /**
     * @brief 预处理图片
     * @param image_path 图片路径
     * @param target_width 目标宽度（默认672）
     * @param target_height 目标高度（默认672）
     * @return OpenVINO Tensor (NCHW 格式)
     */
    static ov::Tensor preprocess(
        const std::string& image_path,
        int target_width = 672,
        int target_height = 672
    );

private:
    /**
     * @brief Resize with padding (保持宽高比)
     */
    static void resizeWithPadding(
        const unsigned char* src_data,
        int src_width,
        int src_height,
        int src_channels,
        unsigned char* dst_data,
        int dst_width,
        int dst_height
    );

    /**
     * @brief Normalize (CLIP 标准)
     */
    static void normalize(
        const unsigned char* src_data,
        float* dst_data,
        int width,
        int height,
        int channels
    );
};

} // namespace llava
