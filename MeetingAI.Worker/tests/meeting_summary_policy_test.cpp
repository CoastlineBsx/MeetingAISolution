#include "summary/meeting_summary_policy.hpp"

#include <iostream>
#include <string>
#include <vector>

namespace {

int Check(bool condition, const std::string& message) {
    if (condition) {
        return 0;
    }
    std::cerr << message << '\n';
    return 1;
}

} // namespace

int main() {
    using meetingai::summary::CountMeaningfulTranscriptBytes;
    using meetingai::summary::FormatGroundedSummaryJson;
    using meetingai::summary::SummaryEvidence;

    int failures = 0;
    failures += Check(
        CountMeaningfulTranscriptBytes("hello") <
            meetingai::summary::kMinimumGroundedTranscriptBytes,
        "short transcript must be rejected");
    failures += Check(
        CountMeaningfulTranscriptBytes(std::string(400, 'a')) >=
            meetingai::summary::kMinimumGroundedTranscriptBytes,
        "substantive transcript must be accepted");

    const std::vector<SummaryEvidence> evidence = {
        {12, "Foundation models include large language models and AI."},
        {13, "Some people argue that generative AI recombines information."},
    };
    const std::string validJson =
        R"({"content_type":"lecture",)"
        R"("overview":[{"text":"内容讨论基础模型和 AI。","segment_id":12}],)"
        R"("key_points":[{"text":"基础模型用于语言建模。","segment_id":12}],)"
        R"("decisions":[],"action_items":[],"open_questions":[],)"
        R"("risks_disagreements":[{"text":"有人对生成式 AI 是否创造新内容存在争议。","segment_id":13}]})";
    std::string formatted;
    failures += Check(
        FormatGroundedSummaryJson(
            validJson,
            evidence,
            true,
            formatted).accepted,
        "grounded JSON summary should pass");
    failures += Check(
        formatted.find("[S12]") != std::string::npos &&
            formatted.find("[S13]") != std::string::npos &&
            formatted.find("【内容类型】\n- 课程或演讲") !=
                std::string::npos &&
            formatted.find("【行动项】") == std::string::npos,
        "detailed summary must adapt to content type and hide empty sections");

    const std::string englishOverviewJson =
        R"({"content_type":"lecture",)"
        R"("overview":[{"text":"Foundation models include large language models.","segment_id":12}],)"
        R"("key_points":[{"text":"基础模型包括大型语言模型。","segment_id":12}],)"
        R"("decisions":[],"action_items":[],"open_questions":[],)"
        R"("risks_disagreements":[]})";
    failures += Check(
        FormatGroundedSummaryJson(
            englishOverviewJson,
            evidence,
            false,
            formatted).accepted &&
            formatted.find("本次内容主要涉及：基础模型包括大型语言模型。") !=
                std::string::npos,
        "English overview must fall back to grounded Chinese key points");

    const std::string unknownCitation =
        R"({"content_type":"other",)"
        R"("overview":[{"text":"内容讨论基础模型。","segment_id":999}],)"
        R"("key_points":[],"decisions":[],"action_items":[],)"
        R"("open_questions":[],"risks_disagreements":[]})";
    failures += Check(
        !FormatGroundedSummaryJson(
            unknownCitation,
            evidence,
            false,
            formatted).accepted,
        "unknown evidence id must be rejected");

    const std::string inventedNumber =
        R"({"content_type":"business_meeting",)"
        R"("overview":[{"text":"市场价值为500万美元。","segment_id":12}],)"
        R"("key_points":[],"decisions":[],"action_items":[],)"
        R"("open_questions":[],"risks_disagreements":[]})";
    failures += Check(
        !FormatGroundedSummaryJson(
            inventedNumber,
            evidence,
            false,
            formatted).accepted,
        "unsupported numbers must not become grounded facts");

    const std::vector<SummaryEvidence> liveEvidence = {
        {1000000001,
         "We are currently discussing a local real-time meeting summary.",
         "实时·我方"},
    };
    const std::string liveJson =
        R"({"content_type":"discussion",)"
        R"("overview":[{"text":"当前正在讨论本地实时会议摘要。","segment_id":1000000001}],)"
        R"("key_points":[{"text":"当前正在讨论本地实时会议摘要。","segment_id":1000000001}],)"
        R"("decisions":[],"action_items":[],"open_questions":[],)"
        R"("risks_disagreements":[]})";
    failures += Check(
        FormatGroundedSummaryJson(
            liveJson,
            liveEvidence,
            false,
            formatted).accepted &&
            formatted.find("[实时·我方]") != std::string::npos &&
            formatted.find("[S1000000001]") == std::string::npos,
        "live partial evidence must use a readable live citation");

    const std::string overflowOverviewJson =
        R"({"content_type":"discussion","overview":[)"
        R"({"text":"第一项。","segment_id":1000000001},)"
        R"({"text":"第二项。","segment_id":1000000001}],)"
        R"("key_points":[],"decisions":[],"action_items":[],)"
        R"("open_questions":[],"risks_disagreements":[]})";
    failures += Check(
        FormatGroundedSummaryJson(
            overflowOverviewJson,
            liveEvidence,
            false,
            formatted).accepted &&
            formatted.find("【当前要点】\n- 第二项。 [实时·我方]") !=
                std::string::npos,
        "overflow overview facts must be preserved as key points");

    if (failures != 0) {
        std::cerr << failures << " meeting summary policy test(s) failed\n";
        return 1;
    }
    std::cout << "All meeting summary policy tests passed\n";
    return 0;
}
