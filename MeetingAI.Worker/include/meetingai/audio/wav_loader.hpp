#pragma once

#include <string>
#include <vector>

namespace meetingai::audio {

// Loads a PCM16 or IEEE-float WAV file, mixes all channels to mono, and
// resamples it to the 16 kHz floating-point format expected by Whisper.
bool LoadWavFile16KhzMono(
    const std::string& filename,
    std::vector<float>& audioData);

} // namespace meetingai::audio
