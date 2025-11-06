using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// Embedding 服务（通过委托注入，解耦具体实现）
/// </summary>
public class EmbeddingNPUService
{
    private readonly Func<string, CancellationToken, Task<float[]>> _getEmbedding;

    /// <summary>
    /// 构造函数 - 注入 Embedding 获取委托
    /// </summary>
    /// <param name="getEmbedding">获取 Embedding 的委托函数</param>
    public EmbeddingNPUService(Func<string, CancellationToken, Task<float[]>> getEmbedding)
    {
        _getEmbedding = getEmbedding ?? throw new ArgumentNullException(nameof(getEmbedding));
    }

    /// <summary>
    /// 生成文本的 Embedding 向量
    /// </summary>
    public async Task<float[]> GetEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        return await _getEmbedding(text, cancellationToken);
    }
}
