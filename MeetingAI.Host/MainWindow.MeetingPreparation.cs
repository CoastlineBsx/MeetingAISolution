using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeetingAI.Host.MeetingPreparation;
using MeetingAI.Host.RAG.Services;

namespace MeetingAI.Host;

public sealed partial class MainWindow
{
    public const int MaxMeetingPreparationMaterials = 5;
    private readonly HotwordExtractor _meetingHotwordExtractor = new();

    public async Task<long> CreateMeetingPreparationAsync(string title)
    {
        await EnsurePreparationServicesAsync();
        return await _vectorDb!.CreatePreparationAsync(title);
    }

    public async Task<MeetingMaterialProcessingResult> AddMeetingPreparationMaterialAsync(
        long preparationId,
        string filePath,
        IProgress<string>? progress = null)
    {
        await EnsurePreparationServicesAsync();
        var count = await _vectorDb!.GetPreparationMaterialCountAsync(preparationId);
        if (count >= MaxMeetingPreparationMaterials)
            throw new InvalidOperationException("一场会议最多只能上传 5 份资料");
        progress?.Report("正在读取并按页解析资料…");
        var extracted = await _documentProcessor!.ExtractAsync(filePath);
        if (extracted.Pages.Count == 0 || string.IsNullOrWhiteSpace(extracted.Content))
            throw new InvalidDataException("资料中没有提取到可用文字");

        var chunks = _documentChunker!.ChunkDocument(extracted.Pages, extracted.FileName);
        if (chunks.Count == 0) throw new InvalidDataException("资料无法生成知识块");

        var docId = await _vectorDb!.AddDocumentAsync(
            extracted.FileName, filePath, extracted.FileType, "auto",
            extracted.FileSize, extracted.UsedOcr);

        try
        {
            progress?.Report($"正在生成本地向量（0/{chunks.Count}）…");
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var embedding = await _embeddingService!.GetEmbeddingAsync(chunk.Text);
                if (embedding.Length == 0) throw new InvalidOperationException($"第 {i + 1} 个知识块未生成向量");
                await _vectorDb.AddChunkAsync(docId, chunk.ChunkIndex, chunk.PageNumber, chunk.Text, embedding);
                progress?.Report($"正在生成本地向量（{i + 1}/{chunks.Count}）…");
            }
            await _vectorDb.UpdateDocumentChunkCountAsync(docId, chunks.Count);
            await _vectorDb.AttachDocumentToPreparationAsync(preparationId, docId, extracted.PageCount);

            var generated = _meetingHotwordExtractor.Extract(extracted.Pages);
            var existing = await _vectorDb.GetHotwordsAsync(preparationId);
            var merged = existing.Concat(generated)
                .GroupBy(item => item.Text.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var preferred = group.OrderByDescending(item => item.Score).First();
                    preferred.SourcePages = group.SelectMany(item => item.SourcePages).Distinct().OrderBy(x => x).ToList();
                    return preferred;
                })
                .OrderByDescending(item => item.Score).ThenBy(item => item.Text)
                .Take(150).ToList();
            await _vectorDb.SaveHotwordsAsync(preparationId, merged);

            return new MeetingMaterialProcessingResult
            {
                Material = new MeetingMaterialInfo
                {
                    DocumentId = docId,
                    FileName = extracted.FileName,
                    FileType = extracted.FileType,
                    PageCount = extracted.PageCount,
                    ChunkCount = chunks.Count,
                    UsedOcr = extracted.UsedOcr
                },
                Hotwords = merged
            };
        }
        catch
        {
            await _vectorDb.DeleteDocumentAsync(docId);
            throw;
        }
    }

    public async Task<List<MeetingMaterialInfo>> GetMeetingPreparationMaterialsAsync(long preparationId)
    {
        await EnsurePreparationServicesAsync();
        return await _vectorDb!.GetPreparationMaterialsAsync(preparationId);
    }

    public async Task<List<HotwordCandidate>> GetMeetingPreparationHotwordsAsync(long preparationId)
    {
        await EnsurePreparationServicesAsync();
        return await _vectorDb!.GetHotwordsAsync(preparationId);
    }

    public async Task SaveMeetingPreparationHotwordsAsync(long preparationId, IEnumerable<HotwordCandidate> hotwords)
    {
        await EnsurePreparationServicesAsync();
        await _vectorDb!.SaveHotwordsAsync(preparationId, hotwords);
    }

    public async Task<List<MeetingPreparationInfo>> GetMeetingPreparationsAsync()
    {
        await EnsurePreparationServicesAsync();
        return await _vectorDb!.GetPreparationsAsync();
    }

    public async Task<MeetingContextSnapshot> GetMeetingContextSnapshotAsync(long? preparationId)
    {
        if (!preparationId.HasValue)
            return new MeetingContextSnapshot();

        await EnsurePreparationServicesAsync();
        var preparation = (await _vectorDb!.GetPreparationsAsync())
            .FirstOrDefault(item => item.PreparationId == preparationId.Value)
            ?? throw new InvalidOperationException("所选会议准备档案不存在");
        return new MeetingContextSnapshot
        {
            PreparationId = preparation.PreparationId,
            Title = preparation.Title,
            DocumentIds = await _vectorDb.GetPreparationDocumentIdsAsync(preparation.PreparationId),
            Hotwords = await _vectorDb.GetHotwordsAsync(preparation.PreparationId)
        };
    }

    public async Task<List<RAG.VectorStore.SearchResult>> SearchMeetingPreparationAsync(
        long preparationId,
        string query,
        int topK = 5)
    {
        await EnsurePreparationServicesAsync();
        var embedding = await _embeddingService!.GetEmbeddingAsync(query);
        return await _vectorDb!.SearchPreparationAsync(preparationId, embedding, topK);
    }

    public void UsePreparationForNextMeeting(long preparationId)
    {
        MeetingContextCoordinator.SelectForNextMeeting(preparationId);
        NavigateToPage("streaming_meeting");
        if (StreamingMeetingFrame.Content is Pages.StreamingMeetingPage page)
            _ = page.SelectMeetingContextAsync(preparationId);
    }

    public void OpenMeetingPreparationPage()
    {
        NavigateToPage("meeting_preparation");
        if (MeetingContextCoordinator.PendingPreparationId is long preparationId &&
            MeetingPreparationFrame.Content is Pages.MeetingPreparationPage page)
            _ = page.LoadPreparationAsync(preparationId);
    }

    private async Task EnsurePreparationServicesAsync()
    {
        if (_vectorDb == null || _embeddingService == null)
            await InitializeRAGAsync();
        if (_documentProcessor == null || _documentChunker == null)
            InitializeDocumentManagement();
        if (_vectorDb == null || _embeddingService == null || _documentProcessor == null || _documentChunker == null)
            throw new InvalidOperationException("会前资料服务未能初始化。请先启动 Worker 并加载 Embedding 模型。");
    }
}
