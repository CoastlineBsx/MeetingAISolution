namespace MeetingAI.Host.Contracts.Messages;

public sealed class StopStreamCommand
{
    public string type { get; set; } = "stop_stream";
}
