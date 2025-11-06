namespace MeetingAI.Host.Contracts.Messages;

/// <summary>
/// Embedding 编码命令
/// </summary>
public record EmbeddingEncodeCommand
{
    public string type => "embedding_encode";
    public required string prompt { get; init; }
}

/// <summary>
/// Embedding 编码结果
/// </summary>
public record EmbeddingResult
{
    public string type { get; init; } = "embedding_result";
    public required float[] embedding { get; init; }
}

/// <summary>
/// Embedding 模型就绪通知
/// </summary>
public record EmbeddingReadyMessage
{
    public string type { get; init; } = "embedding_ready";
    public string device { get; init; } = "";
    public int dim { get; init; }
}
