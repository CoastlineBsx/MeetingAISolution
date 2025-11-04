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
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var workerPath = Path.Combine(baseDir, "..", "..", "MeetingAI.Worker", "MeetingAI.Worker.exe");
        var dbPath = Path.Combine(baseDir, "data", "rag.db");

        // 1. 启动 C++ Worker
        using var workerClient = new WorkerPipeClient(workerPath);
        
        Console.WriteLine("正在启动 C++ Worker (NPU)...");
        if (!await workerClient.StartAsync())
        {
            Console.WriteLine("❌ Worker 启动失败");
            return;
        }
        Console.WriteLine("✅ Worker 启动成功");

        // 2. 初始化服务
        using var vectorDb = new SqliteVectorDatabase(dbPath);
        await vectorDb.InitializeAsync();
        Console.WriteLine("✅ 向量数据库初始化完成");

        var embeddingService = new EmbeddingNPUService(workerClient);
        var graniteService = new GraniteNPUService(workerClient);
        
        using var ragService = new RAGService(
            vectorDb,
            embeddingService,
            graniteService,
            topK: 3);

        // 3. 添加文档示例（需要先实现文档解析）
        /*
        var chunks = new List<(string Content, int PageNumber)>
        {
            ("这是第一段内容，介绍了人工智能的基本概念。", 1),
            ("这是第二段内容，讨论了深度学习的应用。", 1),
            ("这是第三段内容，展望了 AI 的未来发展。", 2)
        };
        
        var docId = await ragService.AddDocumentAsync(
            "AI基础.pdf",
            "/path/to/ai.pdf",
            "pdf",
            "zh",
            chunks);
        
        Console.WriteLine($"✅ 文档已添加，ID: {docId}");
        */

        // 4. RAG 查询示例
        Console.WriteLine("\n开始 RAG 查询...");
        Console.WriteLine("问题: 什么是人工智能？");
        
        Console.Write("回答: ");
        await foreach (var chunk in ragService.QueryStreamAsync("什么是人工智能？"))
        {
            Console.Write(chunk);
        }
        Console.WriteLine("\n");

        Console.WriteLine("示例完成");
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
