using System;
using System.IO;
using System.Threading.Tasks;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;

namespace MeetingAI.Host.RAG;

/// <summary>
/// RAG 系统使用示例
/// </summary>
public class RAGExample
{
    public static async Task RunExampleAsync()
    {
        // 注意：此示例已过时，RAG 服务已改为通过 MainWindow.RAG.cs 集成
        // 请参考 MainWindow.RAG.cs 查看新的 RAG 使用方式

        Console.WriteLine("此示例已过时。请使用 UI 界面中的 RAG 功能。");
        await Task.CompletedTask;

        /*
        // 旧的示例代码（已废弃）
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var workerPath = Path.Combine(baseDir, "..", "..", "MeetingAI.Worker", "MeetingAI.Worker.exe");
        var dbPath = Path.Combine(baseDir, "data", "rag.db");

        // RAG 服务现在使用委托注入方式，不再直接使用 WorkerPipeClient
        // 请参考 MainWindow.RAG.cs 中的 InitializeRAGAsync() 方法
        */
    }

    /// <summary>
    /// 简单测试 Granite NPU
    /// </summary>
    public static async Task TestGraniteNPUAsync()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var workerPath = Path.Combine(baseDir, "..", "..", "MeetingAI.Worker", "MeetingAI.Worker.exe");

        using var workerClient = new WorkerPipeClient(workerPath);
        
        Console.WriteLine("启动 Worker...");
        if (!await workerClient.StartAsync())
        {
            Console.WriteLine("启动失败");
            return;
        }

        var graniteService = new GraniteNPUService(workerClient);
        
        Console.WriteLine("\n测试 Granite 3.3 2B (NPU):");
        Console.Write("回答: ");
        
        await foreach (var chunk in graniteService.GenerateStreamAsync(
            "请用一句话介绍什么是 RAG 检索增强生成", 
            maxTokens: 100))
        {
            Console.Write(chunk);
        }

        Console.WriteLine("\n\n测试完成");
    }
}
