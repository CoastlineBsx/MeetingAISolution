using System;
using System.Collections.Generic;
using System.Linq;

namespace MeetingAI.Host;

/// <summary>
/// 流式 ASR 结果稳定器
/// 功能：
/// 1. 序号去重（同一 revision 只处理一次）
/// 2. 稳定性检测（2-3 次一致才 commit）
/// 3. 抖动缓冲（≤800ms）
/// </summary>
public class StreamStabilizer
{
    public class SegmentEventArgs : EventArgs
    {
        public string StreamId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty; // "near" / "far"
        public string Text { get; init; } = string.Empty;
        public long StartMs { get; init; }
        public long EndMs { get; init; }
        public long QpcStart { get; init; }
        public long QpcEnd { get; init; }
    }

    public event EventHandler<SegmentEventArgs>? OnStableSegment;

    private class SegmentBuffer
    {
        public string Text { get; set; } = string.Empty;
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public long QpcStart { get; set; }
        public long QpcEnd { get; set; }
        public int Count { get; set; } = 1; // 出现次数
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    private readonly Dictionary<string, Dictionary<string, SegmentBuffer>> _streamBuffers = new(); // streamId -> (textHash -> buffer)
    private readonly HashSet<string> _seenRevisions = new(); // 已处理的 revision（去重）
    private readonly int _stabilityThreshold = 2; // 2-3 次一致才 commit
    private readonly TimeSpan _jitterBufferTimeout = TimeSpan.FromMilliseconds(800); // 抖动缓冲超时

    /// <summary>
    /// 处理从 Worker 返回的流式片段
    /// </summary>
    public void OnSegmentReceived(string streamId, string source, string text, long startMs, long endMs, long qpcStart, long qpcEnd, int revision = -1)
    {
        // 1. 序号去重（如果提供了 revision）
        if (revision >= 0)
        {
            string revKey = $"{streamId}:{revision}";
            if (_seenRevisions.Contains(revKey))
            {
                return; // 已处理过
            }
            _seenRevisions.Add(revKey);

            // 清理过期的 revision（保留最近 1000 个）
            if (_seenRevisions.Count > 1000)
            {
                var toRemove = _seenRevisions.Take(500).ToList();
                foreach (var r in toRemove)
                    _seenRevisions.Remove(r);
            }
        }

        // 2. 文本标准化（去空格、转小写）
        string normalizedText = text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedText))
            return;

        // 3. 获取或创建流缓冲区
        if (!_streamBuffers.ContainsKey(streamId))
            _streamBuffers[streamId] = new Dictionary<string, SegmentBuffer>();

        var buffer = _streamBuffers[streamId];

        // 4. 检查是否已存在相同文本
        if (buffer.ContainsKey(normalizedText))
        {
            var existing = buffer[normalizedText];
            existing.Count++;
            existing.LastSeen = DateTime.Now;
            existing.QpcEnd = qpcEnd; // 更新结束时间

            // 达到稳定阈值，触发事件
            if (existing.Count >= _stabilityThreshold)
            {
                OnStableSegment?.Invoke(this, new SegmentEventArgs
                {
                    StreamId = streamId,
                    Source = source,
                    Text = text, // 使用原始文本（保留大小写和空格）
                    StartMs = existing.StartMs,
                    EndMs = existing.EndMs,
                    QpcStart = existing.QpcStart,
                    QpcEnd = existing.QpcEnd
                });

                // 移除已提交的片段
                buffer.Remove(normalizedText);
            }
        }
        else
        {
            // 新片段，加入缓冲区
            buffer[normalizedText] = new SegmentBuffer
            {
                Text = text,
                StartMs = startMs,
                EndMs = endMs,
                QpcStart = qpcStart,
                QpcEnd = qpcEnd,
                Count = 1,
                LastSeen = DateTime.Now
            };
        }

        // 5. 清理超时的片段（抖动缓冲超时）
        CleanupExpiredSegments(streamId);
    }

    /// <summary>
    /// 清理超时的片段
    /// </summary>
    private void CleanupExpiredSegments(string streamId)
    {
        if (!_streamBuffers.ContainsKey(streamId))
            return;

        var buffer = _streamBuffers[streamId];
        var now = DateTime.Now;
        var expired = buffer.Where(kv => now - kv.Value.LastSeen > _jitterBufferTimeout).Select(kv => kv.Key).ToList();

        foreach (var key in expired)
        {
            buffer.Remove(key);
        }
    }

    /// <summary>
    /// 强制清空某个流的缓冲区（停止时调用）
    /// </summary>
    public void FlushStream(string streamId)
    {
        if (!_streamBuffers.ContainsKey(streamId))
            return;

        var buffer = _streamBuffers[streamId];

        // 将所有未提交的片段强制提交（即使没达到稳定阈值）
        foreach (var kv in buffer)
        {
            var seg = kv.Value;
            OnStableSegment?.Invoke(this, new SegmentEventArgs
            {
                StreamId = streamId,
                Source = "unknown",
                Text = seg.Text,
                StartMs = seg.StartMs,
                EndMs = seg.EndMs,
                QpcStart = seg.QpcStart,
                QpcEnd = seg.QpcEnd
            });
        }

        buffer.Clear();
    }

    /// <summary>
    /// 清空所有缓冲区
    /// </summary>
    public void Clear()
    {
        _streamBuffers.Clear();
        _seenRevisions.Clear();
    }
}
