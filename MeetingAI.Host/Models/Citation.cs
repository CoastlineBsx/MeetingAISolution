namespace MeetingAI.Host.Models;

/// <summary>
/// RAG 引用数据模型
/// </summary>
public class Citation
{
    /// <summary>
    /// 来源文件名
    /// </summary>
    public string SourceFile { get; set; } = "";

    /// <summary>
    /// 文档块内容（预览）
    /// </summary>
    public string ChunkText { get; set; } = "";

    /// <summary>
    /// 相似度分数（0-1）
    /// </summary>
    public float Similarity { get; set; }

    /// <summary>
    /// 页码（PDF/Word）
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// 相似度百分比显示
    /// </summary>
    public string SimilarityDisplay => $"{Similarity:P0}";

    /// <summary>
    /// 相似度百分比（整数，用于 UI）
    /// </summary>
    public int SimilarityPercent => (int)(Similarity * 100);

    /// <summary>
    /// 来源显示（包含页码）
    /// </summary>
    public string SourceDisplay => PageNumber > 0
        ? $"{SourceFile} (第{PageNumber}页)"
        : SourceFile;

    // ========== UI 绑定别名（为了兼容不同的 XAML 绑定） ==========

    /// <summary>
    /// 文件名别名（兼容 XAML）
    /// </summary>
    public string Filename => SourceFile;

    /// <summary>
    /// 内容别名（兼容 XAML）
    /// </summary>
    public string Content => ChunkText;
}
