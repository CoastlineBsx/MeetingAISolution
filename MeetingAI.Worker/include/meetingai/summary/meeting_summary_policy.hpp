#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace meetingai::summary {

// 低于这个有效字节数时，强制模型填充完整会议模板很容易诱发幻觉。
inline constexpr std::size_t kMinimumGroundedTranscriptBytes = 300;

struct SummaryValidationResult {
    bool accepted = false;
    std::string reason;
};

struct SummaryEvidence {
    std::int64_t segmentId = 0;
    std::string text;
    // 为空时显示为 [S<segmentId>]；实时 partial 可使用“实时·我方”等标签。
    std::string citationLabel;
};

std::size_t CountMeaningfulTranscriptBytes(const std::string& transcript);

std::string BuildGroundedSummaryPrompt(
    const std::string& transcript,
    bool isFinal,
    bool isRetry);

std::string BuildGroundedSummaryJsonSchema(
    const std::vector<std::int64_t>& allowedSegmentIds);

SummaryValidationResult FormatGroundedSummaryJson(
    const std::string& jsonOutput,
    const std::vector<SummaryEvidence>& evidence,
    std::string& formattedSummary);

} // namespace meetingai::summary
