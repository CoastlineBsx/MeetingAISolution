#include "pch.h"
#include "base64.h"
#include <stdexcept>

namespace meetingai::util {

// Base64 解码表
static const std::string base64_chars =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    "abcdefghijklmnopqrstuvwxyz"
    "0123456789+/";

static inline bool is_base64(unsigned char c) {
    return (isalnum(c) || (c == '+') || (c == '/'));
}

std::vector<unsigned char> Base64Decode(const std::string& encoded_string) {
    size_t in_len = encoded_string.size();
    int i = 0;
    int j = 0;
    int in_ = 0;
    unsigned char char_array_4[4], char_array_3[3];
    std::vector<unsigned char> ret;

    while (in_len-- && (encoded_string[in_] != '=') && is_base64(encoded_string[in_])) {
        char_array_4[i++] = encoded_string[in_]; in_++;
        if (i == 4) {
            for (i = 0; i < 4; i++)
                char_array_4[i] = static_cast<unsigned char>(base64_chars.find(char_array_4[i]));

            char_array_3[0] = (char_array_4[0] << 2) + ((char_array_4[1] & 0x30) >> 4);
            char_array_3[1] = ((char_array_4[1] & 0xf) << 4) + ((char_array_4[2] & 0x3c) >> 2);
            char_array_3[2] = ((char_array_4[2] & 0x3) << 6) + char_array_4[3];

            for (i = 0; (i < 3); i++)
                ret.push_back(char_array_3[i]);
            i = 0;
        }
    }

    if (i) {
        for (j = 0; j < i; j++)
            char_array_4[j] = static_cast<unsigned char>(base64_chars.find(char_array_4[j]));

        char_array_3[0] = (char_array_4[0] << 2) + ((char_array_4[1] & 0x30) >> 4);
        char_array_3[1] = ((char_array_4[1] & 0xf) << 4) + ((char_array_4[2] & 0x3c) >> 2);

        for (j = 0; (j < i - 1); j++) ret.push_back(char_array_3[j]);
    }

    return ret;
}

std::vector<float> Base64DecodeToFloat(const std::string& base64Audio) {
    // 1. Base64 解码到字节
    std::vector<unsigned char> bytes = Base64Decode(base64Audio);

    // 2. 字节转换为 int16 样本
    size_t numSamples = bytes.size() / 2;
    std::vector<float> samples(numSamples);

    for (size_t i = 0; i < numSamples; ++i) {
        // Little-endian int16
        int16_t sample = static_cast<int16_t>(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        // 归一化到 [-1.0, 1.0]
        samples[i] = static_cast<float>(sample) / 32768.0f;
    }

    return samples;
}

} // namespace meetingai::util
