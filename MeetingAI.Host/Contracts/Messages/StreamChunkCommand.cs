namespace MeetingAI.Host.Contracts.Messages;

public sealed class StreamChunkCommand
{
    public string type { get; set; } = "stream_chunk";
    public string data { get; set; } = "";           // Base64 encoded PCM float32 data
    public int sample_rate { get; set; } = 16000;    // Sample rate (always 16000 for Whisper)
}
