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

// ==================== 流式转录接口 ====================

// 开始流式转录（创建并持有全局 stream_state）
// 返回值：true=成功，false=失败（例如模型未加载或已在流式中）
bool StartStream(
    const std::string& sceneMode = "speech",  // speech/music/mixed/auto
    const std::string& language = "auto"       // auto/zh/en/ja/ko/es/fr/de...
);

// 处理音频块（追加到滑动窗口缓冲区并转录）
// audioData: Base64 编码的 float32 PCM 数据（16kHz, 单声道）
// segments: 输出本次新识别的段落（只返回新的段落）
// 返回值：true=成功，false=失败
bool ProcessStreamChunk(
    const std::string& audioDataBase64,
    std::vector<WhisperSegment>& segments
);

// 停止流式转录（释放 stream_state 和缓冲区）
void StopStream();
