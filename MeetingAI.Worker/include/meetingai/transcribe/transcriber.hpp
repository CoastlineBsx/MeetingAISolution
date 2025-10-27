#pragma once
#include <string>
#include <vector>

// Whisper 转录结果的结构体
struct WhisperSegment {
    std::string text;
    double start_time;  // 秒
    double end_time;    // 秒
};

// 转录文件的主函数（内部会确保模型只加载一次）
bool TranscribeAudioFile(
    const std::string& modelPath,
    const std::string& audioPath,
    std::vector<WhisperSegment>& segments
);

// 初始化 Whisper（显式加载；通常不直接用）
bool InitWhisper(const std::string& modelPath);

// 清理 Whisper 资源（进程结束时可调用）
void CleanupWhisper();

// ★ 新增：仅加载一次（多次调用也只会首次真正加载）
bool InitWhisperOnce(const std::string& modelPath);
