using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

/// <summary>
/// Embedding 测试功能
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 测试 Embedding 功能
    /// </summary>
    private async void BtnTestEmbedding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppendLineAsync("[测试] 开始测试 Embedding Pipeline 适配器...");

            // 使用 Pipeline 适配器获取向量
            var embedding = await GetEmbeddingViaPipeAsync("这是一段测试文本");

            await AppendLineAsync($"[测试] ✅ 成功获取向量！");
            await AppendLineAsync($"[测试] 向量维度: {embedding.Length}");
            await AppendLineAsync($"[测试] 前5个值: {embedding[0]:F4}, {embedding[1]:F4}, {embedding[2]:F4}, {embedding[3]:F4}, {embedding[4]:F4}");

            // 测试串行机制：再发一次
            await AppendLineAsync("[测试] 测试串行机制：发送第二个请求...");
            var embedding2 = await GetEmbeddingViaPipeAsync("第二段测试文本");
            await AppendLineAsync($"[测试] ✅ 第二个请求成功！维度: {embedding2.Length}");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[测试] 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 诊断相似度测试
    /// </summary>
    private async void BtnTestSimilarity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            await AppendLineAsync("[诊断] 开始相似度诊断测试...");
            await AppendLineAsync("[诊断] 测试 Embedding 模型对不相关文本的相似度...");

            // 发送测试命令
            var cmd = new TestSimilarityCommand();
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.TestSimilarityCommand) + "\n";

            await SendJsonAsync(json);

            // 等待结果（由 PipeReadLoopAsync 处理）
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[诊断] 失败: {ex.Message}");
        }
    }
}
