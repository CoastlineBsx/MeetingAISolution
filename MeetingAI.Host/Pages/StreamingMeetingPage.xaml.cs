using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Data.Sqlite;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MeetingAI.Host.Contracts.Messages;
using MeetingAI.Host.MeetingPreparation;

namespace MeetingAI.Host.Pages;

public sealed partial class StreamingMeetingPage : Page
{
    // ========== 数据模型 ==========
    // 实时字幕会就地改写最后一条的 Text/TextOpacity，ObservableCollection 只通知增删，
    // 不实现 INotifyPropertyChanged 的话 partial 结果在界面上完全看不到。
    public class StreamingCaption : INotifyPropertyChanged
    {
        private string _text = "";
        private double _textOpacity = 1.0;
        private string _translatedText = "";
        private double _translationOpacity = 1.0;

        public string SpeakerName { get; set; } = "Unknown";
        public SolidColorBrush SpeakerColor { get; set; } = new SolidColorBrush(Colors.Gray);
        public string Timestamp { get; set; } = "";
        public long SegmentId { get; set; }
        public long StartMs { get; set; }

        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; OnPropertyChanged(); } }
        }

        // 临时结果半透明，定稿后恢复不透明
        public double TextOpacity
        {
            get => _textOpacity;
            set { if (Math.Abs(_textOpacity - value) > 0.001) { _textOpacity = value; OnPropertyChanged(); } }
        }

        public string TranslatedText
        {
            get => _translatedText;
            set
            {
                if (_translatedText != value)
                {
                    _translatedText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TranslatedDisplay));
                }
            }
        }

        public string TranslatedDisplay => string.IsNullOrWhiteSpace(_translatedText)
            ? ""
            : $"译文  {_translatedText}";

        public double TranslationOpacity
        {
            get => _translationOpacity;
            set
            {
                if (Math.Abs(_translationOpacity - value) > 0.001)
                {
                    _translationOpacity = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class Speaker
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Gray);
    }

    private sealed class CaptionStreamState
    {
        public StreamingCaption? CurrentParagraph { get; set; }
        public string ConfirmedText { get; set; } = "";
        public bool HasActivePartial { get; set; }
        public DateTime LastFinalTime { get; set; } = DateTime.MinValue;
    }

    private sealed class CaptionTranslationState
    {
        public SortedDictionary<long, string> Confirmed { get; } = new();
        public long? PartialUtteranceId { get; set; }
        public string PartialText { get; set; } = "";
    }

    private sealed class AudioCapturePipeline
    {
        public AudioCapturePipeline(
            string source,
            string displayName,
            bool fillSilence,
            IWaveIn capture,
            BufferedWaveProvider buffer,
            ISampleProvider resampled)
        {
            Source = source;
            DisplayName = displayName;
            FillSilence = fillSilence;
            Capture = capture;
            Buffer = buffer;
            Resampled = resampled;
        }

        public string Source { get; }
        public string DisplayName { get; }
        public bool FillSilence { get; }
        public IWaveIn Capture { get; }
        public BufferedWaveProvider Buffer { get; }
        public ISampleProvider Resampled { get; }
        public EventHandler<WaveInEventArgs>? DataAvailableHandler { get; set; }
        public EventHandler<StoppedEventArgs>? RecordingStoppedHandler { get; set; }
    }

    // ========== 常量 ==========
    private const string MicrophoneSource = "microphone";
    private const string SystemSource = "system";
    private const int TargetSampleRate = 16000;
    private const int ChunkSamples = 1600;   // 100ms @ 16kHz，兼顾延迟和管道压力

    // 段落合并：sherpa 只按静音切分，人在句子中间思考停顿也会被切开，
    // 结果是语义完整的一句话散成好几条。这里用标点模型的输出当语义信号，
    // 句尾是 。！？ 才认为说完了，否则并进同一段。
    private const double ParagraphGapSeconds = 3.0;   // 超过此间隔强制封口
    private const int MaxParagraphChars = 200;        // 段落长度上限，防止无限长
    private const double ScrollThrottleSeconds = 0.5; // partial 滚动节流

    // ========== UI 数据绑定 ==========
    private readonly ObservableCollection<StreamingCaption> _captions = new();
    private readonly ObservableCollection<StreamingCaption> _refinedCaptions = new();
    private readonly ObservableCollection<Speaker> _speakers = new();
    private readonly ObservableCollection<MeetingContextOption> _meetingContexts = new();

    // ========== 转录状态 ==========
    private bool _isRecording = false;
    private DateTime _meetingStartTime;
    private DateTime? _meetingEndTime;
    private DispatcherTimer? _durationTimer;
    private int _segmentCount = 0;

    // Worker 的握手信号：模型首次加载要几秒，音频不能抢跑
    private TaskCompletionSource<bool>? _startedTcs;
    private TaskCompletionSource<bool>? _stoppedTcs;
    private TaskCompletionSource<bool>? _recordingStoppedTcs;

    // 麦克风和系统声音可以同时讲话，每个来源必须有独立的 partial/final 状态。
    private readonly Dictionary<string, CaptionStreamState> _captionStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Source, long UtteranceId), StreamingCaption>
        _captionByUtterance = new();
    private readonly Dictionary<StreamingCaption, CaptionTranslationState>
        _translationStates = new();
    private readonly Dictionary<long, StreamingCaption>
        _refinedCaptionBySegment = new();
    private DateTime _lastScrollTime = DateTime.MinValue;

    // ========== 音频捕获 ==========
    // 每个来源有独立采集/缓冲/重采样/发送泵。MainWindow.SendJsonAsync 内部
    // 有写锁，因此两条泵可以并发生成音频，管道消息仍然不会交叉损坏。
    private readonly List<AudioCapturePipeline> _audioPipelines = new();
    private CancellationTokenSource? _pumpCts;
    private readonly List<Task> _pumpTasks = new();
    private string _activeAudioSourceName = "我方";
    private bool _workerStreamingStarted;
    private string _requestedTranslationMode = "off";
    private bool _summaryEnabled;
    private bool _summaryReady;
    private bool _summaryServiceAvailable;
    private bool _postMeetingSummaryAvailable;
    private bool _isPostProcessing;
    private bool _refinedTranscriptReady;
    private long _activeMeetingId;
    private bool _showingRefinedTranscript;
    private CancellationTokenSource? _postProcessMonitorCts;
    private MeetingContextSnapshot _activeMeetingContext = new();

    private MainWindow? _mainWindow;

    public StreamingMeetingPage()
    {
        this.InitializeComponent();

        CaptionsList.ItemsSource = _captions;
        SpeakersList.ItemsSource = _speakers;
        CmbMeetingContext.ItemsSource = _meetingContexts;
        Loaded += Page_Loaded;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += UpdateDuration;

        _mainWindow = App.MainWindow as MainWindow;

        Debug.WriteLine("[StreamingMeeting] Page initialized");
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadMeetingContextsAsync();
    }

    private async Task ReloadMeetingContextsAsync()
    {
        if (_mainWindow == null || _isRecording) return;
        try
        {
            var pending = MeetingContextCoordinator.PendingPreparationId;
            _meetingContexts.Clear();
            _meetingContexts.Add(new MeetingContextOption());
            foreach (var preparation in await _mainWindow.GetMeetingPreparationsAsync())
            {
                _meetingContexts.Add(new MeetingContextOption
                {
                    PreparationId = preparation.PreparationId,
                    Display = preparation.Display
                });
            }
            CmbMeetingContext.SelectedItem = _meetingContexts.FirstOrDefault(
                item => item.PreparationId == pending) ?? _meetingContexts[0];
        }
        catch (Exception ex)
        {
            TxtMeetingContextStatus.Text = $"会议资料暂不可用：{ex.Message}";
            if (_meetingContexts.Count == 0) _meetingContexts.Add(new MeetingContextOption());
            CmbMeetingContext.SelectedIndex = 0;
        }
    }

    public async Task SelectMeetingContextAsync(long? preparationId)
    {
        MeetingContextCoordinator.SelectForNextMeeting(preparationId);
        await ReloadMeetingContextsAsync();
    }

    private async void CmbMeetingContext_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRecording || _mainWindow == null) return;
        var option = CmbMeetingContext.SelectedItem as MeetingContextOption;
        MeetingContextCoordinator.SelectForNextMeeting(option?.PreparationId);
        UpdateContextSwitchAvailability();
        try
        {
            var snapshot = await _mainWindow.GetMeetingContextSnapshotAsync(option?.PreparationId);
            TxtMeetingContextStatus.Text = snapshot.StatusDisplay;
        }
        catch (Exception ex)
        {
            TxtMeetingContextStatus.Text = $"上下文读取失败：{ex.Message}";
        }
    }

    private void UpdateContextSwitchAvailability(bool controlsEnabled = true)
    {
        bool hasPreparation =
            (CmbMeetingContext.SelectedItem as MeetingContextOption)?
                .PreparationId.HasValue == true;
        CmbMeetingContext.IsEnabled = controlsEnabled;
        ChkUseRagContext.IsEnabled =
            controlsEnabled && hasPreparation;
        ChkUseAsrHotwords.IsEnabled =
            controlsEnabled && hasPreparation;
    }

    private void BtnManageMeetingContext_Click(object sender, RoutedEventArgs e)
        => _mainWindow?.OpenMeetingPreparationPage();

    // ========== 按钮事件 ==========
    public void BtnStartMeeting_Click(object sender, RoutedEventArgs e) => _ = StartMeetingAsync();
    public void BtnStopMeeting_Click(object sender, RoutedEventArgs e) => _ = StopMeetingAsync();
    public void BtnExport_Click(object sender, RoutedEventArgs e) => _ = ExportTranscriptAsync();
    public void BtnClear_Click(object sender, RoutedEventArgs e) => ClearAll();
    public void BtnSummarizeNow_Click(object sender, RoutedEventArgs e)
        => _ = RequestSummaryNowAsync();
    public void BtnCopyCaptions_Click(object sender, RoutedEventArgs e)
    {
        var text = GenerateVisibleCaptionContent();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("没有可复制的字幕");
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("字幕已复制到剪贴板");
    }

    public void BtnShowLiveTranscript_Click(object sender, RoutedEventArgs e)
        => ShowTranscriptVersion(false);

    public void BtnShowFinalTranscript_Click(object sender, RoutedEventArgs e)
        => ShowTranscriptVersion(true);
    public void BtnRetryPostProcess_Click(object sender, RoutedEventArgs e)
        => _ = RetryPostProcessingAsync();
    public void BtnLoadLatestMeeting_Click(object sender, RoutedEventArgs e)
        => _ = LoadLatestMeetingAsync();

    private IEnumerable<StreamingCaption> VisibleCaptions =>
        _showingRefinedTranscript ? _refinedCaptions : _captions;

    private void ShowTranscriptVersion(bool refined)
    {
        if (refined && _refinedCaptions.Count == 0) return;
        _showingRefinedTranscript = refined;
        CaptionsList.ItemsSource =
            refined ? _refinedCaptions : _captions;
        TxtCaptionTitle.Text = refined
            ? "✨ Whisper 最终稿"
            : "📝 Sherpa 实时稿";
        BtnShowLiveTranscript.IsEnabled = refined;
        BtnShowFinalTranscript.IsEnabled =
            !refined && _refinedCaptions.Count > 0;
        ScrollToLatest(force: true);
    }

    private async Task RetryPostProcessingAsync()
    {
        if (_mainWindow == null ||
            _activeMeetingId <= 0 ||
            _isRecording ||
            _isPostProcessing)
        {
            return;
        }

        try
        {
            _isPostProcessing = true;
            _postMeetingSummaryAvailable = false;
            _summaryReady = false;
            _refinedTranscriptReady = false;
            _refinedCaptions.Clear();
            _refinedCaptionBySegment.Clear();
            ShowTranscriptVersion(false);

            BtnRetryPostProcess.Visibility = Visibility.Collapsed;
            BtnStartMeeting.IsEnabled = false;
            BtnClear.IsEnabled = false;
            BtnSummarizeNow.IsEnabled = false;
            PostProcessProgress.Value = 0;
            TxtPostProcessPercent.Text = "0%";
            TxtPostProcessTitle.Text = "正在重新生成会议最终稿";
            TxtPostProcessMessage.Text = "正在读取已保存的会议录音…";
            SetStatus("Processing · 正在重试最终稿");

            await _mainWindow.EnsurePipeAsync();
            _mainWindow.StreamingMessageHandler =
                OnStreamingMessageReceived;
            await _mainWindow.SendJsonAsync(
                "{\"type\":\"retry_meeting_postprocess\"," +
                $"\"meeting_id\":{_activeMeetingId}," +
                $"\"summary_enabled\":{(_summaryEnabled ? "true" : "false")}" +
                "}\n");
            StartPostProcessDatabaseMonitor();
        }
        catch (Exception ex)
        {
            CompletePostProcessing(false, ex.Message);
        }
    }

    // 会后处理的结果本来就以 SQLite 为准，命名管道只用于即时刷新界面。
    // 若 Host 的管道读取循环中断，Worker 仍会继续写数据库；这里直接轮询
    // transcription_run，避免页面永远停在“正在封存会议录音”或 0%。
    private void StartPostProcessDatabaseMonitor()
    {
        StopPostProcessDatabaseMonitor();
        if (_activeMeetingId <= 0 || !_isPostProcessing)
        {
            return;
        }

        _postProcessMonitorCts = new CancellationTokenSource();
        _ = MonitorPostProcessDatabaseAsync(
            _activeMeetingId,
            _postProcessMonitorCts.Token);
    }

    private void StopPostProcessDatabaseMonitor()
    {
        var cts = _postProcessMonitorCts;
        _postProcessMonitorCts = null;
        if (cts == null)
        {
            return;
        }

        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private async Task MonitorPostProcessDatabaseAsync(
        long meetingId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               _isPostProcessing &&
               _activeMeetingId == meetingId)
        {
            try
            {
                var snapshot = await ReadPostProcessSnapshotAsync(
                    meetingId,
                    cancellationToken);
                if (snapshot.HasValue)
                {
                    var (status, progress, error) = snapshot.Value;
                    if (status == "complete")
                    {
                        // 正常管道会在数据库提交后紧接着发 complete。给它一个
                        // 很短的优先窗口，避免数据库恢复和管道 segment 回包重复。
                        await Task.Delay(
                            TimeSpan.FromSeconds(2),
                            cancellationToken);
                        if (!_isPostProcessing ||
                            _activeMeetingId != meetingId)
                        {
                            return;
                        }

                        // 管道失联时不会收到 segment/complete 消息，直接从
                        // SQLite 恢复刚生成的 Whisper 最终稿和最终会议纪要。
                        _isPostProcessing = false;
                        StopPostProcessDatabaseMonitor();
                        await LoadLatestMeetingAsync();
                        return;
                    }

                    if (status == "failed")
                    {
                        CompletePostProcessing(
                            false,
                            string.IsNullOrWhiteSpace(error)
                                ? "Worker 生成会议最终稿失败"
                                : error);
                        return;
                    }

                    ApplyDatabasePostProcessProgress(status, progress);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 数据库短暂忙碌不应终止恢复机制，下次轮询继续尝试。
                Debug.WriteLine(
                    $"[StreamingMeeting] Post-process DB monitor: {ex.Message}");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<(string Status, int Progress, string Error)?>
        ReadPostProcessSnapshotAsync(
            long meetingId,
            CancellationToken cancellationToken)
    {
        var databasePath = System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MeetingAI",
            "meeting.db");
        if (!System.IO.File.Exists(databasePath))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        await using var connection =
            new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status,progress,COALESCE(error_text,'') " +
            "FROM transcription_run WHERE meeting_id=$meetingId " +
            "ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            Math.Clamp(reader.GetInt32(1), 0, 100),
            reader.GetString(2));
    }

    private void ApplyDatabasePostProcessProgress(
        string status,
        int progress)
    {
        PostProcessPanel.Visibility = Visibility.Visible;
        PostProcessProgress.Value = progress;
        TxtPostProcessPercent.Text = $"{progress}%";
        TxtPostProcessTitle.Text = status switch
        {
            "transcribing" => "Whisper 正在生成最终稿",
            "translating" => "正在生成最终译文",
            "summarizing" => "Granite 正在生成最终会议纪要",
            "saving" => "正在保存最终会议成果",
            _ => "正在准备会议最终稿"
        };
        TxtPostProcessMessage.Text = status switch
        {
            "transcribing" =>
                "录音已封存，OpenVINO Whisper 正在离线精修…",
            "translating" =>
                "Whisper 最终稿已完成，正在生成最终译文…",
            "summarizing" =>
                "正在根据 Whisper 最终稿生成最终会议纪要…",
            "saving" =>
                "正在把最终稿和会议纪要保存到本地数据库…",
            _ => "录音已封存，正在启动会后处理…"
        };
        SetStatus($"Processing · {TxtPostProcessMessage.Text}");
    }

    private async Task LoadLatestMeetingAsync()
    {
        if (_isRecording || _isPostProcessing) return;

        BtnLoadLatestMeeting.IsEnabled = false;
        try
        {
            var databasePath = System.IO.Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "MeetingAI",
                "meeting.db");
            if (!System.IO.File.Exists(databasePath))
            {
                SetStatus("还没有本地会议记录");
                return;
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            await using var connection =
                new SqliteConnection(connectionString);
            await connection.OpenAsync();

            long meetingId;
            string title;
            DateTime startedAt;
            DateTime endedAt;
            await using (var meetingCommand = connection.CreateCommand())
            {
                meetingCommand.CommandText =
                    "SELECT id,COALESCE(title,'Streaming Meeting')," +
                    "started_at_utc,ended_at_utc " +
                    "FROM meeting " +
                    "WHERE ext_source='streaming' " +
                    "  AND ended_at_utc IS NOT NULL " +
                    "ORDER BY id DESC LIMIT 1;";
                await using var reader =
                    await meetingCommand.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    SetStatus("还没有已结束的流式会议");
                    return;
                }
                meetingId = reader.GetInt64(0);
                title = reader.GetString(1);
                startedAt = ParseDatabaseTime(
                    reader.IsDBNull(2) ? null : reader.GetString(2));
                endedAt = ParseDatabaseTime(
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }

            long canonicalRunId = 0;
            string latestRunStatus = "";
            int latestRunProgress = 0;
            string latestRunError = "";
            await using (var runCommand = connection.CreateCommand())
            {
                runCommand.CommandText =
                    "SELECT id,status,progress,COALESCE(error_text,'')," +
                    "is_canonical FROM transcription_run " +
                    "WHERE meeting_id=$meetingId " +
                    "ORDER BY id DESC;";
                runCommand.Parameters.AddWithValue(
                    "$meetingId",
                    meetingId);
                await using var reader =
                    await runCommand.ExecuteReaderAsync();
                bool first = true;
                while (await reader.ReadAsync())
                {
                    if (first)
                    {
                        latestRunStatus = reader.GetString(1);
                        latestRunProgress = reader.GetInt32(2);
                        latestRunError = reader.GetString(3);
                        first = false;
                    }
                    if (canonicalRunId == 0 &&
                        reader.GetInt32(4) == 1)
                    {
                        canonicalRunId = reader.GetInt64(0);
                    }
                }
            }

            var liveCaptions = await LoadStoredCaptionsAsync(
                connection,
                meetingId,
                0,
                "asr_normalized");
            var finalCaptions = canonicalRunId > 0
                ? await LoadStoredCaptionsAsync(
                    connection,
                    meetingId,
                    canonicalRunId,
                    "asr_whisper_final")
                : new List<StreamingCaption>();

            string quickSummary = "";
            string detailedSummary = "";
            await using (var summaryCommand = connection.CreateCommand())
            {
                summaryCommand.CommandText =
                    "SELECT summary_text,is_final,summary_kind " +
                    "FROM meeting_summary WHERE meeting_id=$meetingId " +
                    "ORDER BY revision_no;";
                summaryCommand.Parameters.AddWithValue(
                    "$meetingId",
                    meetingId);
                await using var reader =
                    await summaryCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string summary = reader.GetString(0);
                    bool isFinal = reader.GetInt32(1) == 1;
                    string kind = reader.IsDBNull(2)
                        ? ""
                        : reader.GetString(2);
                    if (isFinal ||
                        kind is "final_minutes" or "detailed_draft")
                    {
                        detailedSummary = summary;
                    }
                    else
                    {
                        quickSummary = summary;
                    }
                }
            }

            ClearAll();
            _activeMeetingId = meetingId;
            _meetingStartTime = startedAt;
            _meetingEndTime = endedAt;
            foreach (var caption in liveCaptions)
            {
                _captions.Add(caption);
            }
            foreach (var caption in finalCaptions)
            {
                _refinedCaptions.Add(caption);
                _refinedCaptionBySegment[caption.SegmentId] =
                    caption;
            }
            _refinedTranscriptReady =
                _refinedCaptions.Count > 0;
            _segmentCount = _refinedTranscriptReady
                ? _refinedCaptions.Count
                : _captions.Count;
            TxtSegmentCount.Text = _segmentCount.ToString();
            UpdateDuration(null, EventArgs.Empty);
            TxtMeetingSummary.Text = string.IsNullOrWhiteSpace(quickSummary)
                ? "这场会议没有已保存的实时速览。"
                : quickSummary;
            TxtDetailedMeetingSummary.Text =
                string.IsNullOrWhiteSpace(detailedSummary)
                    ? "这场会议还没有最终会议纪要。"
                    : detailedSummary;
            TxtSummaryUpdated.Text =
                string.IsNullOrWhiteSpace(quickSummary) &&
                string.IsNullOrWhiteSpace(detailedSummary)
                    ? ""
                    : "已从本地数据库恢复";
            TxtMeetingContextStatus.Text =
                $"已打开：{title} · Meeting #{meetingId}";

            BtnExport.IsEnabled =
                _captions.Count > 0 || _refinedCaptions.Count > 0;
            BtnClear.IsEnabled = BtnExport.IsEnabled;
            BtnShowFinalTranscript.IsEnabled =
                _refinedTranscriptReady;

            if (_refinedTranscriptReady)
            {
                ShowTranscriptVersion(true);
            }
            else
            {
                ShowTranscriptVersion(false);
            }

            bool interrupted =
                string.IsNullOrEmpty(latestRunStatus) ||
                latestRunStatus is not "complete";
            if (interrupted)
            {
                PostProcessPanel.Visibility = Visibility.Visible;
                PostProcessProgress.Value =
                    Math.Clamp(latestRunProgress, 0, 100);
                TxtPostProcessPercent.Text =
                    $"{Math.Clamp(latestRunProgress, 0, 100)}%";
                TxtPostProcessTitle.Text =
                    latestRunStatus == "failed"
                        ? "最近一次最终稿生成失败"
                        : "最终稿尚未完成";
                TxtPostProcessMessage.Text =
                    string.IsNullOrWhiteSpace(latestRunError)
                        ? "录音仍保存在本地，可以继续生成最终稿。"
                        : latestRunError;
                BtnRetryPostProcess.Visibility =
                    Visibility.Visible;
            }
            else
            {
                PostProcessPanel.Visibility = Visibility.Visible;
                PostProcessProgress.Value = 100;
                TxtPostProcessPercent.Text = "100%";
                TxtPostProcessTitle.Text = "会议最终稿已完成";
                TxtPostProcessMessage.Text =
                    "已从本地数据库恢复 Whisper 最终稿及会议纪要。";
                BtnRetryPostProcess.Visibility =
                    Visibility.Collapsed;
            }

            _summaryEnabled = ChkLiveSummary.IsChecked == true;
            _postMeetingSummaryAvailable = false;
            BtnSummarizeNow.IsEnabled = false;
            SetStatus(
                _refinedTranscriptReady
                    ? "History · Whisper 最终稿"
                    : "History · Sherpa 实时稿");
        }
        catch (Exception ex)
        {
            SetStatus($"读取最近会议失败: {ex.Message}");
        }
        finally
        {
            BtnLoadLatestMeeting.IsEnabled = true;
        }
    }

    private async Task<List<StreamingCaption>>
        LoadStoredCaptionsAsync(
            SqliteConnection connection,
            long meetingId,
            long transcriptionRunId,
            string stage)
    {
        var captions = new List<StreamingCaption>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.id,st.type,s.start_ms,r.text_final," +
            "(SELECT tr.text_final FROM revision tr " +
            " WHERE tr.segment_id=s.id " +
            "   AND tr.stage LIKE 'translation_%' " +
            " ORDER BY tr.id DESC LIMIT 1) " +
            "FROM segment s " +
            "JOIN stream st ON st.id=s.stream_id " +
            "JOIN revision r ON r.segment_id=s.id " +
            "WHERE s.meeting_id=$meetingId AND r.stage=$stage " +
            (transcriptionRunId > 0
                ? "AND s.transcription_run_id=$runId "
                : "AND s.transcription_run_id IS NULL ") +
            "ORDER BY s.start_ms,s.id;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$stage", stage);
        if (transcriptionRunId > 0)
        {
            command.Parameters.AddWithValue(
                "$runId",
                transcriptionRunId);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            long segmentId = reader.GetInt64(0);
            string source = reader.GetString(1);
            long startMs = reader.GetInt64(2);
            var timestamp = TimeSpan.FromMilliseconds(
                Math.Max(0, startMs));
            captions.Add(new StreamingCaption
            {
                SegmentId = segmentId,
                StartMs = startMs,
                SpeakerName = GetSourceDisplayName(source),
                SpeakerColor = GetSourceColor(source),
                Timestamp = timestamp.ToString(@"hh\:mm\:ss"),
                Text = reader.GetString(3),
                TranslatedText = reader.IsDBNull(4)
                    ? ""
                    : reader.GetString(4),
                TextOpacity = 1.0,
                TranslationOpacity = 1.0
            });
        }
        return captions;
    }

    private static DateTime ParseDatabaseTime(string? value)
    {
        if (DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            return utc.ToLocalTime();
        }
        return DateTime.Now;
    }

    private async Task RequestSummaryNowAsync()
    {
        if ((!_isRecording && !_postMeetingSummaryAvailable) ||
            !_summaryEnabled || !_summaryReady ||
            _mainWindow == null)
        {
            SetSummaryStatus("当前没有可用于生成摘要的会议");
            return;
        }

        await _mainWindow.EnsurePipeAsync();
        _mainWindow.StreamingMessageHandler = OnStreamingMessageReceived;
        BtnSummarizeNow.IsEnabled = false;
        SetSummaryStatus(
            _isRecording
                ? "正在生成自适应详细摘要…"
                : "正在根据本场会议的完整数据库记录重新生成详细摘要…");
        await _mainWindow.SendJsonAsync(
            "{\"type\":\"request_meeting_summary\",\"meeting_id\":" +
            _activeMeetingId + "}\n");
    }

    // ========== 会议控制 ==========
    private async Task StartMeetingAsync()
    {
        if (_isRecording || _isPostProcessing) return;

        try
        {
            if (_mainWindow == null)
            {
                await ShowErrorAsync("无法访问 MainWindow");
                return;
            }

            _requestedTranslationMode = GetSelectedTranslationMode();
            _summaryEnabled = ChkLiveSummary.IsChecked == true;
            string readinessError =
                _mainWindow.GetStreamingModelReadinessError(
                    _requestedTranslationMode,
                    _summaryEnabled);
            if (!string.IsNullOrWhiteSpace(readinessError))
            {
                await ShowErrorAsync(readinessError);
                return;
            }

            BtnStartMeeting.IsEnabled = false;
            CmbAudioSource.IsEnabled = false;
            CmbTranslationMode.IsEnabled = false;
            UpdateContextSwitchAvailability(false);
            BtnManageMeetingContext.IsEnabled = false;
            ChkLiveSummary.IsEnabled = false;
            BtnSummarizeNow.IsEnabled = false;
            _postMeetingSummaryAvailable = false;
            _activeMeetingId = 0;
            _refinedTranscriptReady = false;
            _refinedCaptions.Clear();
            _refinedCaptionBySegment.Clear();
            _captions.Clear();
            _segmentCount = 0;
            ShowTranscriptVersion(false);
            PostProcessPanel.Visibility = Visibility.Collapsed;
            BtnRetryPostProcess.Visibility = Visibility.Collapsed;
            PostProcessProgress.Value = 0;
            TxtPostProcessPercent.Text = "0%";
            SetStatus("Connecting…");

            await _mainWindow.EnsurePipeAsync();

            // 处理器必须在发命令之前挂上，否则会漏掉 streaming_started
            _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainWindow.StreamingMessageHandler = OnStreamingMessageReceived;

            _summaryReady = false;
            _summaryServiceAvailable = false;
            SetTranslationStatus(
                _requestedTranslationMode == "off" ? "关闭" : "模型准备中…");
            SetSummaryStatus(
                _summaryEnabled ? "检查本地 Granite 摘要服务…" : "已关闭");
            TxtMeetingSummary.Text = _summaryEnabled
                ? "正在启动会议，使用 Startup 已加载的 Granite。"
                : "本场会议未启用自动摘要。";
            TxtDetailedMeetingSummary.Text = _summaryEnabled
                ? "会议进行中可随时点击“生成详细摘要”。"
                : "本场会议未启用自动摘要。";
            TxtSummaryUpdated.Text = "";

            var selectedContext = CmbMeetingContext.SelectedItem as MeetingContextOption;
            _activeMeetingContext = await _mainWindow.GetMeetingContextSnapshotAsync(
                selectedContext?.PreparationId);

            var startCmd = new StartStreamingCommand
            {
                sample_rate = TargetSampleRate,
                source = GetSelectedSourceMode(),
                translation_mode = _requestedTranslationMode,
                summary_enabled = _summaryEnabled,
                rag_context_enabled =
                    _activeMeetingContext.HasPreparation &&
                    ChkUseRagContext.IsChecked == true,
                asr_hotwords_enabled =
                    _activeMeetingContext.HasPreparation &&
                    ChkUseAsrHotwords.IsChecked == true,
                preparation_id = _activeMeetingContext.PreparationId,
                context_title = _activeMeetingContext.Title,
                context_document_ids = _activeMeetingContext.DocumentIds,
                hotwords = (ChkUseAsrHotwords.IsChecked == true
                    ? _activeMeetingContext.Hotwords
                    : new List<HotwordCandidate>())
                    .Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Text))
                    .OrderByDescending(item => item.Score)
                    .Take(100)
                    .Select(item => new StreamingHotword { text = item.Text.Trim(), score = item.Score })
                    .ToList()
            };
            await _mainWindow.SendJsonAsync(
                JsonSerializer.Serialize(startCmd, Contracts.AppJsonContext.Utf8.StartStreamingCommand) + "\n");

            SetStatus("Starting transcription…");

            // 模型已在 Startup 加载；这里只等待会话、录音和数据库准备。
            var ready = await Task.WhenAny(
                _startedTcs.Task,
                Task.Delay(TimeSpan.FromSeconds(30)));
            if (ready != _startedTcs.Task)
            {
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                CmbAudioSource.IsEnabled = true;
                CmbTranslationMode.IsEnabled = true;
                UpdateContextSwitchAvailability();
                BtnManageMeetingContext.IsEnabled = true;
                ChkLiveSummary.IsEnabled = true;
                SetStatus("Failed");
                await ShowErrorAsync("等待 Worker 启动流式会话超时（30 秒）。请检查 Startup 模型状态和日志。");
                return;
            }
            if (!_startedTcs.Task.Result)
            {
                // 具体错误已由 streaming_error 分支写进状态栏
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                CmbAudioSource.IsEnabled = true;
                CmbTranslationMode.IsEnabled = true;
                UpdateContextSwitchAvailability();
                BtnManageMeetingContext.IsEnabled = true;
                ChkLiveSummary.IsEnabled = true;
                return;
            }
            _workerStreamingStarted = true;

            if (!InitializeAudioCapture())
            {
                await StopWorkerStreamingSilentlyAsync();
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                CmbAudioSource.IsEnabled = true;
                CmbTranslationMode.IsEnabled = true;
                UpdateContextSwitchAvailability();
                BtnManageMeetingContext.IsEnabled = true;
                ChkLiveSummary.IsEnabled = true;
                SetStatus("Failed");
                await ShowErrorAsync("音频源初始化失败，请检查所选麦克风或系统播放设备。");
                return;
            }

            _isRecording = true;
            _meetingStartTime = DateTime.Now;
            _meetingEndTime = null;
            _segmentCount = 0;

            // 上一场会议可能留着没封口的段落，新会议不该续写它
            _captionStates.Clear();
            _captionByUtterance.Clear();
            _translationStates.Clear();

            foreach (var pipeline in _audioPipelines)
            {
                pipeline.Capture.StartRecording();
            }

            _pumpCts = new CancellationTokenSource();
            _pumpTasks.Clear();
            foreach (var pipeline in _audioPipelines)
            {
                _pumpTasks.Add(Task.Run(
                    () => AudioPumpAsync(pipeline, _pumpCts.Token)));
            }

            _durationTimer?.Start();

            BtnStopMeeting.IsEnabled = true;
            BtnSummarizeNow.IsEnabled = _summaryEnabled && _summaryReady;
            BtnExport.IsEnabled = false;
            BtnClear.IsEnabled = false;
            SetStatus($"Listening · {_activeAudioSourceName}");

            Debug.WriteLine("[StreamingMeeting] Meeting started");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Start failed: {ex.Message}");
            _isRecording = false;
            _durationTimer?.Stop();
            await StopWorkerStreamingSilentlyAsync();
            CleanupResources();
            BtnStartMeeting.IsEnabled = true;
            CmbAudioSource.IsEnabled = true;
            CmbTranslationMode.IsEnabled = true;
            UpdateContextSwitchAvailability();
            BtnManageMeetingContext.IsEnabled = true;
            ChkLiveSummary.IsEnabled = true;
            SetStatus("Failed");
            await ShowErrorAsync($"启动失败：{ex.Message}");
        }
    }

    private async Task StopMeetingAsync()
    {
        if (!_isRecording) return;

        try
        {
            _isRecording = false;
            _isPostProcessing = true;
            _meetingEndTime = DateTime.Now;
            _durationTimer?.Stop();
            UpdateDuration(null, EventArgs.Empty);
            BtnStopMeeting.IsEnabled = false;
            PostProcessPanel.Visibility = Visibility.Visible;
            PostProcessProgress.Value = 0;
            TxtPostProcessPercent.Text = "0%";
            TxtPostProcessTitle.Text = "正在封存会议录音";
            TxtPostProcessMessage.Text =
                "正在安全保存两路录音，随后启动 OpenVINO Whisper…";
            SetStatus("正在停止录音并封存实时稿…");

            // 先停采集和发送泵，再发 stop，避免 stop 之后还有音频包排在后面
            foreach (var pipeline in _audioPipelines)
            {
                try { pipeline.Capture.StopRecording(); } catch { }
            }

            _pumpCts?.Cancel();
            if (_pumpTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(_pumpTasks)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { }
                _pumpTasks.Clear();
            }

            if (_mainWindow != null)
            {
                _recordingStoppedTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stoppedTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var stopCmd = new StopStreamingCommand();
                await _mainWindow.SendJsonAsync(
                    JsonSerializer.Serialize(stopCmd, Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n");
                // 停止命令已经成功交给 Worker。即使后续回执超时，也不能
                // 再补发第二条 stop，否则第一条完成后第二条会得到
                // “流式会话未启动”，掩盖真正的处理状态。
                _workerStreamingStarted = false;

                // 只等待录音封存完成。Whisper/翻译/最终纪要会继续在后台
                // 处理并通过当前消息处理器逐步更新页面。
                var completed = await Task.WhenAny(
                    _recordingStoppedTcs.Task,
                    Task.Delay(TimeSpan.FromSeconds(30)));
                if (completed != _recordingStoppedTcs.Task ||
                    !await _recordingStoppedTcs.Task)
                {
                    throw new TimeoutException("等待 Worker 封存会议录音超时");
                }
            }

            CloseAllParagraphs(); // 两个来源的 final 都已收完

            CleanupResources();

            // 极短或空白录音可能在 UI 恢复调度前就已经完成/失败。
            // 此时完成事件已经设置好了最终状态，不要再把页面改回“处理中”。
            if (!_isPostProcessing)
            {
                return;
            }

            BtnStartMeeting.IsEnabled = false;
            _postMeetingSummaryAvailable = false;
            _summaryReady = false;
            BtnSummarizeNow.IsEnabled = false;
            BtnExport.IsEnabled = _captions.Count > 0;
            BtnClear.IsEnabled = false;
            SetStatus("Processing · 正在生成 Whisper 最终稿");
            SetSummaryStatus(
                _summaryEnabled
                    ? "等待 Whisper 最终稿完成后生成最终会议纪要"
                    : "本场会议未启用摘要");
            StartPostProcessDatabaseMonitor();

            Debug.WriteLine(
                "[StreamingMeeting] Recording stopped; post-processing started");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Stop failed: {ex.Message}");
            await StopWorkerStreamingSilentlyAsync();
            CleanupResources();
            BtnStartMeeting.IsEnabled = true;
            CmbAudioSource.IsEnabled = true;
            CmbTranslationMode.IsEnabled = true;
            UpdateContextSwitchAvailability();
            BtnManageMeetingContext.IsEnabled = true;
            ChkLiveSummary.IsEnabled = true;
            BtnSummarizeNow.IsEnabled = false;
            _summaryReady = false;
            _summaryServiceAvailable = false;
            _postMeetingSummaryAvailable = false;
            _isPostProcessing = false;
            PostProcessPanel.Visibility = Visibility.Collapsed;
            SetStatus("Idle");
            await ShowErrorAsync($"停止失败：{ex.Message}");
        }
    }

    // ========== 音频捕获初始化 ==========
    private bool InitializeAudioCapture()
    {
        try
        {
            DisposeAudioPipelines();

            string mode = GetSelectedSourceMode();
            if (mode is MicrophoneSource or "both")
            {
                _audioPipelines.Add(CreateAudioPipeline(MicrophoneSource));
            }
            if (mode is SystemSource or "both")
            {
                _audioPipelines.Add(CreateAudioPipeline(SystemSource));
            }

            _activeAudioSourceName = mode switch
            {
                MicrophoneSource => "我方（麦克风）",
                SystemSource => "对方（会议音频）",
                _ => "我方 + 对方"
            };

            return _audioPipelines.Count > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Capture init failed: {ex.Message}");
            DisposeAudioPipelines();
            return false;
        }
    }

    private AudioCapturePipeline CreateAudioPipeline(string source)
    {
        IWaveIn capture;
        string displayName;
        bool fillSilence;

        if (source == SystemSource)
        {
            // 捕获默认播放设备的系统内部声音。Loopback 在完全无声时不一定
            // 回调，发送泵会按实时节拍补静音以触发 Sherpa endpoint。
            capture = new WasapiLoopbackCapture();
            displayName = "对方（会议音频）";
            fillSilence = true;
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Communications);
            capture = new WasapiCapture(device);
            displayName = $"我方（{device.FriendlyName}）";
            fillSilence = false;
        }

        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        // 每条链路独立复用自己的降混和重采样器，避免两个设备时钟/格式互相干扰。
        ISampleProvider chain = buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels == 2)
        {
            chain = new StereoToMonoSampleProvider(chain);
        }
        else if (chain.WaveFormat.Channels > 2)
        {
            chain = new MultiplexingSampleProvider(new[] { chain }, 1);
        }

        if (chain.WaveFormat.SampleRate != TargetSampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, TargetSampleRate);
        }

        var pipeline = new AudioCapturePipeline(
            source,
            displayName,
            fillSilence,
            capture,
            buffer,
            chain);

        pipeline.DataAvailableHandler = (_, e) =>
        {
            try
            {
                if (_isRecording && e.BytesRecorded > 0)
                {
                    pipeline.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[StreamingMeeting] {pipeline.Source} buffer append failed: " +
                    ex.Message);
            }
        };

        pipeline.RecordingStoppedHandler = (_, e) =>
        {
            if (e.Exception != null)
            {
                Debug.WriteLine(
                    $"[StreamingMeeting] {pipeline.Source} stopped with error: " +
                    e.Exception.Message);
                DispatcherQueue.TryEnqueue(
                    () => SetStatus(
                        $"{GetSourceDisplayName(pipeline.Source)}音频错误: " +
                        e.Exception.Message));
            }
            else
            {
                Debug.WriteLine(
                    $"[StreamingMeeting] {pipeline.Source} capture stopped");
            }
        };

        capture.DataAvailable += pipeline.DataAvailableHandler;
        capture.RecordingStopped += pipeline.RecordingStoppedHandler;

        Debug.WriteLine(
            $"[StreamingMeeting] Capture {source}: {displayName}, " +
            $"{capture.WaveFormat} -> {TargetSampleRate}Hz mono");

        return pipeline;
    }

    // ========== 音频发送泵（每个来源一条，管道写入由 MainWindow 串行化）==========
    private async Task AudioPumpAsync(
        AudioCapturePipeline pipeline,
        CancellationToken ct)
    {
        var samples = new float[ChunkSamples];
        var pcm = new byte[ChunkSamples * 2];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int got = 0;
                var chunkTimer = Stopwatch.StartNew();
                while (got < ChunkSamples && !ct.IsCancellationRequested)
                {
                    int n = pipeline.Resampled.Read(
                        samples,
                        got,
                        ChunkSamples - got);
                    if (n <= 0)
                    {
                        // WASAPI Loopback 在系统完全无声时可能不触发 DataAvailable。
                        // 按实时节拍补静音，让 Sherpa 能收到尾部静音并触发 endpoint。
                        if (pipeline.FillSilence &&
                            chunkTimer.ElapsedMilliseconds >= 100)
                        {
                            Array.Clear(samples, got, ChunkSamples - got);
                            got = ChunkSamples;
                            break;
                        }

                        await Task.Delay(10, ct).ConfigureAwait(false);
                        continue;
                    }
                    got += n;
                }

                if (got <= 0 || ct.IsCancellationRequested) break;

                for (int i = 0; i < got; i++)
                {
                    short s = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                    pcm[i * 2] = (byte)(s & 0xFF);
                    pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }

                var cmd = new StreamingAudioCommand
                {
                    source = pipeline.Source,
                    audio_data = Convert.ToBase64String(pcm, 0, got * 2),
                    sample_rate = TargetSampleRate,
                    is_end = false
                };

                // 必须走 Utf8 上下文（UnsafeRelaxedJsonEscaping）：默认编码器会把 base64
                // 字母表里的 '+' 转义成 +，Worker 的 Base64Decode 遇到反斜杠就停止解码。
                var json = JsonSerializer.Serialize(cmd, Contracts.AppJsonContext.Utf8.StreamingAudioCommand) + "\n";
                await (_mainWindow?.SendJsonAsync(json) ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[StreamingMeeting] {pipeline.Source} audio pump failed: " +
                ex.Message);
            DispatcherQueue.TryEnqueue(
                () => SetStatus(
                    $"{GetSourceDisplayName(pipeline.Source)}音频错误: {ex.Message}"));
        }
    }

    // ========== Worker 消息处理 ==========
    private void OnStreamingMessageReceived(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return;

            string type = typeElement.GetString() ?? "";
            string Text() => root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            string Source()
            {
                string source = root.TryGetProperty("source", out var s)
                    ? s.GetString() ?? MicrophoneSource
                    : MicrophoneSource;
                return source == SystemSource ? SystemSource : MicrophoneSource;
            }
            string Message() => root.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
            string SummaryKind() => root.TryGetProperty(
                    "summary_kind",
                    out var kind)
                ? kind.GetString() ?? "quick"
                : "quick";
            long UtteranceId() => root.TryGetProperty("utterance_id", out var id)
                && id.TryGetInt64(out long value)
                ? value
                : 0;
            long SegmentId() => root.TryGetProperty("segment_id", out var id)
                && id.TryGetInt64(out long value)
                ? value
                : 0;
            long StartMs() => root.TryGetProperty("start_ms", out var start)
                && start.TryGetInt64(out long value)
                ? value
                : 0;

            switch (type)
            {
                case "streaming_started":
                {
                    string actualMode = root.TryGetProperty(
                            "translation_mode",
                            out var modeElement)
                        ? modeElement.GetString() ?? "off"
                        : "off";
                    bool actualSummaryEnabled =
                        root.TryGetProperty(
                            "summary_enabled",
                            out var summaryElement) &&
                        summaryElement.ValueKind == JsonValueKind.True;
                    int actualHotwordCount =
                        root.TryGetProperty(
                            "hotword_count",
                            out var hotwordElement) &&
                        hotwordElement.TryGetInt32(out int count)
                            ? count
                            : 0;
                    bool actualRagEnabled =
                        root.TryGetProperty(
                            "rag_enabled",
                            out var ragElement) &&
                        ragElement.ValueKind == JsonValueKind.True;
                    bool actualAsrHotwordsEnabled =
                        root.TryGetProperty(
                            "asr_hotwords_enabled",
                            out var asrElement) &&
                        asrElement.ValueKind == JsonValueKind.True;
                    long meetingId =
                        root.TryGetProperty(
                            "meeting_id",
                            out var meetingElement) &&
                        meetingElement.TryGetInt64(out long parsedMeetingId)
                            ? parsedMeetingId
                            : 0;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _activeMeetingId = meetingId;
                        if (_requestedTranslationMode == "off")
                        {
                            SetTranslationStatus("关闭");
                        }
                        else if (actualMode == "off")
                        {
                            SetTranslationStatus(
                                "未启用（Worker 未加载翻译模块）");
                        }
                        else
                        {
                            SetTranslationStatus("已启用 · 完全离线");
                        }

                        _summaryEnabled =
                            _summaryEnabled && actualSummaryEnabled;
                        BtnSummarizeNow.IsEnabled = false;
                        if (!_summaryEnabled)
                        {
                            SetSummaryStatus("本场会议未启用摘要");
                        }

                        TxtMeetingContextStatus.Text =
                            _activeMeetingContext.HasPreparation
                                ? $"{_activeMeetingContext.Title} · " +
                                  $"{_activeMeetingContext.DocumentIds.Count}/5 份资料 · " +
                                  (actualRagEnabled
                                      ? "RAG 已启用"
                                      : "RAG 已关闭") +
                                  " · " +
                                  (actualAsrHotwordsEnabled
                                      ? $"{actualHotwordCount} 个术语用于识别"
                                      : "术语增强已关闭")
                                : "通用模式 · 未绑定会议资料";
                    });
                    _startedTcs?.TrySetResult(true);
                    break;
                }

                // Worker 在加载模型的各阶段回传进度，否则界面只能干等
                case "info":
                {
                    var msg = Message();
                    Debug.WriteLine($"[StreamingMeeting] {msg}");
                    if (msg.StartsWith("[Sherpa]") ||
                        msg.StartsWith("[Translation]"))
                    {
                        DispatcherQueue.TryEnqueue(
                            () => SetStatus(
                                msg.Replace("[Sherpa] ", "")
                                   .Replace("[Translation] ", "")));
                    }
                    break;
                }

                case "streaming_stopped":
                    _stoppedTcs?.TrySetResult(true);
                    break;

                case "streaming_recording_stopped":
                    _recordingStoppedTcs?.TrySetResult(true);
                    break;

                case "streaming_stop_progress":
                {
                    string message = Message();
                    DispatcherQueue.TryEnqueue(() => SetStatus(message));
                    break;
                }

                case "streaming_postprocess_status":
                {
                    string state = root.TryGetProperty(
                            "state",
                            out var stateElement)
                        ? stateElement.GetString() ?? ""
                        : "";
                    int progress = root.TryGetProperty(
                            "progress",
                            out var progressElement) &&
                        progressElement.TryGetInt32(out int parsedProgress)
                            ? Math.Clamp(parsedProgress, 0, 100)
                            : 0;
                    string message = Message();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        PostProcessPanel.Visibility = Visibility.Visible;
                        PostProcessProgress.Value = progress;
                        TxtPostProcessPercent.Text = $"{progress}%";
                        TxtPostProcessMessage.Text = message;
                        TxtPostProcessTitle.Text = state switch
                        {
                            "transcribing" => "Whisper 正在生成最终稿",
                            "translating" => "正在生成最终译文",
                            "summarizing" => "Granite 正在生成最终会议纪要",
                            "saving" => "正在保存最终会议成果",
                            "complete" => "会议最终稿已完成",
                            "failed" => "会议最终稿生成失败",
                            _ => "正在生成会议最终稿"
                        };
                        if (state is not "complete" and not "failed")
                        {
                            SetStatus($"Processing · {message}");
                        }
                    });
                    break;
                }

                case "streaming_postprocess_transcript_reset":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _refinedCaptions.Clear();
                        _refinedCaptionBySegment.Clear();
                        _refinedTranscriptReady = false;
                        BtnShowFinalTranscript.IsEnabled = false;
                    });
                    break;

                case "streaming_postprocess_segment":
                {
                    string text = Text();
                    string source = Source();
                    long segmentId = SegmentId();
                    long startMs = StartMs();
                    if (!string.IsNullOrWhiteSpace(text) && segmentId > 0)
                    {
                        DispatcherQueue.TryEnqueue(
                            () => AddRefinedTranscript(
                                segmentId,
                                source,
                                startMs,
                                text));
                    }
                    break;
                }

                case "streaming_postprocess_translation":
                {
                    string text = Text();
                    long segmentId = SegmentId();
                    if (!string.IsNullOrWhiteSpace(text) && segmentId > 0)
                    {
                        DispatcherQueue.TryEnqueue(
                            () => UpdateRefinedTranslation(
                                segmentId,
                                text));
                    }
                    break;
                }

                case "streaming_postprocess_warning":
                {
                    string message = Message();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TxtPostProcessMessage.Text = message;
                        SetStatus($"Processing warning · {message}");
                    });
                    break;
                }

                case "streaming_postprocess_complete":
                    DispatcherQueue.TryEnqueue(
                        () => CompletePostProcessing(true, ""));
                    break;

                case "streaming_postprocess_error":
                {
                    string message = Message();
                    DispatcherQueue.TryEnqueue(
                        () => CompletePostProcessing(false, message));
                    break;
                }

                case "streaming_partial":
                {
                    var text = Text();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string source = Source();
                        long utteranceId = UtteranceId();
                        DispatcherQueue.TryEnqueue(
                            () => UpdatePartialTranscript(
                                text,
                                source,
                                utteranceId));
                    }
                    break;
                }

                case "streaming_final":
                {
                    var text = Text();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string source = Source();
                        long utteranceId = UtteranceId();
                        DispatcherQueue.TryEnqueue(
                            () => AddFinalTranscript(
                                text,
                                source,
                                utteranceId));
                    }
                    break;
                }

                case "streaming_translation_partial":
                case "streaming_translation_final":
                {
                    var text = Text();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string source = Source();
                        long utteranceId = UtteranceId();
                        bool isFinal = type == "streaming_translation_final";
                        DispatcherQueue.TryEnqueue(
                            () =>
                            {
                                SetTranslationStatus("运行中 · 完全离线");
                                UpdateTranslation(
                                    text,
                                    source,
                                    utteranceId,
                                    isFinal);
                            });
                    }
                    break;
                }

                case "streaming_summary_status":
                {
                    string state = root.TryGetProperty(
                            "state",
                            out var stateElement)
                        ? stateElement.GetString() ?? ""
                        : "";
                    string message = Message();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (state is "ready" or "waiting" or "final")
                        {
                            _summaryServiceAvailable = true;
                            _summaryReady = true;
                        }
                        else if (state is "checking" or "loading" or "disabled")
                        {
                            _summaryServiceAvailable = false;
                            _summaryReady = false;
                        }
                        else if (state is "queued" or "generating" or "retrying")
                        {
                            _summaryReady = false;
                        }
                        else if (state == "error")
                        {
                            // 生成或事实校验失败后允许用户重试；只有模型加载
                            // 失败时服务才不可用。
                            if (message.Contains("加载失败") ||
                                message.Contains("未加载"))
                            {
                                _summaryServiceAvailable = false;
                            }
                            _summaryReady = _summaryServiceAvailable;
                        }
                        SetSummaryStatus(message);
                        BtnSummarizeNow.IsEnabled =
                            _summaryEnabled &&
                            _summaryReady &&
                            (_isRecording ||
                             _postMeetingSummaryAvailable);
                    });
                    break;
                }

                case "streaming_summary_partial":
                {
                    string text = Text();
                    string summaryKind = SummaryKind();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (summaryKind == "detailed")
                            {
                                TxtDetailedMeetingSummary.Text = text;
                            }
                            else
                            {
                                TxtMeetingSummary.Text = text;
                            }
                        }
                        SetSummaryStatus(
                            summaryKind == "detailed"
                                ? "Granite 正在生成详细摘要…"
                                : "Granite 正在更新实时速览…");
                        BtnSummarizeNow.IsEnabled = false;
                    });
                    break;
                }

                case "streaming_summary_final":
                {
                    string text = Text();
                    string summaryKind = SummaryKind();
                    bool isFinal = root.TryGetProperty(
                            "is_final",
                            out var finalElement) &&
                        finalElement.ValueKind == JsonValueKind.True;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (summaryKind == "detailed")
                            {
                                TxtDetailedMeetingSummary.Text = text;
                            }
                            else
                            {
                                TxtMeetingSummary.Text = text;
                            }
                        }
                        SetSummaryStatus(
                            isFinal
                                ? "最终详细摘要已生成并保存"
                                : (summaryKind == "detailed"
                                    ? "自适应详细摘要已生成并保存"
                                    : "实时速览已更新并保存"));
                        TxtSummaryUpdated.Text =
                            $"更新时间：{DateTime.Now:HH:mm:ss} · 已保存到本地数据库";
                        _summaryServiceAvailable = true;
                        _summaryReady = true;
                        BtnSummarizeNow.IsEnabled =
                            _summaryEnabled &&
                            (_isRecording ||
                             _postMeetingSummaryAvailable) &&
                            !isFinal;
                    });
                    break;
                }

                case "streaming_persistence_error":
                {
                    string message = Message();
                    DispatcherQueue.TryEnqueue(
                        () => SetStatus($"数据库写入错误: {message}"));
                    break;
                }

                case "streaming_translation_error":
                case "translation_error":
                {
                    var msg = Message();
                    Debug.WriteLine(
                        $"[StreamingMeeting] Translation error: {msg}");
                    DispatcherQueue.TryEnqueue(
                        () =>
                        {
                            SetTranslationStatus($"错误: {msg}");
                            SetStatus($"翻译错误（转录继续）: {msg}");
                        });
                    break;
                }

                // Worker 发的是 streaming_error，早先只匹配 "error"，所有失败都被静默吞掉
                case "streaming_error":
                case "error":
                {
                    var msg = Message();
                    Debug.WriteLine($"[StreamingMeeting] Worker error: {msg}");

                    // 启动握手期间的错误要让 StartMeetingAsync 立刻返回
                    _startedTcs?.TrySetResult(false);
                    _recordingStoppedTcs?.TrySetResult(false);
                    _stoppedTcs?.TrySetResult(false);

                    // 转录中可能连续报错，这里只更新状态栏。
                    // 弹 ContentDialog 会因为"同时只能开一个"直接抛异常。
                    DispatcherQueue.TryEnqueue(() => SetStatus($"Error: {msg}"));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Message parse failed: {ex.Message} / {json}");
        }
    }

    private void AddRefinedTranscript(
        long segmentId,
        string source,
        long startMs,
        string text)
    {
        if (_refinedCaptionBySegment.TryGetValue(
                segmentId,
                out var existing))
        {
            existing.Text = text;
            return;
        }

        var timestamp = TimeSpan.FromMilliseconds(
            Math.Max(0, startMs));
        var caption = new StreamingCaption
        {
            SegmentId = segmentId,
            StartMs = startMs,
            SpeakerName = GetSourceDisplayName(source),
            SpeakerColor = GetSourceColor(source),
            Timestamp = timestamp.ToString(@"hh\:mm\:ss"),
            Text = text,
            TextOpacity = 1.0
        };

        int insertAt = 0;
        while (insertAt < _refinedCaptions.Count)
        {
            var current = _refinedCaptions[insertAt];
            if (current.StartMs > startMs ||
                (current.StartMs == startMs &&
                 current.SegmentId > segmentId))
            {
                break;
            }
            insertAt++;
        }
        _refinedCaptions.Insert(insertAt, caption);
        _refinedCaptionBySegment[segmentId] = caption;
    }

    private void UpdateRefinedTranslation(long segmentId, string text)
    {
        if (_refinedCaptionBySegment.TryGetValue(
                segmentId,
                out var caption))
        {
            caption.TranslatedText = text;
            caption.TranslationOpacity = 1.0;
        }
    }

    private void CompletePostProcessing(bool success, string message)
    {
        StopPostProcessDatabaseMonitor();
        _isPostProcessing = false;
        _refinedTranscriptReady =
            success && _refinedCaptions.Count > 0;
        PostProcessPanel.Visibility = Visibility.Visible;
        PostProcessProgress.Value = 100;
        TxtPostProcessPercent.Text = "100%";
        TxtPostProcessTitle.Text = success
            ? "会议最终稿已完成"
            : "会议最终稿生成失败";
        TxtPostProcessMessage.Text = success
            ? "Whisper 最终稿以及已生成的译文、会议纪要均已保存到本地数据库。"
            : message;
        BtnRetryPostProcess.Visibility = success
            ? Visibility.Collapsed
            : Visibility.Visible;

        BtnStartMeeting.IsEnabled = true;
        CmbAudioSource.IsEnabled = true;
        CmbTranslationMode.IsEnabled = true;
        UpdateContextSwitchAvailability();
        BtnManageMeetingContext.IsEnabled = true;
        ChkLiveSummary.IsEnabled = true;
        BtnClear.IsEnabled =
            _captions.Count > 0 || _refinedCaptions.Count > 0;
        BtnExport.IsEnabled = BtnClear.IsEnabled;
        BtnShowFinalTranscript.IsEnabled =
            _refinedTranscriptReady && !_showingRefinedTranscript;

        _postMeetingSummaryAvailable =
            success && _summaryEnabled;
        _summaryServiceAvailable =
            _postMeetingSummaryAvailable;
        _summaryReady =
            _postMeetingSummaryAvailable;
        BtnSummarizeNow.IsEnabled =
            _postMeetingSummaryAvailable;

        if (_refinedTranscriptReady)
        {
            _segmentCount = _refinedCaptions.Count;
            TxtSegmentCount.Text = _segmentCount.ToString();
            ShowTranscriptVersion(true);
            SetStatus("Complete · Whisper 最终稿已保存");
        }
        else
        {
            ShowTranscriptVersion(false);
            SetStatus(
                success
                    ? "Complete · 本场录音没有识别出有效文字"
                    : $"最终稿失败 · {message}");
        }
    }

    // ========== 字幕更新 ==========
    // 显示单位是"来源 + 段落"。我方和对方各自累积 confirmed/partial，
    // 所以双方重叠讲话时，两条字幕可以同时就地更新。

    private CaptionStreamState GetCaptionState(string source)
    {
        if (!_captionStates.TryGetValue(source, out var state))
        {
            state = new CaptionStreamState();
            _captionStates[source] = state;
        }
        return state;
    }

    private StreamingCaption EnsureParagraph(
        string source,
        CaptionStreamState state)
    {
        if (state.CurrentParagraph != null) return state.CurrentParagraph;

        state.CurrentParagraph = new StreamingCaption
        {
            SpeakerName = GetSourceDisplayName(source),
            SpeakerColor = GetSourceColor(source),
            Text = "",
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            TextOpacity = 0.6
        };
        state.ConfirmedText = "";
        _captions.Add(state.CurrentParagraph);

        ScrollToLatest(force: true);   // 新增条目一定要滚到底
        return state.CurrentParagraph;
    }

    private void UpdatePartialTranscript(
        string text,
        string source,
        long utteranceId)
    {
        var now = DateTime.Now;
        var state = GetCaptionState(source);

        // 如果当前段已经包含上一句的定稿文本，并且中间停顿较长，
        // 要在写入“新一句的 partial”之前先封口。
        //
        // 不能等 final 到来时再封口：那时当前条目已经显示了新一句的
        // partial，先 CloseParagraph() 会把 partial 留成一条记录，随后
        // final 又新建一条，从而出现“无标点 partial + 有标点 final”
        // 两条内容近似重复的字幕。
        if (ShouldStartNewParagraph(state, now))
        {
            CloseParagraph(source);
        }

        state = GetCaptionState(source);
        var para = EnsureParagraph(source, state);
        RegisterUtterance(source, utteranceId, para);
        para.Text = JoinText(state.ConfirmedText, text);
        state.HasActivePartial = true;

        ScrollToLatest(force: false);  // partial 每秒约 10 条，节流
    }

    private void AddFinalTranscript(
        string text,
        string source,
        long utteranceId)
    {
        var now = DateTime.Now;
        var state = GetCaptionState(source);

        // 正常情况下，当前 final 应当覆盖/提交同一句已经显示的 partial，
        // 不能把 partial 封口后再新建一条 final。
        //
        // 只有当前段确实含有“上一句的已定稿文本”，且这次 final 前没有
        // 收到 partial 帮我们提前分段时，才在这里兜底封口。
        if (!state.HasActivePartial && ShouldStartNewParagraph(state, now))
        {
            CloseParagraph(source);
        }

        state = GetCaptionState(source);
        var para = EnsureParagraph(source, state);
        RegisterUtterance(source, utteranceId, para);

        state.ConfirmedText = JoinText(state.ConfirmedText, text);
        para.Text = state.ConfirmedText;
        para.TextOpacity = 1.0;
        state.HasActivePartial = false;
        state.LastFinalTime = now;

        _segmentCount++;
        TxtSegmentCount.Text = _segmentCount.ToString();

        EnsureSourceExists(source);

        // 句尾有终止标点 = 这句话说完了；或者段落已经太长
        if (EndsSentence(state.ConfirmedText) ||
            state.ConfirmedText.Length >= MaxParagraphChars)
        {
            CloseParagraph(source);
        }

        ScrollToLatest(force: true);
        Debug.WriteLine($"[StreamingMeeting] Final [{source}]: {text}");
    }

    private void RegisterUtterance(
        string source,
        long utteranceId,
        StreamingCaption caption)
    {
        if (utteranceId <= 0) return;

        _captionByUtterance[(source, utteranceId)] = caption;
        if (!_translationStates.ContainsKey(caption))
        {
            _translationStates[caption] = new CaptionTranslationState();
        }
    }

    private void UpdateTranslation(
        string text,
        string source,
        long utteranceId,
        bool isFinal)
    {
        if (utteranceId <= 0 ||
            !_captionByUtterance.TryGetValue(
                (source, utteranceId),
                out var caption))
        {
            // 页面已清空或上一场会议的迟到结果，不能误写到新字幕上。
            return;
        }

        if (!_translationStates.TryGetValue(caption, out var state))
        {
            state = new CaptionTranslationState();
            _translationStates[caption] = state;
        }

        if (isFinal)
        {
            state.Confirmed[utteranceId] = text;
            if (state.PartialUtteranceId == utteranceId)
            {
                state.PartialUtteranceId = null;
                state.PartialText = "";
            }
        }
        else if (!state.Confirmed.ContainsKey(utteranceId))
        {
            state.PartialUtteranceId = utteranceId;
            state.PartialText = text;
        }

        string combined = "";
        foreach (string confirmed in state.Confirmed.Values)
        {
            combined = JoinText(combined, confirmed);
        }
        if (state.PartialUtteranceId.HasValue &&
            !string.IsNullOrWhiteSpace(state.PartialText))
        {
            combined = JoinText(combined, state.PartialText);
        }

        caption.TranslatedText = combined;
        caption.TranslationOpacity =
            state.PartialUtteranceId.HasValue ? 0.65 : 1.0;

        ScrollToLatest(force: isFinal);
        Debug.WriteLine(
            $"[StreamingMeeting] Translation {(isFinal ? "final" : "partial")} " +
            $"[{source}/{utteranceId}]: {text}");
    }

    private static bool ShouldStartNewParagraph(
        CaptionStreamState state,
        DateTime now)
    {
        return state.CurrentParagraph != null
            && !string.IsNullOrWhiteSpace(state.ConfirmedText)
            && state.LastFinalTime != DateTime.MinValue
            && (now - state.LastFinalTime).TotalSeconds > ParagraphGapSeconds;
    }

    private void CloseParagraph(string source)
    {
        var state = GetCaptionState(source);
        if (state.CurrentParagraph != null)
        {
            state.CurrentParagraph.TextOpacity = 1.0;
            state.CurrentParagraph = null;
        }
        state.ConfirmedText = "";
        state.HasActivePartial = false;
    }

    private void CloseAllParagraphs()
    {
        foreach (string source in _captionStates.Keys.ToArray())
        {
            CloseParagraph(source);
        }
    }

    /// <summary>
    /// 终止标点判定。没有标点模型时（文本永远不带标点）这里恒为 false，
    /// 段落就只靠间隔和长度来切，行为退化成合并模式而不会出错。
    /// </summary>
    private static bool EndsSentence(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return "。！？.!?…".Contains(text[^1]);
    }

    /// <summary>
    /// 拼接两段文本。中文之间不加空格，英文单词之间要加，否则会粘成一坨。
    /// </summary>
    private static string JoinText(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;

        char a = left[^1];
        char b = right[0];
        bool needSpace = a < 0x2E80 && b < 0x2E80 && a != ' ' && b != ' ';

        return needSpace ? left + " " + right : left + right;
    }

    private void ScrollToLatest(bool force)
    {
        if (CaptionsList.Items.Count == 0) return;

        // 不节流的话每秒 10 次滚动定位，列表会持续抖动
        var now = DateTime.Now;
        if (!force && (now - _lastScrollTime).TotalSeconds < ScrollThrottleSeconds) return;
        _lastScrollTime = now;

        CaptionsList.ScrollIntoView(CaptionsList.Items[^1]);
    }

    // ========== 来源标签（不做对方内部讲话人分离）==========
    private void EnsureSourceExists(string source)
    {
        string speakerName = GetSourceDisplayName(source);
        if (!_speakers.Any(s => s.Name == speakerName))
        {
            _speakers.Add(new Speaker
            {
                Name = speakerName,
                ColorBrush = GetSourceColor(source)
            });
            TxtSpeakerCount.Text = _speakers.Count.ToString();
        }
    }

    private static string GetSourceDisplayName(string source)
    {
        return source == SystemSource ? "对方" : "我方";
    }

    private static SolidColorBrush GetSourceColor(string source)
    {
        return new SolidColorBrush(
            source == SystemSource ? Colors.OrangeRed : Colors.DodgerBlue);
    }

    private void SetStatus(string text)
    {
        if (TxtStatus != null) TxtStatus.Text = text;
    }

    private void SetTranslationStatus(string text)
    {
        if (TxtTranslationStatus != null) TxtTranslationStatus.Text = text;
    }

    private void SetSummaryStatus(string text)
    {
        if (TxtSummaryStatus != null) TxtSummaryStatus.Text = text;
    }

    private void UpdateDuration(object? sender, object e)
    {
        var duration = (_meetingEndTime ?? DateTime.Now) - _meetingStartTime;
        TxtDuration.Text = $"{duration:hh\\:mm\\:ss}";
    }

    // ========== 导出 ==========
    private async Task ExportTranscriptAsync()
    {
        try
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Text File", new[] { ".txt" });
            savePicker.FileTypeChoices.Add("Markdown File", new[] { ".md" });
            savePicker.SuggestedFileName = $"meeting_transcript_{DateTime.Now:yyyyMMdd_HHmmss}";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            await Windows.Storage.FileIO.WriteTextAsync(file, GenerateTranscriptContent());
            Debug.WriteLine($"[StreamingMeeting] Exported to: {file.Path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Export failed: {ex.Message}");
            await ShowErrorAsync($"导出失败：{ex.Message}");
        }
    }

    private string GenerateTranscriptContent()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Meeting Transcript");
        sb.AppendLine($"Date: {_meetingStartTime:yyyy-MM-dd}");
        sb.AppendLine(
            $"Duration: {(_meetingEndTime ?? DateTime.Now) - _meetingStartTime:hh\\:mm\\:ss}");
        sb.AppendLine($"Segments: {_segmentCount}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 只导出有定稿内容的段落（纯 partial 的临时段落 TextOpacity 仍是 0.6）
        foreach (var caption in VisibleCaptions.Where(
            c => c.TextOpacity >= 1.0 &&
                 !string.IsNullOrWhiteSpace(c.Text)))
        {
            sb.AppendLine(
                $"[{caption.Timestamp}] {caption.SpeakerName}: {caption.Text}");
            if (!string.IsNullOrWhiteSpace(caption.TranslatedText))
            {
                sb.AppendLine($"  译文: {caption.TranslatedText}");
            }
            sb.AppendLine();
        }

        if (TxtSummaryUpdated.Text.Length > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(TxtMeetingSummary.Text))
            {
                sb.AppendLine("# Real-time Quick Summary");
                sb.AppendLine();
                sb.AppendLine(TxtMeetingSummary.Text);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(TxtDetailedMeetingSummary.Text))
            {
                sb.AppendLine(
                    _refinedTranscriptReady
                        ? "# Final Meeting Minutes"
                        : "# Adaptive Detailed Summary");
                sb.AppendLine();
                sb.AppendLine(TxtDetailedMeetingSummary.Text);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private string GenerateVisibleCaptionContent()
    {
        var sb = new StringBuilder();
        foreach (var caption in VisibleCaptions.Where(
            c => !string.IsNullOrWhiteSpace(c.Text)
                 || !string.IsNullOrWhiteSpace(c.TranslatedText)))
        {
            if (!string.IsNullOrWhiteSpace(caption.Text))
            {
                sb.AppendLine(
                    $"[{caption.Timestamp}] {caption.SpeakerName}: {caption.Text}");
            }
            if (!string.IsNullOrWhiteSpace(caption.TranslatedText))
            {
                sb.AppendLine($"译文: {caption.TranslatedText}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ========== 清理 ==========
    private void ClearAll()
    {
        StopPostProcessDatabaseMonitor();
        _captions.Clear();
        _refinedCaptions.Clear();
        _refinedCaptionBySegment.Clear();
        _speakers.Clear();
        _segmentCount = 0;

        _captionStates.Clear();
        _captionByUtterance.Clear();
        _translationStates.Clear();
        _meetingEndTime = null;
        _postMeetingSummaryAvailable = false;
        _summaryServiceAvailable = false;
        _isPostProcessing = false;
        _refinedTranscriptReady = false;
        _activeMeetingId = 0;
        ShowTranscriptVersion(false);

        TxtDuration.Text = "00:00:00";
        TxtSpeakerCount.Text = "0";
        TxtSegmentCount.Text = "0";
        TxtMeetingSummary.Text =
            "会议开始后，Granite 会在后台加载并持续更新实时速览。";
        TxtDetailedMeetingSummary.Text =
            "会议进行中可随时点击“生成详细摘要”。";
        TxtSummaryStatus.Text = "尚未启动";
        TxtSummaryUpdated.Text = "";
        _summaryReady = false;

        BtnExport.IsEnabled = false;
        BtnClear.IsEnabled = false;
        BtnSummarizeNow.IsEnabled = false;
        BtnShowLiveTranscript.IsEnabled = false;
        BtnShowFinalTranscript.IsEnabled = false;
        PostProcessPanel.Visibility = Visibility.Collapsed;
        BtnRetryPostProcess.Visibility = Visibility.Collapsed;
        PostProcessProgress.Value = 0;
        TxtPostProcessPercent.Text = "0%";

        Debug.WriteLine("[StreamingMeeting] Cleared");
    }

    private void CleanupResources()
    {
        try
        {
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _pumpCts = null;
            _pumpTasks.Clear();

            DisposeAudioPipelines();

            Debug.WriteLine("[StreamingMeeting] Resources cleaned up");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Cleanup failed: {ex.Message}");
        }
    }

    private void DisposeAudioPipelines()
    {
        foreach (var pipeline in _audioPipelines)
        {
            try { pipeline.Capture.StopRecording(); } catch { }

            if (pipeline.DataAvailableHandler != null)
            {
                pipeline.Capture.DataAvailable -= pipeline.DataAvailableHandler;
            }
            if (pipeline.RecordingStoppedHandler != null)
            {
                pipeline.Capture.RecordingStopped -=
                    pipeline.RecordingStoppedHandler;
            }

            try { pipeline.Capture.Dispose(); } catch { }
        }
        _audioPipelines.Clear();
    }

    private string GetSelectedSourceMode()
    {
        return CmbAudioSource.SelectedIndex switch
        {
            1 => SystemSource,
            2 => "both",
            _ => MicrophoneSource
        };
    }

    private string GetSelectedTranslationMode()
    {
        return CmbTranslationMode.SelectedIndex switch
        {
            1 => "auto",
            2 => "to_zh",
            3 => "to_en",
            _ => "off"
        };
    }

    private async Task StopWorkerStreamingSilentlyAsync()
    {
        if (!_workerStreamingStarted || _mainWindow == null)
        {
            return;
        }

        try
        {
            _recordingStoppedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stopCmd = new StopStreamingCommand();
            await _mainWindow.SendJsonAsync(
                JsonSerializer.Serialize(
                    stopCmd,
                    Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n");
            await Task.WhenAny(
                _recordingStoppedTcs.Task,
                Task.Delay(TimeSpan.FromSeconds(3)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[StreamingMeeting] Silent worker stop failed: {ex.Message}");
        }
        finally
        {
            _workerStreamingStarted = false;
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
