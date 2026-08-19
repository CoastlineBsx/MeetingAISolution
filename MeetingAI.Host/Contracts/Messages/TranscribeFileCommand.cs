namespace MeetingAI.Host.Contracts.Messages;

public sealed class TranscribeFileCommand
{
    // File transcription is handled exclusively by the OpenVINO pipeline.
    public string type { get; set; } = "transcribe_openvino";
    public string path { get; set; } = "";
    public string mode { get; set; } = "auto";      // auto/speech/music/mixed
    public string language { get; set; } = "auto";  // auto/zh/en/ja/ko/es/fr/de...
}
