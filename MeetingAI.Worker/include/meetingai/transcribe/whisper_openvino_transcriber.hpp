#pragma once

#include <string>
#include <vector>
#include <functional>

namespace meetingai::transcribe {

// 转录结果分段
struct WhisperOpenVINOSegment {
    float start_ts;      // 开始时间（秒）
    float end_ts;        // 结束时间（秒）
    std::string text;    // 转录文本
};

// 进度回调函数类型
// 参数：当前进度百分比 (0-100)
using ProgressCallback = std::function<void(int)>;

/**
 * @brief 加载 OpenVINO Whisper 模型到内存（用于热加载）
 * @param modelPath OpenVINO 模型路径 (例: "models/whisper_large_v3")
 * @param device 设备类型 ("CPU", "GPU", "NPU")
 * @return true 成功, false 失败
 */
bool LoadWhisperOpenVINOModel(const std::string& modelPath, const std::string& device = "CPU");

/**
 * @brief 卸载 OpenVINO Whisper 模型（释放内存）
 */
void UnloadWhisperOpenVINOModel();

/**
 * @brief 检查模型是否已加载
 * @return true 已加载, false 未加载
 */
bool IsWhisperOpenVINOModelLoaded();

/**
 * @brief 使用 OpenVINO Whisper 转录音频文件
 *
 * @param modelPath OpenVINO 模型路径 (例: "models/whisper_large_v3")
 * @param audioPath 音频文件路径 (必须是 16kHz WAV)
 * @param segments 输出：转录结果分段列表
 * @param language 语言代码 ("auto", "zh", "en", "ja", 等)
 * @param progressCallback 可选：进度回调函数
 * @return true 成功, false 失败
 */
bool TranscribeAudioFileOpenVINO(
    const std::string& modelPath,
    const std::string& audioPath,
    std::vector<WhisperOpenVINOSegment>& segments,
    const std::string& language = "auto",
    ProgressCallback progressCallback = nullptr,
    const std::string& hotwords = {}
);

} // namespace meetingai::transcribe
