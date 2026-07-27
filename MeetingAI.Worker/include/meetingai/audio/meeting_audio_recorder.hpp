#pragma once

#include <cstdint>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace meetingai::audio {

// 将 Worker 实际送入 Sherpa 的 16 kHz 单声道样本按来源保存。
// 录音与实时识别使用同一份样本，因此时间戳天然对齐。
class MeetingAudioRecorder {
public:
    MeetingAudioRecorder() = default;
    ~MeetingAudioRecorder();

    MeetingAudioRecorder(const MeetingAudioRecorder&) = delete;
    MeetingAudioRecorder& operator=(const MeetingAudioRecorder&) = delete;

    bool Start(
        std::int64_t meetingId,
        const std::vector<std::string>& sources,
        int sampleRate,
        std::string& error);

    bool Append(
        const std::string& source,
        const float* samples,
        int sampleCount);

    // 完成 WAV 头并关闭所有文件。返回 source -> UTF-8 路径。
    std::unordered_map<std::string, std::string> Stop();

    bool IsRecording() const;
    std::unordered_map<std::string, std::string> Paths() const;

private:
    struct Output {
        std::ofstream file;
        std::string pathUtf8;
        std::uint64_t sampleCount = 0;
        std::uint64_t samplesAtLastHeaderFlush = 0;
    };

    static bool WriteHeader(
        std::ofstream& file,
        int sampleRate,
        std::uint64_t sampleCount);
    void StopLocked();

    mutable std::mutex mutex_;
    std::unordered_map<std::string, Output> outputs_;
    std::int64_t meetingId_ = 0;
    int sampleRate_ = 16000;
    bool recording_ = false;
};

} // namespace meetingai::audio
