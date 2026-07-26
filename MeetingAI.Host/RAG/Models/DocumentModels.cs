using System;
using System.Collections.Generic;

namespace MeetingAI.Host.RAG.Models;

/// <summary>
/// 提取的文档内容
/// </summary>
public class ExtractedDocument
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty; // txt, docx, pdf, jpg, png
    public long FileSize { get; set; } // 字节数
    public int PageCount { get; set; }
    public bool UsedOcr { get; set; } // 是否使用了 OCR
    public List<ExtractedDocumentPage> Pages { get; set; } = new();
}

public class ExtractedDocumentPage
{
    public int PageNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool UsedOcr { get; set; }
    public int ImageCount { get; set; }
}

/// <summary>
/// 文档分块
/// </summary>
public class DocumentChunk
{
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // 文件名
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public int StartChar { get; set; }
    public int EndChar { get; set; }
    public int PageNumber { get; set; }
}

/// <summary>
/// 数据库中的文档信息
/// </summary>
public class DocumentInfo
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadTime { get; set; }
    public int ChunkCount { get; set; }
    public bool HasOcr { get; set; }
    public string OcrLanguage { get; set; } = string.Empty;

    // UI 显示辅助属性
    public string FileSizeDisplay => FormatFileSize(FileSize);
    public string UploadTimeDisplay => UploadTime.ToString("MM-dd HH:mm");
    public string OcrBadge => HasOcr ? "✓" : "-";

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
        return $"{bytes / (1024 * 1024)}MB";
    }
}

/// <summary>
/// 文档统计信息
/// </summary>
public class DocumentStats
{
    public int TotalDocuments { get; set; }
    public int TotalChunks { get; set; }
    public int OcrDocuments { get; set; }

    public string DisplayText => $"📊 {TotalDocuments}文档 | {TotalChunks}块 | {OcrDocuments}OCR";
}
