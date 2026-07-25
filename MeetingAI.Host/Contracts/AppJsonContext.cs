using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host.Contracts;

// System.Text.Json 的 Source Generator 上下文
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(TranscribeFileCommand))]
[JsonSerializable(typeof(TranscribeOpenVINOCommand))]
[JsonSerializable(typeof(QuitMessage))]
[JsonSerializable(typeof(GraniteGenerateStreamCommand))]
[JsonSerializable(typeof(GraniteChatStreamCommand))]
[JsonSerializable(typeof(GraniteStartChatCommand))]
[JsonSerializable(typeof(GraniteFinishChatCommand))]
[JsonSerializable(typeof(EmbeddingEncodeCommand))]
[JsonSerializable(typeof(EmbeddingResult))]
[JsonSerializable(typeof(EmbeddingReadyMessage))]
[JsonSerializable(typeof(TestSimilarityCommand))]
[JsonSerializable(typeof(SimilarityTestResult))]
[JsonSerializable(typeof(SimilarityPair))]
[JsonSerializable(typeof(StreamingAudioCommand))]
[JsonSerializable(typeof(StartStreamingCommand))]
[JsonSerializable(typeof(StopStreamingCommand))]
internal partial class AppJsonContext : JsonSerializerContext
{
    // 延迟初始化：创建一个允许直接输出中文字符（UTF-8）的上下文
    private static readonly Lazy<AppJsonContext> s_utf8Context = new(() =>
    {
        var options = new JsonSerializerOptions(Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return new AppJsonContext(options);
    });

    // 提供一个使用 UTF-8 编码器的默认实例
    public static AppJsonContext Utf8 => s_utf8Context.Value;
}
