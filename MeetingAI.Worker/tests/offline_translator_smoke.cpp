#include "translation/offline_translator.hpp"

#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    if (argc != 3) {
        std::cerr << "usage: offline_translator_smoke <en-zh-model> <zh-en-model>\n";
        return 2;
    }

    std::mutex mutex;
    std::condition_variable changed;
    std::vector<meetingai::translation::TranslationEvent> results;
    meetingai::translation::OfflineTranslator translator;

    if (!translator.Start(
            "auto",
            argv[1],
            argv[2],
            [&](const meetingai::translation::TranslationEvent& event) {
                std::lock_guard<std::mutex> lock(mutex);
                results.push_back(event);
                changed.notify_all();
            })) {
        std::cerr << translator.GetLastError() << '\n';
        return 3;
    }

    translator.Submit(
        "microphone",
        1,
        "Welcome to the fully offline meeting assistant.",
        true);
    translator.Submit(
        "system",
        1,
        "实时翻译不会把会议音频发送到云端。",
        true);

    {
        std::unique_lock<std::mutex> lock(mutex);
        if (!changed.wait_for(
                lock,
                std::chrono::seconds(60),
                [&] { return results.size() >= 2; })) {
            std::cerr << "translation timed out\n";
            translator.Stop(false);
            return 4;
        }
    }

    translator.Stop(true);
    for (const auto& result : results) {
        std::cout
            << result.source << '\t'
            << result.targetLanguage << '\t'
            << result.text << '\n';
    }
    return 0;
}
