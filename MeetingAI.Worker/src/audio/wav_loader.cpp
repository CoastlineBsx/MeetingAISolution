#include "pch.h"
#include "audio/wav_loader.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <vector>

namespace {

constexpr std::uint32_t kTargetSampleRate = 16000;
constexpr double kPi = 3.14159265358979323846;

template <typename T>
bool ReadValue(std::istream& stream, T& value) {
    return static_cast<bool>(
        stream.read(reinterpret_cast<char*>(&value), sizeof(T)));
}

bool ReadFourCc(std::istream& stream, char (&value)[4]) {
    return static_cast<bool>(stream.read(value, sizeof(value)));
}

bool FourCcEquals(const char (&value)[4], const char* expected) {
    return std::memcmp(value, expected, 4) == 0;
}

double LanczosKernel(double x) {
    constexpr int radius = 3;
    if (x == 0.0) return 1.0;
    if (std::abs(x) >= radius) return 0.0;
    const double piX = kPi * x;
    return (radius * std::sin(piX) * std::sin(piX / radius)) /
        (piX * piX);
}

} // namespace

namespace meetingai::audio {

bool LoadWavFile16KhzMono(
    const std::string& filename,
    std::vector<float>& audioData) {
    audioData.clear();

    std::ifstream file(filename, std::ios::binary);
    if (!file) {
        std::cerr << "[Audio] Unable to open WAV file: " << filename
                  << std::endl;
        return false;
    }

    char riff[4]{};
    std::uint32_t riffSize = 0;
    char wave[4]{};
    if (!ReadFourCc(file, riff) || !ReadValue(file, riffSize) ||
        !ReadFourCc(file, wave) || !FourCcEquals(riff, "RIFF") ||
        !FourCcEquals(wave, "WAVE")) {
        std::cerr << "[Audio] Invalid RIFF/WAVE header: " << filename
                  << std::endl;
        return false;
    }

    std::uint16_t audioFormat = 0;
    std::uint16_t channelCount = 0;
    std::uint32_t sampleRate = 0;
    std::uint16_t bitsPerSample = 0;
    std::vector<std::uint8_t> sampleBytes;
    bool foundFormat = false;
    bool foundData = false;

    while (file && (!foundFormat || !foundData)) {
        char chunkId[4]{};
        std::uint32_t chunkSize = 0;
        if (!ReadFourCc(file, chunkId) || !ReadValue(file, chunkSize)) {
            break;
        }

        if (FourCcEquals(chunkId, "fmt ")) {
            if (chunkSize < 16 ||
                !ReadValue(file, audioFormat) ||
                !ReadValue(file, channelCount) ||
                !ReadValue(file, sampleRate)) {
                break;
            }

            std::uint32_t byteRate = 0;
            std::uint16_t blockAlign = 0;
            if (!ReadValue(file, byteRate) || !ReadValue(file, blockAlign) ||
                !ReadValue(file, bitsPerSample)) {
                break;
            }
            if (chunkSize > 16) {
                file.seekg(static_cast<std::streamoff>(chunkSize - 16),
                           std::ios::cur);
            }
            foundFormat = true;
        } else if (FourCcEquals(chunkId, "data")) {
            sampleBytes.resize(chunkSize);
            if (chunkSize > 0 &&
                !file.read(reinterpret_cast<char*>(sampleBytes.data()),
                           static_cast<std::streamsize>(chunkSize))) {
                break;
            }
            foundData = true;
        } else {
            file.seekg(static_cast<std::streamoff>(chunkSize), std::ios::cur);
        }

        // RIFF chunks are word aligned.
        if ((chunkSize & 1U) != 0U) {
            file.seekg(1, std::ios::cur);
        }
    }

    const bool isPcm16 = audioFormat == 1 && bitsPerSample == 16;
    const bool isFloat32 = audioFormat == 3 && bitsPerSample == 32;
    if (!foundFormat || !foundData || channelCount == 0 || sampleRate == 0 ||
        (!isPcm16 && !isFloat32)) {
        std::cerr << "[Audio] Unsupported or incomplete WAV file: format="
                  << audioFormat << ", channels=" << channelCount
                  << ", sampleRate=" << sampleRate
                  << ", bits=" << bitsPerSample << std::endl;
        return false;
    }

    const std::size_t bytesPerSample = bitsPerSample / 8;
    const std::size_t sampleCount = sampleBytes.size() / bytesPerSample;
    if (sampleCount < channelCount) {
        std::cerr << "[Audio] WAV data chunk is empty: " << filename
                  << std::endl;
        return false;
    }

    std::vector<float> interleaved(sampleCount);
    for (std::size_t i = 0; i < sampleCount; ++i) {
        const auto* source = sampleBytes.data() + (i * bytesPerSample);
        if (isPcm16) {
            std::int16_t value = 0;
            std::memcpy(&value, source, sizeof(value));
            interleaved[i] = static_cast<float>(value) / 32768.0f;
        } else {
            std::memcpy(&interleaved[i], source, sizeof(float));
        }
    }

    const std::size_t frameCount = sampleCount / channelCount;
    std::vector<float> mono(frameCount, 0.0f);
    for (std::size_t frame = 0; frame < frameCount; ++frame) {
        double sum = 0.0;
        for (std::uint16_t channel = 0; channel < channelCount; ++channel) {
            sum += interleaved[(frame * channelCount) + channel];
        }
        mono[frame] = static_cast<float>(sum / channelCount);
    }

    if (sampleRate == kTargetSampleRate) {
        audioData = std::move(mono);
    } else {
        const double ratio =
            static_cast<double>(sampleRate) / kTargetSampleRate;
        const std::size_t outputCount =
            static_cast<std::size_t>(mono.size() / ratio);
        audioData.resize(outputCount);

        for (std::size_t i = 0; i < outputCount; ++i) {
            const double sourceIndex = i * ratio;
            const int center = static_cast<int>(sourceIndex);
            double weightedSum = 0.0;
            double weightTotal = 0.0;
            for (int tap = -3; tap <= 3; ++tap) {
                const int sourcePosition = center + tap;
                if (sourcePosition < 0 ||
                    sourcePosition >= static_cast<int>(mono.size())) {
                    continue;
                }
                const double weight =
                    LanczosKernel(sourceIndex - sourcePosition);
                weightedSum += mono[sourcePosition] * weight;
                weightTotal += weight;
            }
            audioData[i] = static_cast<float>(
                weightTotal != 0.0 ? weightedSum / weightTotal : 0.0);
        }
    }

    // Retain the voice-focused preprocessing used by the previous loader.
    constexpr float highPassAlpha = 0.95f;
    float previousInput = 0.0f;
    float previousOutput = 0.0f;
    for (float& sample : audioData) {
        const float currentInput = sample;
        const float currentOutput = highPassAlpha *
            (previousOutput + currentInput - previousInput);
        sample = currentOutput;
        previousInput = currentInput;
        previousOutput = currentOutput;
    }

    float peak = 0.0f;
    for (float sample : audioData) {
        peak = (std::max)(peak, std::abs(sample));
    }
    if (peak > 0.01f) {
        const float scale = 0.8f / peak;
        for (float& sample : audioData) {
            sample *= scale;
        }
    }

    std::cout << "[Audio] Loaded " << filename << ": " << sampleRate
              << " Hz, " << channelCount << " channel(s), "
              << (static_cast<double>(audioData.size()) / kTargetSampleRate)
              << " seconds at 16 kHz mono" << std::endl;
    return !audioData.empty();
}

} // namespace meetingai::audio
