#include "command_parser.h"
#include "sherpa_streaming_transcriber.h"

#include <iostream>
#include <string>
#include <vector>

int main(int argc, char** argv)
{
    if (argc != 4) {
        std::cerr
            << "usage: sherpa_dual_stream_smoke_test MODEL_DIR TOKENS BPE_VOCAB\n";
        return 2;
    }

    meetingai::proto::MeetingContextCommand context;
    context.preparationId = 1;
    context.hotwords = {
        { "Application: useful", 1.0f },
        { "COMP0197: Applied Deep Learning", 1.25f },
        { "OpenVINO", 3.5f }
    };
    const std::string hotwords =
        meetingai::proto::buildSherpaHotwordsBuffer(context);

    meetingai::transcribe::SherpaStreamingTranscriber transcriber;
    if (!transcriber.Initialize(
            argv[1],
            argv[2],
            16000,
            hotwords,
            argv[3])) {
        std::cerr << transcriber.GetLastError() << '\n';
        return 1;
    }
    if (!transcriber.StartSession("microphone", hotwords) ||
        !transcriber.StartSession("system", hotwords)) {
        std::cerr << transcriber.GetLastError() << '\n';
        return 1;
    }

    std::vector<float> silence(1600, 0.0f);
    std::vector<meetingai::transcribe::SherpaStreamResult> results;
    if (!transcriber.AcceptWaveform(
            "microphone",
            silence.data(),
            static_cast<int>(silence.size()),
            results) ||
        !transcriber.AcceptWaveform(
            "system",
            silence.data(),
            static_cast<int>(silence.size()),
            results)) {
        std::cerr << transcriber.GetLastError() << '\n';
        return 1;
    }

    if (!transcriber.EndSession("microphone", results) ||
        !transcriber.EndSession("system", results) ||
        transcriber.IsRunning()) {
        std::cerr << transcriber.GetLastError() << '\n';
        return 1;
    }

    std::cout << "Sherpa dual-stream hotword smoke test passed\n";
    return 0;
}
