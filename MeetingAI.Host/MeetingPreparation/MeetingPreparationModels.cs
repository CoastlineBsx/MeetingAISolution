using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MeetingAI.Host.MeetingPreparation;

public sealed class MeetingPreparationInfo
{
    public long PreparationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MaterialCount { get; set; }
    public int EnabledHotwordCount { get; set; }
    public string Display => $"{Title} · {MaterialCount}/5 份资料 · {EnabledHotwordCount} 个热词";
}

public sealed class MeetingMaterialInfo
{
    public long DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public bool UsedOcr { get; set; }
    public string Display => $"{FileName} · {PageCount} 页 · {ChunkCount} 个知识块" + (UsedOcr ? " · OCR" : "");
}

public sealed class HotwordCandidate : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private double _score = 2.0;
    private bool _enabled = true;

    public long HotwordId { get; set; }
    public string Text { get => _text; set => Set(ref _text, value); }
    public double Score { get => _score; set => Set(ref _score, Math.Clamp(value, 1.0, 5.0)); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public List<int> SourcePages { get; set; } = new();
    public string SourceKind { get; set; } = "rule";
    public string PagesDisplay => SourcePages.Count == 0 ? "—" : string.Join(", ", SourcePages);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class MeetingMaterialProcessingResult
{
    public MeetingMaterialInfo Material { get; set; } = new();
    public List<HotwordCandidate> Hotwords { get; set; } = new();
}

public sealed class MeetingContextSnapshot
{
    public long? PreparationId { get; set; }
    public string Title { get; set; } = "无会议资料";
    public List<long> DocumentIds { get; set; } = new();
    public List<HotwordCandidate> Hotwords { get; set; } = new();
    public bool HasPreparation => PreparationId.HasValue;
    public string StatusDisplay => HasPreparation
        ? $"{DocumentIds.Count}/5 份资料 · {Hotwords.Count(item => item.Enabled)} 个热词 · 限定 RAG 已就绪"
        : "通用模式 · 不使用会前资料和热词";
}

public sealed class MeetingContextOption
{
    public long? PreparationId { get; set; }
    public string Display { get; set; } = "无会议资料（通用模式）";
}

public static class MeetingContextCoordinator
{
    public static long? PendingPreparationId { get; private set; }
    public static event EventHandler? SelectionChanged;

    public static void SelectForNextMeeting(long? preparationId)
    {
        PendingPreparationId = preparationId;
        SelectionChanged?.Invoke(null, EventArgs.Empty);
    }
}
