namespace MeetingAI.Host.Contracts.Messages;

public sealed class TranscribeOpenVINOCommand
{
    public string type { get; set; } = "transcribe_openvino";
    public string path { get; set; } = "";
    public string language { get; set; } = "auto";  // auto/zh/en/ja/ko/es/fr/de...
}
