#pragma once

#include <string>

namespace meetingai::transcribe {

struct StableTranscriptPrefix {
    std::string finalizedRawText;
    std::string finalizedText;
    std::string remainingRawText;
};

/// <summary>
/// Normalizes a UTF-8 streaming transcript after punctuation restoration.
/// Chinese-dominant sentences keep full-width punctuation. English-dominant
/// sentences use ASCII punctuation and sentence casing.
/// </summary>
std::string NormalizeBilingualTranscript(const std::string& text);

/// <summary>
/// Joins two raw ASR fragments without inserting spaces between CJK text.
/// </summary>
std::string JoinTranscriptFragments(
    const std::string& left,
    const std::string& right);

/// <summary>
/// Uses a punctuated version of rawText as one-fragment lookahead. A sentence
/// is returned only when there is enough following speech to make its terminal
/// punctuation stable; the uncertain raw suffix is retained for the next pass.
/// </summary>
bool TryExtractStableTranscriptPrefix(
    const std::string& rawText,
    const std::string& punctuatedText,
    StableTranscriptPrefix& result);

} // namespace meetingai::transcribe
