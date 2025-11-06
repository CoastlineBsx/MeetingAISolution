using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

/// <summary>
/// RAG Pipeline 适配器 - 通过现有管道获取 Embedding
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 通过现有管道获取 Embedding（复用 _pipe）
    /// 串行方式：同一时间只处理一个 Embedding 请求
    /// </summary>
    public async Task<float[]> GetEmbeddingViaPipeAsync(string text, CancellationToken ct = default)
    {
        // 获取锁，确保串行处理
        lock (_embeddingLock)
        {
            if (_embeddingTcs != null && !_embeddingTcs.Task.IsCompleted)
            {
                throw new InvalidOperationException("上一个 Embedding 请求尚未完成，请稍后再试");
            }

            _embeddingTcs = new TaskCompletionSource<float[]>();
        }

        try
        {
            // 确保管道已连接
            await EnsurePipeAsync();

            // 构建并发送命令
            var cmd = new EmbeddingEncodeCommand { prompt = text };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.EmbeddingEncodeCommand) + "\n";
            await SendJsonAsync(json);

            // 等待响应（会在 MainWindow.Pipe.cs 的读循环中设置结果）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30秒超时

            var resultTask = _embeddingTcs.Task;
            var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);

            var completedTask = await Task.WhenAny(resultTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                throw new TimeoutException("Embedding 请求超时（30秒）");
            }

            return await resultTask;
        }
        catch (Exception)
        {
            // 清理 TCS
            lock (_embeddingLock)
            {
                _embeddingTcs?.TrySetCanceled();
                _embeddingTcs = null;
            }
            throw;
        }
    }

    /// <summary>
    /// 内部方法：设置 Embedding 结果（从 Pipe 读循环调用）
    /// </summary>
    internal void SetEmbeddingResult(float[] embedding)
    {
        lock (_embeddingLock)
        {
            _embeddingTcs?.TrySetResult(embedding);
            _embeddingTcs = null;
        }
    }

    /// <summary>
    /// 内部方法：设置 Embedding 错误（从 Pipe 读循环调用）
    /// </summary>
    internal void SetEmbeddingError(Exception ex)
    {
        lock (_embeddingLock)
        {
            _embeddingTcs?.TrySetException(ex);
            _embeddingTcs = null;
        }
    }
}
