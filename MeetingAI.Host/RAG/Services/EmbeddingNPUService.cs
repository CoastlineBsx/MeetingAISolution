using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// Embedding NPU 服务（通过 C++ Worker）
/// </summary>
public class EmbeddingNPUService
{
    private readonly WorkerPipeClient _workerClient;

    public EmbeddingNPUService(WorkerPipeClient workerClient)
    {
        _workerClient = workerClient;
    }

    /// <summary>
    /// 生成文本的 Embedding 向量
    /// </summary>
    public async Task<float[]> GetEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var command = JsonSerializer.Serialize(new
        {
            type = "get_embedding",
            text
        });

        var response = await _workerClient.SendCommandAsync(command, cancellationToken);
        
        var result = JsonSerializer.Deserialize<EmbeddingResponse>(response);
        return result?.Embedding ?? Array.Empty<float>();
    }

    private class EmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}
