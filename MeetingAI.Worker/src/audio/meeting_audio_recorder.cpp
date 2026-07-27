#include "audio/meeting_audio_recorder.hpp"

#include "paths.h"

#include <algorithm>
#include <cmath>
#include <filesystem>
#include <limits>

namespace fs = std::filesystem;

namespace meetingai::audio {
namespace {

std::string PathToUtf8(const fs::path& path) {
    const std::u8string value = path.u8string();
    return std::string(value.begin(), value.end());
}

template <typename T>
void WriteValue(std::ofstream& file, T value) {
    file.write(
        reinterpret_cast<const char*>(&value),
        static_cast<std::streamsize>(sizeof(T)));
}

} // namespace

MeetingAudioRecorder::~MeetingAudioRecorder() {
    Stop();
}

bool MeetingAudioRecorder::Start(
    std::int64_t meetingId,
    const std::vector<std::string>& sources,
    int sampleRate,
    std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    StopLocked();
    error.clear();

    if (meetingId <= 0 || sampleRate <= 0 || sources.empty()) {
        error = "无效的会议录音参数";
        return false;
    }

    try {
        const fs::path root =
            fs::path(meetingai::util::utf8ToW(
                meetingai::util::getDataRoot()))
            / "recordings"
            / ("meeting-" + std::to_string(meetingId));
        fs::create_directories(root);

        for (const std::string& source : sources) {
            if (source != "microphone" && source != "system") {
                continue;
            }

            Output output;
            const fs::path path = root / (source + ".wav");
            output.pathUtf8 = PathToUtf8(path);
            output.file.open(
                path,
                std::ios::binary | std::ios::out | std::ios::trunc);
            if (!output.file.is_open()
                || !WriteHeader(output.file, sampleRate, 0)) {
                error = "无法创建会议录音文件: " + output.pathUtf8;
                StopLocked();
                return false;
            }
            outputs_.emplace(source, std::move(output));
        }
    }
    catch (const std::exception& exception) {
        error = std::string("创建会议录音失败: ") + exception.what();
        StopLocked();
        return false;
    }

    if (outputs_.empty()) {
        error = "没有可录制的音频来源";
        return false;
    }

    meetingId_ = meetingId;
    sampleRate_ = sampleRate;
    recording_ = true;
    return true;
}

bool MeetingAudioRecorder::Append(
    const std::string& source,
    const float* samples,
    int sampleCount) {
    if (!samples || sampleCount <= 0) {
        return false;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    if (!recording_) {
        return false;
    }

    const auto found = outputs_.find(source);
    if (found == outputs_.end() || !found->second.file.is_open()) {
        return false;
    }

    Output& output = found->second;
    std::vector<std::int16_t> pcm(
        static_cast<std::size_t>(sampleCount));
    std::transform(
        samples,
        samples + sampleCount,
        pcm.begin(),
        [](float sample) {
            const float bounded = std::clamp(sample, -1.0f, 1.0f);
            const float scaled =
                bounded * static_cast<float>(
                    std::numeric_limits<std::int16_t>::max());
            return static_cast<std::int16_t>(std::lrint(scaled));
        });

    output.file.seekp(0, std::ios::end);
    output.file.write(
        reinterpret_cast<const char*>(pcm.data()),
        static_cast<std::streamsize>(
            pcm.size() * sizeof(std::int16_t)));
    if (!output.file.good()) {
        return false;
    }
    output.sampleCount += pcm.size();

    // 每十秒刷新一次 WAV 长度。即使应用异常退出，最多只丢失文件尾部
    // 的索引长度，已经写入的绝大部分音频仍可被恢复和重处理。
    const std::uint64_t flushInterval =
        static_cast<std::uint64_t>(sampleRate_) * 10;
    if (output.sampleCount - output.samplesAtLastHeaderFlush
        >= flushInterval) {
        if (!WriteHeader(
                output.file,
                sampleRate_,
                output.sampleCount)) {
            return false;
        }
        output.samplesAtLastHeaderFlush = output.sampleCount;
    }
    return true;
}

std::unordered_map<std::string, std::string>
MeetingAudioRecorder::Stop() {
    std::lock_guard<std::mutex> lock(mutex_);
    std::unordered_map<std::string, std::string> paths;
    for (const auto& [source, output] : outputs_) {
        paths.emplace(source, output.pathUtf8);
    }
    StopLocked();
    return paths;
}

bool MeetingAudioRecorder::IsRecording() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return recording_;
}

std::unordered_map<std::string, std::string>
MeetingAudioRecorder::Paths() const {
    std::lock_guard<std::mutex> lock(mutex_);
    std::unordered_map<std::string, std::string> paths;
    for (const auto& [source, output] : outputs_) {
        paths.emplace(source, output.pathUtf8);
    }
    return paths;
}

bool MeetingAudioRecorder::WriteHeader(
    std::ofstream& file,
    int sampleRate,
    std::uint64_t sampleCount) {
    if (!file.is_open()) {
        return false;
    }

    const std::uint64_t bytes64 =
        sampleCount * sizeof(std::int16_t);
    const std::uint32_t dataBytes = static_cast<std::uint32_t>(
        std::min<std::uint64_t>(
            bytes64,
            std::numeric_limits<std::uint32_t>::max() - 36));
    const std::uint32_t riffBytes = 36 + dataBytes;
    const std::uint16_t format = 1;
    const std::uint16_t channels = 1;
    const std::uint16_t bitsPerSample = 16;
    const std::uint32_t byteRate =
        static_cast<std::uint32_t>(
            sampleRate * channels * bitsPerSample / 8);
    const std::uint16_t blockAlign =
        static_cast<std::uint16_t>(
            channels * bitsPerSample / 8);
    const std::uint32_t fmtBytes = 16;

    const std::streampos end = file.tellp();
    file.seekp(0, std::ios::beg);
    file.write("RIFF", 4);
    WriteValue(file, riffBytes);
    file.write("WAVE", 4);
    file.write("fmt ", 4);
    WriteValue(file, fmtBytes);
    WriteValue(file, format);
    WriteValue(file, channels);
    WriteValue(file, static_cast<std::uint32_t>(sampleRate));
    WriteValue(file, byteRate);
    WriteValue(file, blockAlign);
    WriteValue(file, bitsPerSample);
    file.write("data", 4);
    WriteValue(file, dataBytes);
    file.seekp(end < std::streampos(44) ? std::streampos(44) : end);
    file.flush();
    return file.good();
}

void MeetingAudioRecorder::StopLocked() {
    for (auto& [source, output] : outputs_) {
        (void)source;
        if (output.file.is_open()) {
            WriteHeader(
                output.file,
                sampleRate_,
                output.sampleCount);
            output.file.close();
        }
    }
    outputs_.clear();
    meetingId_ = 0;
    recording_ = false;
}

} // namespace meetingai::audio
