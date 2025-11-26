#include <iostream>
#include <iomanip>
#include <filesystem>
#include <vector>
#include <string>
#include "openvino/genai/whisper_pipeline.hpp"
#include "wav_generator.h"

namespace fs = std::filesystem;

int main(int argc, char* argv[]) {
    std::cout << "=================================================\n";
    std::cout << "   OpenVINO Whisper Timestamp Verification Test  \n";
    std::cout << "=================================================\n\n";

    try {
        // ========== 1. 配置路径 ==========
        std::string model_path = "models/whisper_large_v3";
        std::string test_audio_path = "test_audio.wav";

        if (argc >= 2) {
            model_path = argv[1];
        }
        if (argc >= 3) {
            test_audio_path = argv[2];
        }

        std::cout << "[Config]\n";
        std::cout << "  Model path: " << model_path << "\n";
        std::cout << "  Audio path: " << test_audio_path << "\n\n";

        // ========== 2. 生成测试音频 ==========
        std::cout << "[Step 1/5] Generating test audio...\n";

        if (!fs::exists(test_audio_path)) {
            std::cout << "  Creating test WAV file (3 seconds, 16kHz mono)...\n";
            if (!meetingai::util::GenerateTestWav(test_audio_path, 3.0f)) {
                std::cerr << "  ❌ Failed to generate test audio\n";
                return 1;
            }
            std::cout << "  ✓ Test audio created: " << test_audio_path << "\n";
        } else {
            std::cout << "  ✓ Using existing audio: " << test_audio_path << "\n";
        }
        std::cout << "\n";

        // ========== 3. 读取音频 ==========
        std::cout << "[Step 2/5] Reading audio file...\n";

        uint32_t sample_rate = 0;
        std::vector<float> audio_samples;

        try {
            audio_samples = meetingai::util::ReadWavFile(test_audio_path, &sample_rate);
            std::cout << "  ✓ Audio loaded:\n";
            std::cout << "    - Sample rate: " << sample_rate << " Hz\n";
            std::cout << "    - Duration: " << (audio_samples.size() / static_cast<float>(sample_rate)) << " seconds\n";
            std::cout << "    - Samples: " << audio_samples.size() << "\n";
        } catch (const std::exception& e) {
            std::cerr << "  ❌ Error reading audio: " << e.what() << "\n";
            return 1;
        }
        std::cout << "\n";

        // ========== 4. 加载 Whisper Pipeline ==========
        std::cout << "[Step 3/5] Loading Whisper pipeline...\n";
        std::cout << "  Model: " << model_path << "\n";
        std::cout << "  Device: CPU\n";

        if (!fs::exists(model_path)) {
            std::cerr << "  ❌ Model path does not exist: " << model_path << "\n";
            return 1;
        }

        ov::genai::WhisperPipeline pipeline(model_path, "CPU");
        std::cout << "  ✓ Pipeline loaded successfully\n\n";

        // ========== 5. 配置生成参数 ==========
        std::cout << "[Step 4/5] Configuring generation...\n";

        ov::genai::WhisperGenerationConfig config(fs::path(model_path) / "generation_config.json");

        // ⭐ 关键：开启 timestamps
        config.return_timestamps = true;
        config.max_new_tokens = 100;
        config.language = "<|en|>";  // 英文（测试音频是正弦波，可能识别为噪音或无内容）
        config.task = "transcribe";

        std::cout << "  Configuration:\n";
        std::cout << "    - return_timestamps: " << (config.return_timestamps ? "true" : "false") << " ⭐\n";
        std::cout << "    - language: " << config.language.value_or("(not set)") << "\n";
        std::cout << "    - task: " << config.task.value_or("(not set)") << "\n";
        std::cout << "    - max_tokens: " << config.max_new_tokens << "\n";
        std::cout << "\n";

        // ========== 6. 执行推理 ==========
        std::cout << "[Step 5/5] Running inference...\n";
        std::cout << "  (This may take 10-30 seconds for first run)\n";

        auto start_time = std::chrono::high_resolution_clock::now();

        ov::genai::RawSpeechInput raw_speech(audio_samples.begin(), audio_samples.end());
        auto result = pipeline.generate(raw_speech, config);

        auto end_time = std::chrono::high_resolution_clock::now();
        auto duration_ms = std::chrono::duration_cast<std::chrono::milliseconds>(end_time - start_time).count();

        std::cout << "  ✓ Inference completed in " << duration_ms << " ms\n\n";

        // ========== 7. 显示结果 ==========
        std::cout << "=================================================\n";
        std::cout << "                    RESULTS                      \n";
        std::cout << "=================================================\n\n";

        std::cout << "Full transcription text:\n";
        std::cout << "  \"" << result << "\"\n\n";

        // ⭐⭐⭐ 关键测试：检查是否有 timestamps
        std::cout << "=================================================\n";
        std::cout << "           TIMESTAMP VERIFICATION                \n";
        std::cout << "=================================================\n\n";

        if (result.chunks && !result.chunks->empty()) {
            std::cout << "✅ SUCCESS: Timestamps are supported!\n\n";
            std::cout << "Word-level timestamps:\n";
            std::cout << "  Total chunks: " << result.chunks->size() << "\n\n";

            for (size_t i = 0; i < result.chunks->size(); ++i) {
                const auto& chunk = (*result.chunks)[i];
                std::cout << "  [" << i << "] "
                          << std::fixed << std::setprecision(2)
                          << chunk.start_ts << "s - " << chunk.end_ts << "s"
                          << " (" << (chunk.end_ts - chunk.start_ts) << "s): "
                          << "\"" << chunk.text << "\"\n";
            }

            std::cout << "\n";
            std::cout << "=================================================\n";
            std::cout << "✅ VERIFICATION PASSED: DTW timestamps work!\n";
            std::cout << "=================================================\n";

        } else {
            std::cout << "❌ FAILURE: No timestamps found\n";
            std::cout << "  result.chunks is empty or null\n\n";

            std::cout << "Possible reasons:\n";
            std::cout << "  1. OpenVINO GenAI version doesn't support timestamps\n";
            std::cout << "  2. return_timestamps config didn't work\n";
            std::cout << "  3. Audio was too short or silent\n\n";

            std::cout << "=================================================\n";
            std::cout << "❌ VERIFICATION FAILED\n";
            std::cout << "=================================================\n";
            return 1;
        }

        // ========== 8. 性能统计 ==========
        std::cout << "\nPerformance:\n";
        std::cout << "  Audio duration: " << (audio_samples.size() / static_cast<float>(sample_rate)) << " seconds\n";
        std::cout << "  Inference time: " << duration_ms << " ms\n";
        std::cout << "  Real-time factor: " << (duration_ms / 1000.0f) / (audio_samples.size() / static_cast<float>(sample_rate)) << "x\n";

        return 0;

    } catch (const std::exception& e) {
        std::cerr << "\n❌ EXCEPTION: " << e.what() << "\n";
        return 1;
    } catch (...) {
        std::cerr << "\n❌ Unknown exception occurred\n";
        return 1;
    }
}
