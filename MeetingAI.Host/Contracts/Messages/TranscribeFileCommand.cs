namespace MeetingAI.Host.Contracts.Messages;

public sealed class TranscribeFileCommand
{
    public string type { get; set; } = "transcribe_file";
    public string path { get; set; } = "";
}
