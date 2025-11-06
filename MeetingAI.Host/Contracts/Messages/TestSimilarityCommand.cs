using System;

namespace MeetingAI.Host.Contracts.Messages;

/// <summary>
/// 相似度诊断测试命令
/// </summary>
public record TestSimilarityCommand
{
    public string type => "embedding_test_similarity";
}

/// <summary>
/// 相似度测试结果中的单个文本对
/// </summary>
public record SimilarityPair
{
    public string text1 { get; init; } = "";
    public string text2 { get; init; } = "";
    public float similarity { get; init; }
}

/// <summary>
/// 相似度测试结果
/// </summary>
public record SimilarityTestResult
{
    public string type { get; init; } = "similarity_test_result";
    public SimilarityPair[] pairs { get; init; } = Array.Empty<SimilarityPair>();
}
