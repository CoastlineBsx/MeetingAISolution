#pragma once
#include <vector>
#include <string>
#include <fstream>
#include <cmath>
#include <cstdint>

namespace meetingai::util {

// 简单的 WAV 文件头（16kHz, mono, 16-bit PCM）
#pragma pack(push, 1)
struct WavHeader {
    // RIFF Header
    char riff[4] = {'R', 'I', 'F', 'F'};
    uint32_t file_size;
    char wave[4] = {'W', 'A', 'V', 'E'};

    // Format chunk
    char fmt[4] = {'f', 'm', 't', ' '};
    uint32_t fmt_size = 16;
    uint16_t audio_format = 1;  // PCM
    uint16_t num_channels = 1;  // Mono
    uint32_t sample_rate = 16000;
    uint32_t byte_rate = 16000 * 2;  // sample_rate * channels * bits/8
    uint16_t block_align = 2;  // channels * bits/8
    uint16_t bits_per_sample = 16;

    // Data chunk
    char data[4] = {'d', 'a', 't', 'a'};
    uint32_t data_size;
};
#pragma pack(pop)

// 生成测试音频：包含语音模拟（正弦波 + 包络）
inline bool GenerateTestWav(const std::string& filename, float duration_seconds = 3.0f) {
    const uint32_t sample_rate = 16000;
    const uint32_t num_samples = static_cast<uint32_t>(sample_rate * duration_seconds);

    std::vector<int16_t> samples(num_samples);

    // 生成简单的"语音"：多个频率的正弦波混合
    for (uint32_t i = 0; i < num_samples; ++i) {
        float t = static_cast<float>(i) / sample_rate;

        // 包络：模拟语音的音量变化
        float envelope = 0.5f * (1.0f + std::sin(2.0f * 3.14159f * 0.5f * t));

        // 混合多个频率（模拟人声的基频和谐波）
        float signal = 0.0f;
        signal += 0.4f * std::sin(2.0f * 3.14159f * 200.0f * t);  // 基频
        signal += 0.2f * std::sin(2.0f * 3.14159f * 400.0f * t);  // 2次谐波
        signal += 0.1f * std::sin(2.0f * 3.14159f * 600.0f * t);  // 3次谐波

        // 应用包络
        signal *= envelope;

        // 转换为 int16
        samples[i] = static_cast<int16_t>(signal * 16000.0f);
    }

    // 写入 WAV 文件
    WavHeader header;
    header.data_size = num_samples * sizeof(int16_t);
    header.file_size = sizeof(WavHeader) - 8 + header.data_size;

    std::ofstream file(filename, std::ios::binary);
    if (!file) {
        return false;
    }

    file.write(reinterpret_cast<const char*>(&header), sizeof(header));
    file.write(reinterpret_cast<const char*>(samples.data()), header.data_size);

    return file.good();
}

// 读取 WAV 文件，返回归一化的 float32 数据 [-1.0, 1.0]
inline std::vector<float> ReadWavFile(const std::string& filename, uint32_t* out_sample_rate = nullptr) {
    std::ifstream file(filename, std::ios::binary);
    if (!file) {
        throw std::runtime_error("Cannot open WAV file: " + filename);
    }

    // 读取 WAV 头
    WavHeader header;
    file.read(reinterpret_cast<char*>(&header), sizeof(header));

    // 简单验证
    if (std::string(header.riff, 4) != "RIFF" ||
        std::string(header.wave, 4) != "WAVE" ||
        std::string(header.data, 4) != "data") {
        throw std::runtime_error("Invalid WAV file format");
    }

    if (header.audio_format != 1) {
        throw std::runtime_error("Only PCM format is supported");
    }

    if (out_sample_rate) {
        *out_sample_rate = header.sample_rate;
    }

    // 读取 PCM 数据
    uint32_t num_samples = header.data_size / (header.bits_per_sample / 8) / header.num_channels;
    std::vector<float> samples;
    samples.reserve(num_samples);

    if (header.bits_per_sample == 16) {
        std::vector<int16_t> pcm_data(num_samples * header.num_channels);
        file.read(reinterpret_cast<char*>(pcm_data.data()), header.data_size);

        // 转换为 float32 并归一化
        for (uint32_t i = 0; i < num_samples; ++i) {
            // 如果是立体声，只取第一个声道
            int16_t sample = pcm_data[i * header.num_channels];
            samples.push_back(static_cast<float>(sample) / 32768.0f);
        }
    } else {
        throw std::runtime_error("Only 16-bit PCM is supported");
    }

    return samples;
}

} // namespace meetingai::util
