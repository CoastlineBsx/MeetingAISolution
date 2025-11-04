using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// Granite NPU 文本生成服务（通过 C++ Worker）
/// </summary>
public class GraniteNPUService
{
    private readonly WorkerPipeClient _workerClient;

    public GraniteNPUService(WorkerPipeClient workerClient)
    {
        _workerClient = workerClient;
    }

    /// <summary>
    /// 生成文本
    /// </summary>
    public async Task<string> GenerateAsync(
        string prompt,
        int maxTokens = 128,
        float temperature = 0.7f,
        CancellationToken cancellationToken = default)
    {
        var command = JsonSerializer.Serialize(new
        {
            type = "granite_generate",
            prompt,
            max_tokens = maxTokens,
            temperature
        });

        var response = await _workerClient.SendCommandAsync(command, cancellationToken);
        
        var result = JsonSerializer.Deserialize<GenerateResponse>(response);
        return result?.Text ?? string.Empty;
    }

    /// <summary>
    /// 流式生成文本
    /// </summary>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        int maxTokens = 128,
        float temperature = 0.7f,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var command = JsonSerializer.Serialize(new
        {
            type = "granite_generate_stream",
            prompt,
            max_tokens = maxTokens,
            temperature
        });

        await _workerClient.SendCommandAsync(command, cancellationToken);

        // 读取流式响应
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _workerClient.SendCommandAsync("{\"type\":\"read_stream\"}", cancellationToken);
            
            if (string.IsNullOrEmpty(response))
                break;

            var chunk = JsonSerializer.Deserialize<StreamChunk>(response);
            
            if (chunk?.Type == "token" && !string.IsNullOrEmpty(chunk.Text))
            {
                yield return chunk.Text;
            }
            else if (chunk?.Type == "done")
            {
                break;
            }
        }
    }

    private class GenerateResponse
    {
        public string? Text { get; set; }
    }

    private class StreamChunk
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }
}
