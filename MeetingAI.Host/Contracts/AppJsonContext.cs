using System.Text.Json.Serialization;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host.Contracts;

// System.Text.Json 的 Source Generator 上下文
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(TranscribeFileCommand))]
[JsonSerializable(typeof(QuitMessage))]
[JsonSerializable(typeof(StartStreamCommand))]
[JsonSerializable(typeof(StreamChunkCommand))]
[JsonSerializable(typeof(StopStreamCommand))]
internal partial class AppJsonContext : JsonSerializerContext { }
