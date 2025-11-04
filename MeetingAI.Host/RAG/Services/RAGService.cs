using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeetingAI.Host.RAG.VectorStore;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// RAG 检索增强生成服务
/// </summary>
public class RAGService : IDisposable
{
    private readonly SqliteVectorDatabase _vectorDb;
    private readonly EmbeddingNPUService _embeddingService;
    private readonly GraniteNPUService _graniteService;
    private readonly int _topK;

    public RAGService(
        SqliteVectorDatabase vectorDb,
        EmbeddingNPUService embeddingService,
        GraniteNPUService graniteService,
        int topK = 3)
    {
        _vectorDb = vectorDb;
        _embeddingService = embeddingService;
        _graniteService = graniteService;
        _topK = topK;
    }

    /// <summary>
    /// 执行 RAG 查询（流式返回）
    /// </summary>
    public async IAsyncEnumerable<string> QueryStreamAsync(
        string question,
        float temperature = 0.7f,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. 问题向量化
        var questionEmbedding = await _embeddingService.GetEmbeddingAsync(question, cancellationToken);

        // 2. 向量检索
        var searchResults = await _vectorDb.SearchAsync(questionEmbedding, _topK);

        // 3. 构建 RAG Prompt
        var prompt = BuildRAGPrompt(question, searchResults);

        // 4. Granite NPU 生成
        await foreach (var chunk in _graniteService.GenerateStreamAsync(
            prompt,
            maxTokens: 256,
            temperature: temperature,
            cancellationToken: cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// 执行 RAG 查询（一次性返回）
    /// </summary>
    public async Task<string> QueryAsync(
        string question,
        float temperature = 0.7f,
        CancellationToken cancellationToken = default)
    {
        var questionEmbedding = await _embeddingService.GetEmbeddingAsync(question, cancellationToken);
        var searchResults = await _vectorDb.SearchAsync(questionEmbedding, _topK);
        var prompt = BuildRAGPrompt(question, searchResults);

        return await _graniteService.GenerateAsync(
            prompt,
            maxTokens: 256,
            temperature: temperature,
            cancellationToken: cancellationToken);
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

    private string BuildRAGPrompt(string question, List<SearchResult> searchResults)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是一个智能助手，请根据以下参考资料回答用户的问题。");
        sb.AppendLine("如果参考资料中没有相关信息，请明确说明并基于你的知识回答。");
        sb.AppendLine();

        if (searchResults.Count > 0)
        {
            sb.AppendLine("【参考资料】");
            for (int i = 0; i < searchResults.Count; i++)
            {
                var result = searchResults[i];
                sb.AppendLine($"[资料{i + 1}] 来源: {result.Filename} (第{result.PageNumber}页) - 相似度: {result.Similarity:F3}");
                sb.AppendLine(result.Content);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("【参考资料】");
            sb.AppendLine("(未找到相关文档)");
            sb.AppendLine();
        }

        sb.AppendLine("【用户问题】");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("请基于以上信息简洁回答:");

        return sb.ToString();
    }

    public void Dispose()
    {
        _vectorDb?.Dispose();
    }
}
