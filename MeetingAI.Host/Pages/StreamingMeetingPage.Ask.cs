using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeetingAI.Host.Pages;

/// <summary>
/// 会议内搜索（AutoSuggestBox）与“询问本次会议”（本地检索 + Granite 回答）。
/// 全部基于内存字幕和现有管道命令，不依赖 FTS5，也不改 Worker。
/// </summary>
public sealed partial class StreamingMeetingPage
{
    // ========== 搜索 ==========

    private sealed class CaptionSearchResult
    {
        public required StreamingCaption Caption { get; init; }
        public required string Header { get; init; }
        public required string Snippet { get; init; }
        public override string ToString() => Snippet;
    }

    private List<StreamingCaption> _searchMatches = new();
    private int _searchMatchIndex = -1;
    private StreamingCaption? _flashedCaption;
    private DispatcherTimer? _flashTimer;

    // 不能用 static 字段初始化 WinUI 画刷：类型初始化器可能在非 UI 单元
    // 运行，SolidColorBrush 构造会抛 COMException 0x8001010E。
    private Brush? _flashBrush;

    private void TxtCaptionSearch_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var results = BuildSearchResults(sender.Text);
        sender.ItemsSource = results;
    }

    private void TxtCaptionSearch_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is CaptionSearchResult chosen)
        {
            JumpToCaption(chosen.Caption);
            return;
        }

        // 直接回车：在全部命中之间循环跳转（类似 Ctrl+F 的“下一个”）
        var results = BuildSearchResults(args.QueryText);
        if (_searchMatches.Count == 0)
        {
            SetStatus("没有匹配的字幕");
            return;
        }

        _searchMatchIndex = (_searchMatchIndex + 1) % _searchMatches.Count;
        SetStatus($"匹配 {_searchMatchIndex + 1}/{_searchMatches.Count}");
        JumpToCaption(_searchMatches[_searchMatchIndex]);
    }

    private List<CaptionSearchResult> BuildSearchResults(string query)
    {
        var results = new List<CaptionSearchResult>();
        var terms = TokenizeQuery(query);
        _searchMatches = new List<StreamingCaption>();

        if (terms.Count == 0)
        {
            _searchMatchIndex = -1;
            return results;
        }

        foreach (var caption in VisibleCaptions)
        {
            string haystack = caption.Text + "\n" + caption.TranslatedText;
            if (string.IsNullOrWhiteSpace(haystack))
            {
                continue;
            }

            bool allHit = terms.All(t =>
                haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (!allHit)
            {
                continue;
            }

            _searchMatches.Add(caption);
            if (results.Count < 20)
            {
                results.Add(new CaptionSearchResult
                {
                    Caption = caption,
                    Header = $"[{caption.Timestamp}] {caption.SpeakerName}",
                    Snippet = BuildSnippet(caption, terms[0])
                });
            }
        }

        _searchMatchIndex = -1;
        return results;
    }

    private static List<string> TokenizeQuery(string query)
    {
        return (query ?? "")
            .Split(
                new[] { ' ', '\t', '，', ',', '。', '？', '?', '！', '!' },
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string BuildSnippet(StreamingCaption caption, string term)
    {
        string text = caption.Text;
        int index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            text = caption.TranslatedText;
            index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        }
        if (index < 0 || text.Length <= 80)
        {
            return text.Length <= 80 ? text : text[..80] + "…";
        }

        int start = Math.Max(0, index - 24);
        int length = Math.Min(80, text.Length - start);
        return (start > 0 ? "…" : "") +
               text.Substring(start, length) +
               (start + length < text.Length ? "…" : "");
    }

    private void JumpToCaption(StreamingCaption caption)
    {
        if (!VisibleCaptions.Contains(caption))
        {
            // 结果来自另一版本的稿件（例如搜的是实时稿、现在显示最终稿）。
            bool inRefined = _refinedCaptions.Contains(caption);
            if (inRefined == _showingRefinedTranscript)
            {
                return; // 已被 Clear 掉的旧结果
            }
            ShowTranscriptVersion(inRefined);
        }

        CaptionsList.ScrollIntoView(caption);
        FlashCaption(caption);
    }

    private void FlashCaption(StreamingCaption caption)
    {
        if (_flashedCaption != null)
        {
            _flashedCaption.HighlightBrush = null;
        }

        _flashedCaption = caption;
        _flashBrush ??= new SolidColorBrush(Color.FromArgb(70, 255, 185, 0));
        caption.HighlightBrush = _flashBrush;

        _flashTimer ??= CreateFlashTimer();
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private DispatcherTimer CreateFlashTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_flashedCaption != null)
            {
                _flashedCaption.HighlightBrush = null;
                _flashedCaption = null;
            }
        };
        return timer;
    }

    // ========== 询问本次会议 ==========

    private sealed class AskSegment
    {
        public long SegmentId;
        public string Speaker = "";
        public string Text = "";
        public bool IsRefined;
        public float[]? Embedding;
    }

    private sealed class AskSourceItem
    {
        public long SegmentId;
        public bool IsRefined;
        public string Display { get; init; } = "";
    }

    private readonly object _askLock = new();
    private readonly Dictionary<long, AskSegment> _askSegments = new();
    private readonly ConcurrentQueue<long> _embeddingQueue = new();
    // GetEmbeddingViaPipeAsync 内部是全局单飞 TCS，页面侧必须自行串行化，
    // 否则问题向量和后台字幕向量会互相打翻对方的请求。
    private static readonly SemaphoreSlim EmbeddingGate = new(1, 1);
    private Task? _embeddingPumpTask;
    private CancellationTokenSource? _embeddingPumpCts;
    private bool _isAsking;
    private DispatcherTimer? _askTimeoutTimer;
    private readonly StringBuilder _askAnswerBuffer = new();

    /// <summary>UI 线程调用：登记一条定稿字幕，进入问答语料和嵌入队列。</summary>
    private void RegisterAskSegment(
        long segmentId,
        string source,
        string text,
        bool isRefined)
    {
        if (segmentId <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string speaker = source is SystemSource or "对方" ? "对方" : "我方";
        lock (_askLock)
        {
            _askSegments[segmentId] = new AskSegment
            {
                SegmentId = segmentId,
                Speaker = speaker,
                Text = text,
                IsRefined = isRefined
            };
        }

        _embeddingQueue.Enqueue(segmentId);
        EnsureEmbeddingPump();

        if (!_isAsking)
        {
            BtnAsk.IsEnabled = true;
        }
    }

    private void EnsureEmbeddingPump()
    {
        if (_embeddingPumpTask is { IsCompleted: false })
        {
            return;
        }

        _embeddingPumpCts = new CancellationTokenSource();
        var token = _embeddingPumpCts.Token;
        _embeddingPumpTask = Task.Run(() => EmbeddingPumpAsync(token));
    }

    private async Task EmbeddingPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_embeddingQueue.TryDequeue(out long segmentId))
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }

            AskSegment? segment;
            lock (_askLock)
            {
                _askSegments.TryGetValue(segmentId, out segment);
            }
            if (segment == null || segment.Embedding != null)
            {
                continue;
            }

            if (_mainWindow is not { } window || !window.IsEmbeddingLoaded)
            {
                // 模型未加载：丢弃，问答退化为关键词检索。模型加载后
                // 新到的字幕会正常获得向量。
                continue;
            }

            try
            {
                await EmbeddingGate.WaitAsync(cancellationToken);
                float[] embedding;
                try
                {
                    embedding = await window.GetEmbeddingViaPipeAsync(
                        segment.Text,
                        cancellationToken);
                }
                finally
                {
                    EmbeddingGate.Release();
                }

                if (embedding.Length > 0)
                {
                    lock (_askLock)
                    {
                        if (_askSegments.TryGetValue(segmentId, out var current))
                        {
                            current.Embedding = embedding;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // 与文档 RAG 抢了同一个嵌入请求：稍后重试。
                _embeddingQueue.Enqueue(segmentId);
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[MeetingAsk] Embedding failed for {segmentId}: {ex.Message}");
            }

            // 让音频/命令消息有喘息空间，嵌入不抢管道。
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void TxtAskInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && BtnAsk.IsEnabled)
        {
            e.Handled = true;
            BtnAsk_Click(sender, new RoutedEventArgs());
        }
    }

    public void BtnAsk_Click(object sender, RoutedEventArgs e)
        => _ = RunAskAsync();

    private async Task RunAskAsync()
    {
        if (_isAsking || _mainWindow is not { } window)
        {
            return;
        }

        string question = TxtAskInput.Text?.Trim() ?? "";
        if (question.Length == 0)
        {
            TxtAskStatus.Text = "请输入问题。";
            return;
        }

        if (!window.IsGraniteLoaded)
        {
            TxtAskStatus.Text = "Granite 未加载：请先到 Startup 页面加载 Granite。";
            return;
        }

        List<AskSegment> corpus;
        lock (_askLock)
        {
            // Whisper 精修完成后以最终稿为准，否则用实时稿。
            bool useRefined = _refinedTranscriptReady &&
                _askSegments.Values.Any(s => s.IsRefined);
            corpus = _askSegments.Values
                .Where(s => s.IsRefined == useRefined)
                .OrderBy(s => s.SegmentId)
                .ToList();
        }
        if (corpus.Count == 0)
        {
            TxtAskStatus.Text = "还没有可用的定稿字幕。";
            return;
        }

        _isAsking = true;
        BtnAsk.IsEnabled = false;
        _askAnswerBuffer.Clear();
        TxtAskAnswer.Text = "";
        AskSourcesList.ItemsSource = null;
        TxtAskStatus.Text = _isRecording
            ? "正在检索并生成回答…（生成期间实时字幕会短暂延迟）"
            : "正在检索并生成回答…";

        try
        {
            var selected = await SelectRelevantSegmentsAsync(
                window, question, corpus);

            var prompt = BuildAskPrompt(question, selected);
            var sources = selected
                .Select((s, i) => new AskSourceItem
                {
                    SegmentId = s.SegmentId,
                    IsRefined = s.IsRefined,
                    Display = $"[{i + 1}] {s.Speaker}: " +
                              (s.Text.Length > 46 ? s.Text[..46] + "…" : s.Text)
                })
                .ToList();

            window.MeetingAskTokenHandler = token =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _askAnswerBuffer.Append(token);
                    TxtAskAnswer.Text = _askAnswerBuffer.ToString();
                });
            };
            window.MeetingAskDoneHandler = () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    FinishAsk(window);
                    AskSourcesList.ItemsSource = sources;
                    TxtAskStatus.Text =
                        $"回答完成 · 依据 {sources.Count} 条字幕（点击下方来源可跳转）";
                });
            };

            StartAskTimeout(window);

            var cmd = new Contracts.Messages.GraniteGenerateStreamCommand
            {
                prompt = prompt,
                max_tokens = 512,
                temperature = 0.0f
            };
            await window.SendJsonAsync(
                JsonSerializer.Serialize(
                    cmd,
                    Contracts.AppJsonContext.Utf8.GraniteGenerateStreamCommand) +
                "\n");
        }
        catch (Exception ex)
        {
            FinishAsk(window);
            TxtAskStatus.Text = $"提问失败：{ex.Message}";
        }
    }

    private async Task<List<AskSegment>> SelectRelevantSegmentsAsync(
        MainWindow window,
        string question,
        List<AskSegment> corpus)
    {
        var hits = new HashSet<long>();

        // 1) 关键词命中（对名称、金额、编号最可靠）
        var terms = TokenizeQuery(question)
            .Where(t => t.Length >= 2)
            .ToList();
        foreach (var segment in corpus)
        {
            if (terms.Any(t => segment.Text.Contains(
                    t, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(segment.SegmentId);
            }
        }

        // 2) 语义命中（有向量才参与；嵌入模型未加载时自动退化）
        var embedded = corpus.Where(s => s.Embedding != null).ToList();
        if (embedded.Count > 0 && window.IsEmbeddingLoaded)
        {
            try
            {
                await EmbeddingGate.WaitAsync();
                float[] questionEmbedding;
                try
                {
                    questionEmbedding =
                        await window.GetEmbeddingViaPipeAsync(question);
                }
                finally
                {
                    EmbeddingGate.Release();
                }

                if (questionEmbedding.Length > 0)
                {
                    foreach (var id in embedded
                        .Select(s => (s.SegmentId,
                            Score: CosineSimilarity(
                                questionEmbedding, s.Embedding!)))
                        .OrderByDescending(x => x.Score)
                        .Take(8)
                        .Select(x => x.SegmentId))
                    {
                        hits.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[MeetingAsk] Question embedding failed: {ex.Message}");
            }
        }

        // 3) 兜底：什么都没命中就取最近的字幕
        var selected = corpus
            .Where(s => hits.Contains(s.SegmentId))
            .ToList();
        if (selected.Count == 0)
        {
            selected = corpus.TakeLast(10).ToList();
        }

        // 按时间序，控制条数和总长度，避免撑爆 Granite 上下文
        selected = selected.OrderBy(s => s.SegmentId).ToList();
        while (selected.Count > 12 ||
               selected.Sum(s => s.Text.Length) > 3000)
        {
            selected.RemoveAt(0);
        }
        return selected;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA <= 0 || normB <= 0) return 0f;
        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }

    private static string BuildAskPrompt(
        string question,
        List<AskSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是完全离线运行的会议助手。以下是本次会议的部分字幕摘录，带有编号。");
        sb.AppendLine("请只依据这些摘录回答用户的问题；摘录中没有的信息必须回答“会议内容中未提及”，不要编造。");
        sb.AppendLine("引用依据时使用编号，例如 [2]。");
        sb.AppendLine();
        sb.AppendLine("会议字幕摘录：");
        for (int i = 0; i < segments.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {segments[i].Speaker}: {segments[i].Text}");
        }
        sb.AppendLine();
        sb.AppendLine($"用户问题：{question}");
        return sb.ToString();
    }

    private void StartAskTimeout(MainWindow window)
    {
        _askTimeoutTimer ??= CreateAskTimeoutTimer();
        _askTimeoutTimer.Stop();
        _askTimeoutTimer.Start();
    }

    private DispatcherTimer CreateAskTimeoutTimer()
    {
        var timer = new DispatcherTimer
        {
            // Granite 可能正被实时摘要占用（Worker 侧串行），给足排队时间。
            Interval = TimeSpan.FromSeconds(240)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_isAsking && _mainWindow is { } window)
            {
                FinishAsk(window);
                TxtAskStatus.Text = "生成超时：Granite 可能正忙，请稍后重试。";
            }
        };
        return timer;
    }

    private void FinishAsk(MainWindow window)
    {
        window.MeetingAskTokenHandler = null;
        window.MeetingAskDoneHandler = null;
        _askTimeoutTimer?.Stop();
        _isAsking = false;
        bool hasCorpus;
        lock (_askLock)
        {
            hasCorpus = _askSegments.Count > 0;
        }
        BtnAsk.IsEnabled = hasCorpus;
    }

    public void AskSourcesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AskSourceItem item)
        {
            return;
        }

        var map = item.IsRefined
            ? _refinedCaptionBySegment
            : _liveCaptionBySegment;
        if (map.TryGetValue(item.SegmentId, out var caption))
        {
            JumpToCaption(caption);
        }
    }

    /// <summary>ClearAll 时调用：清空搜索与问答的所有状态。</summary>
    private void ResetSearchAndAsk()
    {
        if (_mainWindow is { } window && _isAsking)
        {
            FinishAsk(window);
        }

        lock (_askLock)
        {
            _askSegments.Clear();
        }
        while (_embeddingQueue.TryDequeue(out _)) { }

        _searchMatches.Clear();
        _searchMatchIndex = -1;
        _flashTimer?.Stop();
        _flashedCaption = null;

        TxtCaptionSearch.Text = "";
        TxtCaptionSearch.ItemsSource = null;
        TxtAskInput.Text = "";
        TxtAskAnswer.Text = "";
        _askAnswerBuffer.Clear();
        AskSourcesList.ItemsSource = null;
        TxtAskStatus.Text = "会议开始并出现定稿字幕后即可提问。";
        BtnAsk.IsEnabled = false;
    }
}
