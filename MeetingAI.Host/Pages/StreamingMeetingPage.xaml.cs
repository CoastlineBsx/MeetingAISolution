using System;
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
using Windows.UI;
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class Speaker
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Gray);
    }

    // ========== 常量 ==========
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

    // 当前正在累积的段落。多个 utterance 会合并进同一条字幕，直到封口。
    private StreamingCaption? _currentParagraph;
    private string _confirmedText = "";        // 该段落里已定稿的部分
    private DateTime _lastFinalTime = DateTime.MinValue;
    private DateTime _lastScrollTime = DateTime.MinValue;

    // ========== 音频捕获 ==========
    // 采集回调只负责把数据塞进缓冲区，重采样和发送都在单个泵任务里做：
    // 重采样器全程复用（滤波器状态连续），且只有一个任务写管道（天然串行）。
    private WasapiCapture? _microphone;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _monoResampled;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    private MainWindow? _mainWindow;

    // ========== 说话人颜色池 ==========
    private readonly Color[] _speakerColors = new[]
    {
        Colors.DodgerBlue,
        Colors.OrangeRed,
        Colors.MediumSeaGreen,
        Colors.MediumPurple,
        Colors.Goldenrod,
        Colors.DeepPink,
        Colors.Teal,
        Colors.Coral
    };

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
            SetStatus("Connecting…");

            await _mainWindow.EnsurePipeAsync();

            // 处理器必须在发命令之前挂上，否则会漏掉 streaming_started
            _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainWindow.StreamingMessageHandler = OnStreamingMessageReceived;

            var startCmd = new StartStreamingCommand { sample_rate = TargetSampleRate };
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
                SetStatus("Failed");
                await ShowErrorAsync("等待 Worker 启动流式会话超时（3 分钟）。请检查日志窗口里 [Worker] 开头的输出。");
                return;
            }
            if (!_startedTcs.Task.Result)
            {
                // 具体错误已由 streaming_error 分支写进状态栏
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                return;
            }

            if (!InitializeAudioCapture())
            {
                _mainWindow.StreamingMessageHandler = null;
                BtnStartMeeting.IsEnabled = true;
                SetStatus("Failed");
                await ShowErrorAsync("麦克风初始化失败，请检查音频设备。");
                return;
            }

            _isRecording = true;
            _meetingStartTime = DateTime.Now;
            _segmentCount = 0;

            // 上一场会议可能留着没封口的段落，新会议不该续写它
            _currentParagraph = null;
            _confirmedText = "";
            _lastFinalTime = DateTime.MinValue;

            _microphone!.StartRecording();

            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => AudioPumpAsync(_pumpCts.Token));

            _durationTimer?.Start();

            BtnStopMeeting.IsEnabled = true;
            BtnExport.IsEnabled = false;
            BtnClear.IsEnabled = false;
            SetStatus("Listening");

            Debug.WriteLine("[StreamingMeeting] Meeting started");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Start failed: {ex.Message}");
            BtnStartMeeting.IsEnabled = true;
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
            try { _microphone?.StopRecording(); } catch { }

            _pumpCts?.Cancel();
            if (_pumpTask != null)
            {
                try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                _pumpTask = null;
            }

            if (_mainWindow != null)
            {
                _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var stopCmd = new StopStreamingCommand();
                await _mainWindow.SendJsonAsync(
                    JsonSerializer.Serialize(stopCmd, Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n");

                // Worker 在 streaming_stopped 之前还会补发最后一段 final 结果，
                // 立刻注销处理器会把它丢掉
                await Task.WhenAny(_stoppedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                _mainWindow.StreamingMessageHandler = null;
            }

            CloseParagraph();   // 收尾的 final 已经收完，把最后一段定稿

            _durationTimer?.Stop();
            CleanupResources();

            BtnStartMeeting.IsEnabled = true;
            BtnExport.IsEnabled = _captions.Count > 0;
            BtnClear.IsEnabled = _captions.Count > 0;
            SetStatus("Idle");

            Debug.WriteLine("[StreamingMeeting] Meeting stopped");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Stop failed: {ex.Message}");
            BtnStartMeeting.IsEnabled = true;
            SetStatus("Idle");
            await ShowErrorAsync($"停止失败：{ex.Message}");
        }
    }

    // ========== 音频捕获初始化 ==========
    private bool InitializeAudioCapture()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _microphone = new WasapiCapture(device);

            // 采集线程只做入队，任何耗时操作都会拖累 WASAPI
            _captureBuffer = new BufferedWaveProvider(_microphone.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
                ReadFully = false   // 缓冲区空时返回 0，让泵按真实速率走，不然会疯狂发静音
            };

            _microphone.DataAvailable += OnAudioDataAvailable;
            _microphone.RecordingStopped += (s, e) => Debug.WriteLine("[StreamingMeeting] Recording stopped");

            // 先降混再重采样（省一半计算量），整条链路全程复用
            ISampleProvider chain = _captureBuffer.ToSampleProvider();
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

            _monoResampled = chain;

            Debug.WriteLine($"[StreamingMeeting] Capture: {device.FriendlyName}, " +
                            $"{_microphone.WaveFormat} -> {TargetSampleRate}Hz mono");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Capture init failed: {ex.Message}");
            return false;
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (_isRecording && e.BytesRecorded > 0)
            {
                _captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Buffer append failed: {ex.Message}");
        }
    }

    // ========== 音频发送泵（单任务，保证管道写入串行且有序）==========
    private async Task AudioPumpAsync(CancellationToken ct)
    {
        var samples = new float[ChunkSamples];
        var pcm = new byte[ChunkSamples * 2];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int got = 0;
                while (got < ChunkSamples && !ct.IsCancellationRequested)
                {
                    int n = _monoResampled?.Read(samples, got, ChunkSamples - got) ?? 0;
                    if (n <= 0)
                    {
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
            Debug.WriteLine($"[StreamingMeeting] Audio pump failed: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => SetStatus($"Audio error: {ex.Message}"));
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
            int SpeakerId() => root.TryGetProperty("speaker_id", out var s) && s.TryGetInt32(out var v) && v >= 0 ? v : 0;
            string Message() => root.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";

            switch (type)
            {
                case "streaming_started":
                    _startedTcs?.TrySetResult(true);
                    break;

                // Worker 在加载模型的各阶段回传进度，否则界面只能干等
                case "info":
                {
                    var msg = Message();
                    Debug.WriteLine($"[StreamingMeeting] {msg}");
                    if (msg.StartsWith("[Sherpa]"))
                    {
                        DispatcherQueue.TryEnqueue(() => SetStatus(msg.Replace("[Sherpa] ", "")));
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
                        int sid = SpeakerId();
                        DispatcherQueue.TryEnqueue(() => UpdatePartialTranscript(text, sid));
                    }
                    break;
                }

                case "streaming_final":
                {
                    var text = Text();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        int sid = SpeakerId();
                        DispatcherQueue.TryEnqueue(() => AddFinalTranscript(text, sid));
                    }
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
    // 显示单位是"段落"而非单个 utterance：已定稿的文本累积在 _confirmedText，
    // 未定稿的 partial 实时拼在后面，直到满足封口条件才起新段。

    private StreamingCaption EnsureParagraph(int speakerId)
    {
        if (_currentParagraph != null) return _currentParagraph;

        _currentParagraph = new StreamingCaption
        {
            SpeakerName = $"Speaker {speakerId}",
            SpeakerColor = GetSpeakerColor(speakerId),
            Text = "",
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            TextOpacity = 0.6
        };
        _confirmedText = "";
        _captions.Add(_currentParagraph);

        ScrollToLatest(force: true);   // 新增条目一定要滚到底
        return _currentParagraph;
    }

    private void UpdatePartialTranscript(string text, int speakerId)
    {
        var para = EnsureParagraph(speakerId);
        para.Text = JoinText(_confirmedText, text);

        ScrollToLatest(force: false);  // partial 每秒约 10 条，节流
    }

    private void AddFinalTranscript(string text, int speakerId)
    {
        var now = DateTime.Now;

        // 距上一句间隔过大，说明上一段其实已经说完了，先封口再开新段
        if (_currentParagraph != null
            && _lastFinalTime != DateTime.MinValue
            && (now - _lastFinalTime).TotalSeconds > ParagraphGapSeconds)
        {
            CloseParagraph();
        }

        var para = EnsureParagraph(speakerId);

        _confirmedText = JoinText(_confirmedText, text);
        para.Text = _confirmedText;
        para.TextOpacity = 1.0;
        _lastFinalTime = now;

        _segmentCount++;
        TxtSegmentCount.Text = _segmentCount.ToString();

        EnsureSpeakerExists(speakerId);

        // 句尾有终止标点 = 这句话说完了；或者段落已经太长
        if (EndsSentence(_confirmedText) || _confirmedText.Length >= MaxParagraphChars)
        {
            CloseParagraph();
        }

        ScrollToLatest(force: true);
        Debug.WriteLine($"[StreamingMeeting] Final: {text}");
    }

    private void CloseParagraph()
    {
        if (_currentParagraph != null)
        {
            _currentParagraph.TextOpacity = 1.0;
            _currentParagraph = null;
        }
        _confirmedText = "";
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

    // ========== 说话人管理（当前不做分离，逻辑保留备用）==========
    private void EnsureSpeakerExists(int speakerId)
    {
        string speakerName = $"Speaker {speakerId}";
        if (!_speakers.Any(s => s.Name == speakerName))
        {
            _speakers.Add(new Speaker { Name = speakerName, ColorBrush = GetSpeakerColor(speakerId) });
            TxtSpeakerCount.Text = _speakers.Count.ToString();
        }
    }

    private SolidColorBrush GetSpeakerColor(int speakerId)
    {
        int index = Math.Abs(speakerId) % _speakerColors.Length;
        return new SolidColorBrush(_speakerColors[index]);
    }

    private void SetStatus(string text)
    {
        if (TxtStatus != null) TxtStatus.Text = text;
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
            sb.AppendLine($"[{caption.Timestamp}] {caption.Text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ========== 清理 ==========
    private void ClearAll()
    {
        _captions.Clear();
        _speakers.Clear();
        _segmentCount = 0;

        _currentParagraph = null;
        _confirmedText = "";
        _lastFinalTime = DateTime.MinValue;

        TxtDuration.Text = "00:00:00";
        TxtSpeakerCount.Text = "0";
        TxtSegmentCount.Text = "0";

        BtnExport.IsEnabled = false;
        BtnClear.IsEnabled = false;

        Debug.WriteLine("[StreamingMeeting] Cleared");
    }

    private void CleanupResources()
    {
        try
        {
            _pumpCts?.Dispose();
            _pumpCts = null;

            if (_microphone != null)
            {
                _microphone.DataAvailable -= OnAudioDataAvailable;
                _microphone.Dispose();
                _microphone = null;
            }

            _monoResampled = null;
            _captureBuffer = null;

            Debug.WriteLine("[StreamingMeeting] Resources cleaned up");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Cleanup failed: {ex.Message}");
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
