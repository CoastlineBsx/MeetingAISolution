using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MeetingAI.Host;

/// <summary>
/// 字幕融合器
/// 功能：
/// 1. 按 QPC 时间戳合并双流（near/far）
/// 2. 检测时间重叠，标注来源
/// 3. 格式化输出：[time][source] text
/// 4. 增量更新（仅返回新增内容）
/// </summary>
public class CaptionMerger
{
    public class Caption
    {
        public long QpcStart { get; set; }
        public long QpcEnd { get; set; }
        public string Source { get; set; } = string.Empty; // "near" / "far"
        public string Text { get; set; } = string.Empty;
        public DateTime ReceivedTime { get; set; } = DateTime.Now;
    }

    public class MergedCaptionEventArgs : EventArgs
    {
        public string FormattedText { get; init; } = string.Empty;
        public Caption Caption { get; init; } = new Caption();
    }

    public event EventHandler<MergedCaptionEventArgs>? OnNewCaption;

    private readonly SortedList<long, Caption> _timeline = new(); // QpcStart -> Caption
    private long _lastCommittedQpc = 0; // 上次提交的时间点
    private readonly long _qpcFrequency;

    public CaptionMerger(long qpcFrequency)
    {
        _qpcFrequency = qpcFrequency;
    }

    /// <summary>
    /// 添加新的字幕片段
    /// </summary>
    public void AddCaption(string source, string text, long qpcStart, long qpcEnd)
    {
        var caption = new Caption
        {
            QpcStart = qpcStart,
            QpcEnd = qpcEnd,
            Source = source,
            Text = text,
            ReceivedTime = DateTime.Now
        };

        // 检测重叠
        bool hasOverlap = DetectOverlap(caption, out string overlapInfo);

        // 插入时间线
        _timeline[qpcStart] = caption;

        // 格式化输出
        string formattedText = FormatCaption(caption, overlapInfo);

        // 触发事件
        OnNewCaption?.Invoke(this, new MergedCaptionEventArgs
        {
            FormattedText = formattedText,
            Caption = caption
        });

        // 更新最后提交时间
        _lastCommittedQpc = Math.Max(_lastCommittedQpc, qpcEnd);

        // 清理旧片段（保留最近 5 分钟）
        CleanupOldCaptions();
    }

    /// <summary>
    /// 检测时间重叠（策略B）
    /// </summary>
    private bool DetectOverlap(Caption newCaption, out string overlapInfo)
    {
        overlapInfo = string.Empty;
        var overlappingCaptions = _timeline.Values.Where(c =>
            c.Source != newCaption.Source && // 不同来源
            IsOverlapping(c.QpcStart, c.QpcEnd, newCaption.QpcStart, newCaption.QpcEnd)
        ).ToList();

        if (overlappingCaptions.Any())
        {
            overlapInfo = $" [重叠: {string.Join(", ", overlappingCaptions.Select(c => c.Source))}]";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断两个时间段是否重叠
    /// </summary>
    private bool IsOverlapping(long start1, long end1, long start2, long end2)
    {
        return start1 < end2 && start2 < end1;
    }

    /// <summary>
    /// 格式化字幕：[time][source] text
    /// </summary>
    private string FormatCaption(Caption caption, string overlapInfo)
    {
        string timeStr = FormatTime(caption.QpcStart);
        string sourceTag = caption.Source switch
        {
            "near" => "麦克风",
            "far" => "扬声器",
            _ => caption.Source
        };

        return $"[{timeStr}][{sourceTag}] {caption.Text}{overlapInfo}";
    }

    /// <summary>
    /// 将 QPC 时间戳转换为可读时间（HH:MM:SS.mmm）
    /// </summary>
    private string FormatTime(long qpcTicks)
    {
        double totalSeconds = qpcTicks / (double)_qpcFrequency;
        int hours = (int)(totalSeconds / 3600);
        int minutes = (int)((totalSeconds % 3600) / 60);
        int seconds = (int)(totalSeconds % 60);
        int milliseconds = (int)((totalSeconds % 1) * 1000);

        if (hours > 0)
            return $"{hours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
        else
            return $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
    }

    /// <summary>
    /// 清理 5 分钟前的字幕
    /// </summary>
    private void CleanupOldCaptions()
    {
        var fiveMinutesAgo = DateTime.Now.AddMinutes(-5);
        var oldKeys = _timeline.Where(kv => kv.Value.ReceivedTime < fiveMinutesAgo).Select(kv => kv.Key).ToList();

        foreach (var key in oldKeys)
        {
            _timeline.Remove(key);
        }
    }

    /// <summary>
    /// 获取完整时间线（用于调试）
    /// </summary>
    public string GetFullTimeline()
    {
        var sb = new StringBuilder();
        foreach (var caption in _timeline.Values)
        {
            sb.AppendLine(FormatCaption(caption, ""));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 清空时间线
    /// </summary>
    public void Clear()
    {
        _timeline.Clear();
        _lastCommittedQpc = 0;
    }
}
