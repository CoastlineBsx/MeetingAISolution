namespace MeetingAI.Host.Contracts.Messages;

/// <summary>
/// Granite 单轮生成命令（流式）
/// </summary>
public record GraniteGenerateStreamCommand
{
    public string type => "granite_generate_stream";
    public required string prompt { get; init; }
    public int max_tokens { get; init; } = 256;
    public float temperature { get; init; } = 0.7f;
}

/// <summary>
/// Granite 多轮对话命令（流式）
/// </summary>
public record GraniteChatStreamCommand
{
    public string type => "granite_chat_stream";
    public required string prompt { get; init; }
    public int max_tokens { get; init; } = 256;
    public float temperature { get; init; } = 0.7f;
}

/// <summary>
/// 开始多轮会话
/// </summary>
public record GraniteStartChatCommand
{
    public string type => "granite_start_chat";
    public string system_message { get; init; } = "你是一个专业、简洁的中文助手。";
}

/// <summary>
/// 结束多轮会话
/// </summary>
public record GraniteFinishChatCommand
{
    public string type => "granite_finish_chat";
}
