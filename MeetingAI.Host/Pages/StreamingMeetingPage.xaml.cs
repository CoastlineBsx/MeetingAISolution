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
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MeetingAI.Host.Contracts.Messages;

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
    private readonly ObservableCollection<Speaker> _speakers = new();

    // ========== 转录状态 ==========
    private bool _isRecording = false;
    private DateTime _meetingStartTime;
    private DispatcherTimer? _durationTimer;
    private int _segmentCount = 0;

    // Worker 的握手信号：模型首次加载要几秒，音频不能抢跑
    private TaskCompletionSource<bool>? _startedTcs;
    private TaskCompletionSource<bool>? _stoppedTcs;

    // 麦克风和系统声音可以同时讲话，每个来源必须有独立的 partial/final 状态。
    private readonly Dictionary<string, CaptionStreamState> _captionStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Source, long UtteranceId), StreamingCaption>
        _captionByUtterance = new();
    private readonly Dictionary<StreamingCaption, CaptionTranslationState>
        _translationStates = new();
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

    private MainWindow? _mainWindow;

    public StreamingMeetingPage()
    {
        this.InitializeComponent();

        CaptionsList.ItemsSource = _captions;
        SpeakersList.ItemsSource = _speakers;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += UpdateDuration;

        _mainWindow = App.MainWindow as MainWindow;

        Debug.WriteLine("[StreamingMeeting] Page initialized");
    }

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

    private async Task RequestSummaryNowAsync()
    {
        if (!_isRecording || !_summaryEnabled || !_summaryReady ||
            _mainWindow == null)
        {
            SetSummaryStatus("请先启动已启用摘要的会议");
            return;
        }

        BtnSummarizeNow.IsEnabled = false;
        SetSummaryStatus("正在请求立即总结…");
        await _mainWindow.SendJsonAsync(
            "{\"type\":\"request_meeting_summary\"}\n");
    }

    // ========== 会议控制 ==========
    private async Task StartMeetingAsync()
    {
        if (_isRecording) return;

        try
        {
            if (_mainWindow == null)
            {
                await ShowErrorAsync("无法访问 MainWindow");
                return;
            }

            BtnStartMeeting.IsEnabled = false;
            CmbAudioSource.IsEnabled = false;
            CmbTranslationMode.IsEnabled = false;
            ChkLiveSummary.IsEnabled = false;
            BtnSummarizeNow.IsEnabled = false;
            SetStatus("Connecting…");

            await _mainWindow.EnsurePipeAsync();

            // 处理器必须在发命令之前挂上，否则会漏掉 streaming_started
            _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainWindow.StreamingMessageHandler = OnStreamingMessageReceived;

            _requestedTranslationMode = GetSelectedTranslationMode();
            _summaryEnabled = ChkLiveSummary.IsChecked == true;
            _summaryReady = false;
            SetTranslationStatus(
                _requestedTranslationMode == "off" ? "关闭" : "模型准备中…");
            SetSummaryStatus(
                _summaryEnabled ? "准备本地 Granite 摘要服务…" : "已关闭");
            TxtMeetingSummary.Text = _summaryEnabled
                ? "正在启动会议，摘要模型将在后台加载。"
                : "本场会议未启用自动摘要。";
            TxtSummaryUpdated.Text = "";

            var startCmd = new StartStreamingCommand
            {
                sample_rate = TargetSampleRate,
                source = GetSelectedSourceMode(),
                translation_mode = _requestedTranslationMode,
                summary_enabled = _summaryEnabled
            };
            await _mainWindow.SendJsonAsync(
                JsonSerializer.Serialize(startCmd, Contracts.AppJsonContext.Default.StartStreamingCommand) + "\n");

            SetStatus("Loading model…");

            // 等 Worker 确认会话就绪。抢在模型加载完之前送音频，Worker 只会回一串"会话未启动"。
            // 首次加载要读 200MB+ 的 onnx 并做图优化，冷启动可能到分钟级，超时给宽一点。
            var ready = await Task.WhenAny(_startedTcs.Task, Task.Delay(TimeSpan.FromMinutes(3)));
            if (ready != _startedTcs.Task)
            {
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                CmbAudioSource.IsEnabled = true;
                CmbTranslationMode.IsEnabled = true;
                ChkLiveSummary.IsEnabled = true;
                SetStatus("Failed");
                await ShowErrorAsync("等待 Worker 启动流式会话超时（3 分钟）。请检查日志窗口里 [Worker] 开头的输出。");
                return;
            }
            if (!_startedTcs.Task.Result)
            {
                // 具体错误已由 streaming_error 分支写进状态栏
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                CmbAudioSource.IsEnabled = true;
                CmbTranslationMode.IsEnabled = true;
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
                ChkLiveSummary.IsEnabled = true;
                SetStatus("Failed");
                await ShowErrorAsync("音频源初始化失败，请检查所选麦克风或系统播放设备。");
                return;
            }

            _isRecording = true;
            _meetingStartTime = DateTime.Now;
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
            BtnStopMeeting.IsEnabled = false;
            SetStatus("Finalizing…");

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
                _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var stopCmd = new StopStreamingCommand();
                await _mainWindow.SendJsonAsync(
                    JsonSerializer.Serialize(stopCmd, Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n");

                // Worker 在 streaming_stopped 之前还会补发最后一段 final 结果，
                // 立刻注销处理器会把它丢掉
                await Task.WhenAny(
                    _stoppedTcs.Task,
                    Task.Delay(TimeSpan.FromMinutes(3)));
                _mainWindow.StreamingMessageHandler = null;
                _workerStreamingStarted = false;
            }

            CloseAllParagraphs(); // 两个来源的 final 都已收完

            _durationTimer?.Stop();
            CleanupResources();

            BtnStartMeeting.IsEnabled = true;
            CmbAudioSource.IsEnabled = true;
            CmbTranslationMode.IsEnabled = true;
            ChkLiveSummary.IsEnabled = true;
            BtnSummarizeNow.IsEnabled = false;
            _summaryReady = false;
            BtnExport.IsEnabled = _captions.Count > 0;
            BtnClear.IsEnabled = _captions.Count > 0;
            SetStatus("Idle");

            Debug.WriteLine("[StreamingMeeting] Meeting stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Stop failed: {ex.Message}");
            await StopWorkerStreamingSilentlyAsync();
            CleanupResources();
            BtnStartMeeting.IsEnabled = true;
            CmbAudioSource.IsEnabled = true;
            CmbTranslationMode.IsEnabled = true;
            ChkLiveSummary.IsEnabled = true;
            BtnSummarizeNow.IsEnabled = false;
            _summaryReady = false;
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
            long UtteranceId() => root.TryGetProperty("utterance_id", out var id)
                && id.TryGetInt64(out long value)
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
                    DispatcherQueue.TryEnqueue(() =>
                    {
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
                        _summaryReady =
                            state is "ready" or "waiting" or "final";
                        SetSummaryStatus(message);
                        BtnSummarizeNow.IsEnabled =
                            _summaryEnabled && _summaryReady && _isRecording;
                    });
                    break;
                }

                case "streaming_summary_partial":
                {
                    string text = Text();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            TxtMeetingSummary.Text = text;
                        }
                        SetSummaryStatus("Granite 正在生成摘要…");
                        BtnSummarizeNow.IsEnabled = false;
                    });
                    break;
                }

                case "streaming_summary_final":
                {
                    string text = Text();
                    bool isFinal = root.TryGetProperty(
                            "is_final",
                            out var finalElement) &&
                        finalElement.ValueKind == JsonValueKind.True;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            TxtMeetingSummary.Text = text;
                        }
                        SetSummaryStatus(
                            isFinal
                                ? "最终摘要已生成并保存"
                                : "滚动摘要已更新并保存");
                        TxtSummaryUpdated.Text =
                            $"更新时间：{DateTime.Now:HH:mm:ss} · 已保存到本地数据库";
                        _summaryReady = true;
                        BtnSummarizeNow.IsEnabled =
                            _summaryEnabled && _isRecording && !isFinal;
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
        return "。！？!?…".Contains(text[^1]);
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
        var duration = DateTime.Now - _meetingStartTime;
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
        sb.AppendLine($"Duration: {DateTime.Now - _meetingStartTime:hh\\:mm\\:ss}");
        sb.AppendLine($"Segments: {_segmentCount}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 只导出有定稿内容的段落（纯 partial 的临时段落 TextOpacity 仍是 0.6）
        foreach (var caption in _captions.Where(c => c.TextOpacity >= 1.0 && !string.IsNullOrWhiteSpace(c.Text)))
        {
            sb.AppendLine(
                $"[{caption.Timestamp}] {caption.SpeakerName}: {caption.Text}");
            if (!string.IsNullOrWhiteSpace(caption.TranslatedText))
            {
                sb.AppendLine($"  译文: {caption.TranslatedText}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(TxtMeetingSummary.Text) &&
            TxtSummaryUpdated.Text.Length > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# Local AI Meeting Summary");
            sb.AppendLine();
            sb.AppendLine(TxtMeetingSummary.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GenerateVisibleCaptionContent()
    {
        var sb = new StringBuilder();
        foreach (var caption in _captions.Where(
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
        _captions.Clear();
        _speakers.Clear();
        _segmentCount = 0;

        _captionStates.Clear();
        _captionByUtterance.Clear();
        _translationStates.Clear();

        TxtDuration.Text = "00:00:00";
        TxtSpeakerCount.Text = "0";
        TxtSegmentCount.Text = "0";
        TxtMeetingSummary.Text =
            "会议开始后，Granite 会在后台加载并持续更新摘要。";
        TxtSummaryStatus.Text = "尚未启动";
        TxtSummaryUpdated.Text = "";
        _summaryReady = false;

        BtnExport.IsEnabled = false;
        BtnClear.IsEnabled = false;
        BtnSummarizeNow.IsEnabled = false;

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
            _stoppedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stopCmd = new StopStreamingCommand();
            await _mainWindow.SendJsonAsync(
                JsonSerializer.Serialize(
                    stopCmd,
                    Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n");
            await Task.WhenAny(
                _stoppedTcs.Task,
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
