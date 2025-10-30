#pragma once
#include <string>
#include <vector>

// Whisper 转录结果的结构体
struct WhisperSegment {
    std::string text;
    double start_time;  // 秒
    double end_time;    // 秒

    // ★ Whisper 后处理质量指标（工业界标准命名）
    float no_speech_probability = 0.0f;     // 无语音概率（0.0-1.0），>0.9 可能无语音
    float average_log_probability = 0.0f;   // 平均对数概率，<-1.8 表示模型不确定
    float text_compression_ratio = 0.0f;    // 文本压缩率，>5.5 表示重复/幻觉
};

// 转录文件的主函数（内部会确保模型只加载一次）
bool TranscribeAudioFile(
    const std::string& modelPath,
    const std::string& audioPath,
    std::vector<WhisperSegment>& segments,
    const std::string& sceneMode = "auto",  // auto/speech/music/mixed
    const std::string& language = "auto"    // auto/zh/en/ja/ko/es/fr/de...
);

// 初始化 Whisper（显式加载；通常不直接用）
bool InitWhisper(const std::string& modelPath);

// 清理 Whisper 资源（进程结束时可调用）
void CleanupWhisper();

// ★ 新增：仅加载一次（多次调用也只会首次真正加载）
bool InitWhisperOnce(const std::string& modelPath);
