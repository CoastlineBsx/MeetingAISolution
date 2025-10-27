namespace MeetingAI.Host.Contracts.Messages;

public sealed class PingMessage
{
    public string type { get; set; } = "ping";
    public string payload { get; set; } = "";
}
