namespace MeetingAI.Host.Contracts.Messages;

/// <summary>
/// 发送音频流数据到 Worker 进行实时转录
/// </summary>
public class StreamingAudioCommand
{
    public string type { get; set; } = "streaming_audio";

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
}

/// <summary>
/// 停止流式转录会话
/// </summary>
public class StopStreamingCommand
{
    public string type { get; set; } = "stop_streaming";
}
