using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

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
}
