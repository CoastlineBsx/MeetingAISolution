namespace MeetingAI.Host.Contracts.Messages;

/// <summary>
/// 发送音频流数据到 Worker 进行实时转录
/// </summary>
public class StreamingAudioCommand
{
    public string type { get; set; } = "streaming_audio";

    /// <summary>
    /// 音频来源：microphone（我方）或 system（对方/会议音频）
    /// </summary>
    public string source { get; set; } = "microphone";

    /// <summary>
    /// Base64 编码的音频数据（16kHz, 16-bit, mono PCM）
    /// </summary>
    public string audio_data { get; set; } = "";

    /// <summary>
    /// 采样率 (必须是 16000)
    /// </summary>
    public int sample_rate { get; set; } = 16000;

    /// <summary>
    /// 是否是音频流的结束标记
    /// </summary>
    public bool is_end { get; set; } = false;
}

/// <summary>
/// 开始流式转录会话
/// </summary>
public class StartStreamingCommand
{
    public string type { get; set; } = "start_streaming";
    public int sample_rate { get; set; } = 16000;
    /// <summary>
    /// microphone、system 或 both
    /// </summary>
    public string source { get; set; } = "microphone";

    /// <summary>
    /// off、auto（中英自动互译）、to_zh（仅译为中文）或 to_en（仅译为英文）
    /// </summary>
    public string translation_mode { get; set; } = "off";

    /// <summary>
    /// 是否启用 Granite 本地滚动会议摘要。
    /// </summary>
    public bool summary_enabled { get; set; } = true;
}

/// <summary>
/// 停止流式转录会话
/// </summary>
public class StopStreamingCommand
{
    public string type { get; set; } = "stop_streaming";
}
