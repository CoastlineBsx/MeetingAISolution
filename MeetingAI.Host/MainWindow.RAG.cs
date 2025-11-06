using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;

namespace MeetingAI.Host;

/// <summary>
/// RAG 功能核心逻辑
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 初始化 RAG 服务
    /// </summary>
    public async Task InitializeRAGAsync()
    {
        if (_isRAGInitialized)
        {
            await AppendLineAsync("[RAG] 已初始化，跳过");
            return;
        }

        try
        {
            await AppendLineAsync("[RAG] 初始化中...");

            // 1. 初始化向量数据库
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingAI");

            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }

            var dbPath = Path.Combine(appDataPath, "meeting_rag.db");
            _vectorDb = new SqliteVectorDatabase(dbPath);
            await _vectorDb.InitializeAsync();

            await AppendLineAsync($"[RAG] 向量库已就绪: {dbPath}");

            // 2. 初始化 Embedding 服务（注入委托）
            _embeddingService = new EmbeddingNPUService(
                async (text, ct) => await GetEmbeddingViaPipeAsync(text, ct)
            );

            await AppendLineAsync("[RAG] Embedding 服务已就绪（使用 Pipeline 适配器）");

            // 3. 初始化 RAG 服务
            _ragService = new RAGService(
                _vectorDb,
                _embeddingService,
                topK: 2  // 默认检索前2个最相关的文档块
            );

            await AppendLineAsync("[RAG] RAG 服务已就绪");

            _isRAGInitialized = true;
            await AppendLineAsync("[RAG] ✅ 初始化完成");

            // 显示现有文档数量
            var docs = await _ragService.GetAllDocumentsAsync();
            await AppendLineAsync($"[RAG] 当前知识库文档数: {docs.Count}");

            // 初始化文档管理功能
            InitializeDocumentManagement();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[RAG] ❌ 初始化失败: {ex.Message}");
            _isRAGInitialized = false;
            throw;
        }
    }

    /// <summary>
    /// 构建 RAG Prompt（将查询和上下文拼接）
    /// </summary>
    private string BuildRAGPrompt(string userQuery, string contextText)
    {
        if (string.IsNullOrEmpty(contextText))
        {
            // 没有找到相关文档
            return $"你是一个智能助手。用户没有提供参考资料，请基于你的知识回答问题。\n\n【用户问题】\n{userQuery}";
        }

        return $@"你是一个智能助手，请根据以下参考资料回答用户的问题。
如果参考资料中没有相关信息，请明确说明并基于你的知识回答。

{contextText}

【用户问题】
{userQuery}

请基于以上信息简洁回答:";
    }

    /// <summary>
    /// 获取所有文档列表（用于UI显示）
    /// </summary>
    public async Task<List<DocumentInfo>> GetAllDocumentsAsync()
    {
        if (_ragService == null)
        {
            await InitializeRAGAsync();
        }

        return await _ragService!.GetAllDocumentsAsync();
    }

    /// <summary>
    /// 删除文档
    /// </summary>
    public async Task DeleteDocumentAsync(long docId)
    {
        if (_ragService == null)
            throw new InvalidOperationException("RAG 服务未初始化");

        await _ragService.DeleteDocumentAsync(docId);
        await AppendLineAsync($"[RAG] 已删除文档 ID={docId}");
    }

    /// <summary>
    /// 添加测试文档（临时方法，用于测试）
    /// </summary>
    public async Task AddTestDocumentAsync()
    {
        try
        {
            if (_ragService == null)
            {
                await InitializeRAGAsync();
            }

            await AppendLineAsync("[RAG] 添加测试文档中...");

            // 测试文档内容
            var chunks = new List<(string Content, int PageNumber)>
            {
                ("MeetingAI 是一个基于 AI 的会议记录系统，使用 Whisper 进行语音转文字，Granite 进行智能对话。", 1),
                ("系统支持实时转录麦克风和扬声器音频，可以同时录制多个音频源。", 1),
                ("RAG（Retrieval-Augmented Generation）技术可以让 AI 根据用户上传的文档回答问题。", 2),
                ("系统使用 bge-m3 模型生成 1024 维的文本向量，使用余弦相似度进行检索。", 2)
            };

            var docId = await _ragService!.AddDocumentAsync(
                filename: "MeetingAI_系统说明.txt",
                filepath: "测试文档",
                fileType: "txt",
                language: "zh-CN",
                chunks: chunks,
                cancellationToken: default
            );

            await AppendLineAsync($"[RAG] ✅ 测试文档添加成功！文档 ID={docId}, 共 {chunks.Count} 个块");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[RAG] ❌ 添加测试文档失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空所有文档（慎用！）
    /// </summary>
    public async Task ClearAllDocumentsAsync()
    {
        try
        {
            if (_ragService == null)
                return;

            var docs = await _ragService.GetAllDocumentsAsync();
            foreach (var doc in docs)
            {
                await _ragService.DeleteDocumentAsync(doc.DocId);
            }

            await AppendLineAsync($"[RAG] 已清空所有文档 (共 {docs.Count} 个)");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[RAG] 清空文档失败: {ex.Message}");
        }
    }
}
