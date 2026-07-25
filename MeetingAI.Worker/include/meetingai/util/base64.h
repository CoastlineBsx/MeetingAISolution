#pragma once
#include <string>
#include <vector>

namespace meetingai::util {

/// <summary>
/// Base64 解码
/// </summary>
std::vector<unsigned char> Base64Decode(const std::string& encoded_string);

/// <summary>
/// Base64 解码并转换为 float32 音频样本
/// </summary>
/// <param name="base64Audio">Base64 编码的 PCM int16 数据</param>
/// <returns>float32 音频样本 [-1.0, 1.0]</returns>
std::vector<float> Base64DecodeToFloat(const std::string& base64Audio);

} // namespace meetingai::util
