#include "database.hpp"
#include "summary/meeting_summary_service.hpp"

#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>

namespace {

std::mutex g_testMutex;
std::vector<MeetingTranscriptEntry> g_databaseEntries;
std::vector<std::string> g_savedSummaries;
std::vector<std::string> g_prompts;

bool WaitFor(
    std::condition_variable& condition,
    std::unique_lock<std::mutex>& lock,
    const std::function<bool()>& predicate) {
    return condition.wait_for(
        lock,
        std::chrono::seconds(5),
        predicate);
}

int Check(bool condition, const std::string& message) {
    if (condition) {
        return 0;
    }
    std::cerr << message << '\n';
    return 1;
}

} // namespace

std::vector<MeetingTranscriptEntry> LoadMeetingTranscriptSince(
    std::int64_t meetingId,
    std::int64_t afterSegmentId) {
    std::lock_guard<std::mutex> lock(g_testMutex);
    if (meetingId != 42) {
        return {};
    }

    std::vector<MeetingTranscriptEntry> result;
    for (const MeetingTranscriptEntry& entry : g_databaseEntries) {
        if (entry.segmentId > afterSegmentId) {
            result.push_back(entry);
        }
    }
    return result;
}

bool InsertMeetingSummary(
    std::int64_t meetingId,
    std::int64_t,
    const std::string&,
    const std::string&,
    const std::string& summaryText,
    bool) {
    std::lock_guard<std::mutex> lock(g_testMutex);
    if (meetingId != 42) {
        return false;
    }
    g_savedSummaries.push_back(summaryText);
    return true;
}

int main() {
    using meetingai::summary::MeetingSummaryService;

    {
        std::lock_guard<std::mutex> lock(g_testMutex);
        g_databaseEntries = {
            {12, "system", std::string(320, 'a')},
        };
    }

    std::mutex eventMutex;
    std::condition_variable eventCondition;
    int finalEventCount = 0;
    bool ready = false;

    MeetingSummaryService service;
    service.Start(
        42,
        [](std::string&) { return true; },
        [](const std::string& prompt,
           const std::string& jsonSchema,
           const MeetingSummaryService::PartialCallback&) {
            {
                std::lock_guard<std::mutex> lock(g_testMutex);
                g_prompts.push_back(prompt);
            }
            if (jsonSchema.find("1000000001") == std::string::npos) {
                return std::string{};
            }
            return std::string(
                R"({"overview":[{"text":"摘要包含当前实时字幕。","segment_id":1000000001}],)"
                R"("key_points":[],"decisions":[],"action_items":[],)"
                R"("open_questions":[],"risks_disagreements":[]})");
        },
        [&](const std::string& state,
            const std::string&,
            bool,
            std::int64_t) {
            std::lock_guard<std::mutex> lock(eventMutex);
            if (state == "ready") {
                ready = true;
            }
            if (state == "final") {
                ++finalEventCount;
            }
            eventCondition.notify_all();
        });

    int failures = 0;
    {
        std::unique_lock<std::mutex> lock(eventMutex);
        failures += Check(
            WaitFor(eventCondition, lock, [&] { return ready; }),
            "summary service did not become ready");
    }

    const std::string firstLive =
        "first-live-snapshot: the current sentence is not final yet";
    service.UpdateLiveTranscript("microphone", 1, firstLive);
    service.RequestNow();
    {
        std::unique_lock<std::mutex> lock(eventMutex);
        failures += Check(
            WaitFor(eventCondition, lock, [&] { return finalEventCount >= 1; }),
            "first immediate summary was not generated");
    }

    const std::string secondLive =
        "second-live-snapshot: more words have arrived before finalization";
    service.UpdateLiveTranscript("microphone", 1, secondLive);
    service.RequestNow();
    {
        std::unique_lock<std::mutex> lock(eventMutex);
        failures += Check(
            WaitFor(eventCondition, lock, [&] { return finalEventCount >= 2; }),
            "second immediate summary was not generated");
    }
    service.Stop(false);

    {
        std::lock_guard<std::mutex> lock(g_testMutex);
        failures += Check(
            g_prompts.size() == 2,
            "each click must create exactly one current snapshot");
        failures += Check(
            g_prompts.size() >= 2 &&
                g_prompts[0].find(firstLive) != std::string::npos &&
                g_prompts[1].find(secondLive) != std::string::npos &&
                g_prompts[1].find(firstLive) == std::string::npos,
            "later click must use the latest partial instead of accumulating old partials");
        failures += Check(
            g_savedSummaries.size() == 2 &&
                g_savedSummaries.back().find("[实时·我方]") !=
                    std::string::npos,
            "live snapshot summary must be saved with a readable citation");
    }

    if (failures != 0) {
        std::cerr << failures
                  << " meeting summary live snapshot test(s) failed\n";
        return 1;
    }
    std::cout << "All meeting summary live snapshot tests passed\n";
    return 0;
}
