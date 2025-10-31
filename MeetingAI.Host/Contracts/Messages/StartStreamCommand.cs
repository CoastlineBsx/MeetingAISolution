namespace MeetingAI.Host.Contracts.Messages;

public sealed class StartStreamCommand
{
    public string type { get; set; } = "start_stream";
    public string mode { get; set; } = "speech";      // speech/music/mixed/auto
    public string language { get; set; } = "auto";    // auto/zh/en/ja/ko/es/fr/de...
}
