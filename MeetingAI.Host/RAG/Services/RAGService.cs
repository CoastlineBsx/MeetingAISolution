using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeetingAI.Host.Models;
using MeetingAI.Host.RAG.VectorStore;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// RAG 检索服务（只负责检索，不负责生成）
/// </summary>
public class RAGService : IDisposable
{
    private readonly SqliteVectorDatabase _vectorDb;
    private readonly EmbeddingNPUService _embeddingService;
    private readonly int _topK;

    public RAGService(
        SqliteVectorDatabase vectorDb,
        EmbeddingNPUService embeddingService,
        int topK = 3)
    {
        _vectorDb = vectorDb ?? throw new ArgumentNullException(nameof(vectorDb));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _topK = topK;
    }

    /// <summary>
    /// 检索相关文档上下文
    /// </summary>
    public async Task<RAGContext> RetrieveContextAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        // 1. 问题向量化
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, cancellationToken);

        // 2. 向量检索
        var searchResults = await _vectorDb.SearchAsync(queryEmbedding, _topK);

        // 3. 构建上下文文本
        var contextText = BuildContextText(searchResults);

        // 4. 构建引用列表
        var citations = BuildCitations(searchResults);

        return new RAGContext
        {
            ContextText = contextText,
            Citations = citations,
            Results = searchResults
        };
    }

    /// <summary>
    /// 添加文档到向量库
    /// </summary>
    public async Task<long> AddDocumentAsync(
        string filename,
        string filepath,
        string fileType,
        string language,
        List<(string Content, int PageNumber)> chunks,
        CancellationToken cancellationToken = default)
    {
        // 1. 添加文档记录
        var docId = await _vectorDb.AddDocumentAsync(filename, filepath, fileType, language);

        // 2. 为每个块生成 Embedding 并存储
        int chunkIndex = 0;
        foreach (var (content, pageNumber) in chunks)
        {
            var embedding = await _embeddingService.GetEmbeddingAsync(content, cancellationToken);
            await _vectorDb.AddChunkAsync(docId, chunkIndex++, pageNumber, content, embedding);
        }

        // 3. 更新文档块数量
        await _vectorDb.UpdateDocumentChunkCountAsync(docId, chunks.Count);

        return docId;
    }

    /// <summary>
    /// 获取所有文档列表
    /// </summary>
    public async Task<List<DocumentInfo>> GetAllDocumentsAsync()
    {
        return await _vectorDb.GetAllDocumentsAsync();
    }

    /// <summary>
    /// 删除文档
    /// </summary>
    public async Task DeleteDocumentAsync(long docId)
    {
        await _vectorDb.DeleteDocumentAsync(docId);
    }

    private string BuildContextText(List<SearchResult> searchResults)
    {
        if (searchResults.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("【参考资料】");

        for (int i = 0; i < searchResults.Count; i++)
        {
            var result = searchResults[i];
            sb.AppendLine($"[资料{i + 1}] 来源: {result.Filename} (第{result.PageNumber}页) - 相似度: {result.Similarity:F3}");
            sb.AppendLine(result.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private List<Citation> BuildCitations(List<SearchResult> searchResults)
    {
        var citations = new List<Citation>();

        for (int i = 0; i < searchResults.Count; i++)
        {
            var result = searchResults[i];
            citations.Add(new Citation
            {
                SourceFile = result.Filename,
                PageNumber = result.PageNumber,
                Similarity = result.Similarity,
                ChunkText = result.Content.Length > 100
                    ? result.Content.Substring(0, 100) + "..."
                    : result.Content
            });
        }

        return citations;
    }

    public void Dispose()
    {
        _vectorDb?.Dispose();
    }
}

/// <summary>
/// RAG 上下文结果
/// </summary>
public class RAGContext
{
    /// <summary>
    /// 构建的上下文文本（用于拼接到 Prompt）
    /// </summary>
    public string ContextText { get; set; } = string.Empty;

    /// <summary>
    /// 引用列表（用于 UI 显示）
    /// </summary>
    public List<Citation> Citations { get; set; } = new();

    /// <summary>
    /// 原始搜索结果
    /// </summary>
    public List<SearchResult> Results { get; set; } = new();
}

