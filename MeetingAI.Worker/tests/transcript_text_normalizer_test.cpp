#include "transcript_text_normalizer.hpp"

#include <iostream>
#include <string>

namespace {

int Check(const std::string& input, const std::string& expected)
{
    const std::string actual =
        meetingai::transcribe::NormalizeBilingualTranscript(input);
    if (actual == expected) {
        return 0;
    }

    std::cerr << "input:    " << input << '\n'
              << "expected: " << expected << '\n'
              << "actual:   " << actual << '\n';
    return 1;
}

int CheckJoin(
    const std::string& left,
    const std::string& right,
    const std::string& expected)
{
    const std::string actual =
        meetingai::transcribe::JoinTranscriptFragments(left, right);
    if (actual == expected) {
        return 0;
    }

    std::cerr << "join expected: " << expected << '\n'
              << "join actual:   " << actual << '\n';
    return 1;
}

int CheckStablePrefix(
    const std::string& raw,
    const std::string& punctuated,
    const std::string& expectedFinal,
    const std::string& expectedRemaining)
{
    meetingai::transcribe::StableTranscriptPrefix result;
    if (!meetingai::transcribe::TryExtractStableTranscriptPrefix(
        raw,
        punctuated,
        result)) {
        std::cerr << "stable prefix was not found: " << punctuated << '\n';
        return 1;
    }
    if (result.finalizedText == expectedFinal &&
        result.remainingRawText == expectedRemaining) {
        return 0;
    }

    std::cerr << "stable final expected: " << expectedFinal << '\n'
              << "stable final actual:   " << result.finalizedText << '\n'
              << "remaining expected:    " << expectedRemaining << '\n'
              << "remaining actual:      " << result.remainingRawText << '\n';
    return 1;
}

} // namespace

int main()
{
    int failures = 0;
    failures += Check(
        "ARE THEY THE SAME THING\xEF\xBC\x9F OR ARE THEY DIFFERENT\xE3\x80\x82",
        "Are they the same thing? Or are they different.");
    failures += Check(
        "THIS USES AI\xEF\xBC\x8C GPU\xEF\xBC\x8C AND ONNX\xE3\x80\x82",
        "This uses AI, GPU, and ONNX.");
    failures += Check(
        "I'M USING THE OPENVINO API\xE3\x80\x82",
        "I'm using the OpenVINO API.");
    failures += Check(
        "\xE4\xBB\x8A\xE5\xA4\xA9\xE8\xAE\xA8\xE8\xAE\xBA AI\xEF\xBC\x8C"
        "\xE6\x95\x88\xE6\x9E\x9C\xE5\xBE\x88\xE5\xA5\xBD\xE3\x80\x82",
        "\xE4\xBB\x8A\xE5\xA4\xA9\xE8\xAE\xA8\xE8\xAE\xBA AI\xEF\xBC\x8C"
        "\xE6\x95\x88\xE6\x9E\x9C\xE5\xBE\x88\xE5\xA5\xBD\xE3\x80\x82");
    failures += Check(
        "OpenAI makes ChatGPT\xE3\x80\x82",
        "OpenAI makes ChatGPT.");
    failures += Check("ARE", "Are");
    failures += Check("GPU", "GPU");
    failures += Check(
        "\xE4\xBB\x8A\xE5\xA4\xA9\xE8\xAE\xA8\xE8\xAE\xBA DEEP LEARNING "
        "\xE6\xA8\xA1\xE5\x9E\x8B",
        "\xE4\xBB\x8A\xE5\xA4\xA9\xE8\xAE\xA8\xE8\xAE\xBA deep learning "
        "\xE6\xA8\xA1\xE5\x9E\x8B");
    failures += CheckJoin("THIS IS", "A TEST", "THIS IS A TEST");
    failures += CheckJoin(
        "\xE8\xBF\x99\xE6\x98\xAF",
        "\xE6\xB5\x8B\xE8\xAF\x95",
        "\xE8\xBF\x99\xE6\x98\xAF\xE6\xB5\x8B\xE8\xAF\x95");
    failures += CheckStablePrefix(
        "I WANT TO EXPLAIN HOW THIS WORKS NOW LETS CONTINUE",
        "I want to explain how this works. Now lets continue.",
        "I want to explain how this works.",
        "NOW LETS CONTINUE");
    failures += CheckStablePrefix(
        "\xE8\xBF\x99\xE6\x98\xAF\xE7\xAC\xAC\xE4\xB8\x80\xE5\x8F\xA5"
        "\xE8\xAF\x9D\xE7\x8E\xB0\xE5\x9C\xA8\xE5\xBC\x80\xE5\xA7\x8B"
        "\xE7\xAC\xAC\xE4\xBA\x8C\xE5\x8F\xA5\xE8\xAF\x9D",
        "\xE8\xBF\x99\xE6\x98\xAF\xE7\xAC\xAC\xE4\xB8\x80\xE5\x8F\xA5"
        "\xE8\xAF\x9D\xE3\x80\x82\xE7\x8E\xB0\xE5\x9C\xA8\xE5\xBC\x80"
        "\xE5\xA7\x8B\xE7\xAC\xAC\xE4\xBA\x8C\xE5\x8F\xA5\xE8\xAF\x9D"
        "\xE3\x80\x82",
        "\xE8\xBF\x99\xE6\x98\xAF\xE7\xAC\xAC\xE4\xB8\x80\xE5\x8F\xA5"
        "\xE8\xAF\x9D\xE3\x80\x82",
        "\xE7\x8E\xB0\xE5\x9C\xA8\xE5\xBC\x80\xE5\xA7\x8B\xE7\xAC\xAC"
        "\xE4\xBA\x8C\xE5\x8F\xA5\xE8\xAF\x9D");

    if (failures != 0) {
        std::cerr << failures << " normalization test(s) failed\n";
        return 1;
    }

    std::cout << "All transcript normalization tests passed\n";
    return 0;
}
