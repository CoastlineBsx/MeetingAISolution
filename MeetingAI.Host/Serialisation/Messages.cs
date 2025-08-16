// namespace 建议用 MeetingAI.Host；这样 MainWindow 不需要额外 using
namespace MeetingAI.Host;

public sealed class PingMessage
{
    public string type { get; set; } = "ping";
    public string payload { get; set; } = "";
}

public sealed class TranscribeFileCommand
{
    public string type { get; set; } = "transcribe_file";
    public string path { get; set; } = "";
}

public sealed class QuitMessage
{
    public string type { get; set; } = "quit";
}
