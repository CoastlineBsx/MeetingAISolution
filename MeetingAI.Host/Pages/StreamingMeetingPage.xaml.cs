using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Windows.UI;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host.Pages;

public sealed partial class StreamingMeetingPage : Page
{
    // ========== 数据模型 ==========
    public class StreamingCaption
    {
        public string SpeakerName { get; set; } = "Unknown";
        public SolidColorBrush SpeakerColor { get; set; } = new SolidColorBrush(Colors.Gray);
        public string Text { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public double TextOpacity { get; set; } = 1.0;  // 用于区分临时/最终结果
    }

    public class Speaker
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Gray);
    }

    // ========== UI 数据绑定 ==========
    private readonly ObservableCollection<StreamingCaption> _captions = new();
    private readonly ObservableCollection<Speaker> _speakers = new();

    // ========== 转录状态 ==========
    private bool _isRecording = false;
    private DateTime _meetingStartTime;
    private DispatcherTimer? _durationTimer;
    private int _segmentCount = 0;

    // ========== 音频捕获相关 ==========
    private WasapiCapture? _microphone;
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

        // 绑定数据源
        CaptionsList.ItemsSource = _captions;
        SpeakersList.ItemsSource = _speakers;

        // 初始化定时器
        _durationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _durationTimer.Tick += UpdateDuration;

        // 获取 MainWindow 引用（用于访问管道）
        _mainWindow = App.MainWindow as MainWindow;

        Debug.WriteLine("[StreamingMeeting] Page initialized");
    }

    // ========== 按钮事件处理 ==========
    public void BtnStartMeeting_Click(object sender, RoutedEventArgs e)
    {
        _ = StartMeetingAsync();
    }

    public void BtnStopMeeting_Click(object sender, RoutedEventArgs e)
    {
        _ = StopMeetingAsync();
    }

    public void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        _ = ExportTranscriptAsync();
    }

    public void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ClearAll();
    }

    // ========== 会议控制逻辑 ==========
    private async Task StartMeetingAsync()
    {
        if (_isRecording) return;

        try
        {
            Debug.WriteLine("[StreamingMeeting] Starting meeting...");

            // 1. 确保管道连接
            if (_mainWindow == null)
            {
                await ShowErrorAsync("Cannot access MainWindow");
                return;
            }

            await _mainWindow.EnsurePipeAsync();

            // 2. 发送开始流式转录命令
            var startCmd = new StartStreamingCommand
            {
                sample_rate = 16000
            };
            var json = JsonSerializer.Serialize(startCmd, Contracts.AppJsonContext.Default.StartStreamingCommand) + "\n";
            await _mainWindow.SendJsonAsync(json);

            Debug.WriteLine("[StreamingMeeting] Sent start_streaming command");

            // 3. 初始化音频捕获
            if (!InitializeAudioCapture())
            {
                await ShowErrorAsync("Failed to initialize microphone. Please check audio device.");
                return;
            }

            // 4. 注册消息处理器
            _mainWindow.StreamingMessageHandler = OnStreamingMessageReceived;

            // 5. 开始录音
            _microphone?.StartRecording();
            _isRecording = true;
            _meetingStartTime = DateTime.Now;
            _segmentCount = 0;

            // 6. 启动定时器
            _durationTimer?.Start();

            // 7. 更新 UI
            BtnStartMeeting.IsEnabled = false;
            BtnStopMeeting.IsEnabled = true;
            BtnExport.IsEnabled = false;
            BtnClear.IsEnabled = false;

            Debug.WriteLine("[StreamingMeeting] Meeting started successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Start meeting failed: {ex.Message}");
            await ShowErrorAsync($"Failed to start meeting: {ex.Message}");
        }
    }

    private async Task StopMeetingAsync()
    {
        if (!_isRecording) return;

        try
        {
            Debug.WriteLine("[StreamingMeeting] Stopping meeting...");

            // 1. 停止录音
            _microphone?.StopRecording();
            _isRecording = false;

            // 2. 发送停止命令
            if (_mainWindow != null)
            {
                var stopCmd = new StopStreamingCommand();
                var json = JsonSerializer.Serialize(stopCmd, Contracts.AppJsonContext.Default.StopStreamingCommand) + "\n";
                await _mainWindow.SendJsonAsync(json);

                // 注销消息处理器
                _mainWindow.StreamingMessageHandler = null;
            }

            // 3. 停止定时器
            _durationTimer?.Stop();

            // 4. 清理资源
            CleanupResources();

            // 5. 更新 UI
            BtnStartMeeting.IsEnabled = true;
            BtnStopMeeting.IsEnabled = false;
            BtnExport.IsEnabled = _captions.Count > 0;
            BtnClear.IsEnabled = _captions.Count > 0;

            Debug.WriteLine("[StreamingMeeting] Meeting stopped successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Stop meeting failed: {ex.Message}");
            await ShowErrorAsync($"Failed to stop meeting: {ex.Message}");
        }
    }

    // ========== 音频捕获初始化 ==========
    private bool InitializeAudioCapture()
    {
        try
        {
            // 获取默认麦克风
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _microphone = new WasapiCapture(device);

            // 设置数据回调
            _microphone.DataAvailable += OnAudioDataAvailable;
            _microphone.RecordingStopped += (s, e) =>
            {
                Debug.WriteLine("[StreamingMeeting] Recording stopped");
            };

            Debug.WriteLine($"[StreamingMeeting] Audio capture initialized: {device.FriendlyName}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Audio capture initialization failed: {ex.Message}");
            return false;
        }
    }

    // ========== 音频数据处理 ==========
    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (!_isRecording || _mainWindow == null || _microphone == null)
                return;

            // 转换音频格式：WasapiCapture 输出 -> 16kHz 16-bit mono PCM
            var samples = ConvertTo16kHzMono(e.Buffer, e.BytesRecorded, _microphone.WaveFormat);

            if (samples.Length == 0)
                return;

            // 将 float[] 转换为 int16[] 再转 base64
            var int16Samples = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                int16Samples[i] = (short)(Math.Clamp(samples[i], -1.0f, 1.0f) * 32767);
            }

            var bytes = new byte[int16Samples.Length * 2];
            Buffer.BlockCopy(int16Samples, 0, bytes, 0, bytes.Length);
            var base64Audio = Convert.ToBase64String(bytes);

            // 发送到 Worker
            _ = Task.Run(async () =>
            {
                try
                {
                    var audioCmd = new StreamingAudioCommand
                    {
                        audio_data = base64Audio,
                        sample_rate = 16000,
                        is_end = false
                    };

                    var json = JsonSerializer.Serialize(audioCmd, Contracts.AppJsonContext.Default.StreamingAudioCommand) + "\n";
                    await _mainWindow.SendJsonAsync(json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StreamingMeeting] Failed to send audio: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Audio processing failed: {ex.Message}");
        }
    }

    // ========== 音频格式转换 ==========
    private float[] ConvertTo16kHzMono(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        try
        {
            // 创建输入流
            using var inputStream = new RawSourceWaveStream(buffer, 0, bytesRecorded, sourceFormat);

            // 转换为 16kHz mono
            var resampler = new MediaFoundationResampler(inputStream, new WaveFormat(16000, 1));

            // 读取重采样后的数据
            var resampledBuffer = new byte[bytesRecorded * 2]; // 预留足够空间
            int resampledBytes = resampler.Read(resampledBuffer, 0, resampledBuffer.Length);

            // 转换为 float32
            int sampleCount = resampledBytes / 2; // 16-bit = 2 bytes per sample
            var samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(resampledBuffer, i * 2);
                samples[i] = sample / 32768f; // 归一化到 [-1.0, 1.0]
            }

            return samples;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Audio conversion failed: {ex.Message}");
            return Array.Empty<float>();
        }
    }

    // ========== 处理 Worker 的转录消息 ==========
    private void OnStreamingMessageReceived(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            string type = typeElement.GetString() ?? "";

            switch (type)
            {
                case "streaming_partial":
                    // 部分结果（实时显示）
                    string partialText = root.TryGetProperty("text", out var partialTextElem)
                        ? partialTextElem.GetString() ?? ""
                        : "";

                    int speakerId = root.TryGetProperty("speaker_id", out var speakerIdElem)
                        ? speakerIdElem.GetInt32()
                        : 0;

                    if (!string.IsNullOrWhiteSpace(partialText))
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            UpdatePartialTranscript(partialText, speakerId);
                        });
                    }
                    break;

                case "streaming_final":
                    // 最终结果
                    string finalText = root.TryGetProperty("text", out var finalTextElem)
                        ? finalTextElem.GetString() ?? ""
                        : "";

                    int finalSpeakerId = root.TryGetProperty("speaker_id", out var finalSpeakerIdElem)
                        ? finalSpeakerIdElem.GetInt32()
                        : 0;

                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            AddFinalTranscript(finalText, finalSpeakerId);
                        });
                    }
                    break;

                case "error":
                    string errorMsg = root.TryGetProperty("message", out var msgElement)
                        ? msgElement.GetString() ?? "Unknown error"
                        : "Unknown error";

                    Debug.WriteLine($"[StreamingMeeting] Worker error: {errorMsg}");
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await ShowErrorAsync($"Transcription error: {errorMsg}");
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Failed to process message: {ex.Message}");
        }
    }

    // ========== 转录文本更新 ==========
    private void UpdatePartialTranscript(string text, int speakerId)
    {
        // 更新或添加临时结果（最后一条）
        if (_captions.Count > 0 && _captions.Last().TextOpacity < 1.0)
        {
            // 更新现有临时结果
            _captions[_captions.Count - 1].Text = text;
        }
        else
        {
            // 添加新的临时结果
            var caption = new StreamingCaption
            {
                SpeakerName = $"Speaker {speakerId}",
                SpeakerColor = GetSpeakerColor(speakerId),
                Text = text,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                TextOpacity = 0.6 // 临时结果半透明
            };

            _captions.Add(caption);
        }

        // 自动滚动到底部
        if (CaptionsList.Items.Count > 0)
        {
            CaptionsList.ScrollIntoView(CaptionsList.Items[CaptionsList.Items.Count - 1]);
        }
    }

    private void AddFinalTranscript(string text, int speakerId)
    {
        // 如果最后一条是临时结果，替换为最终结果
        if (_captions.Count > 0 && _captions.Last().TextOpacity < 1.0)
        {
            _captions[_captions.Count - 1].Text = text;
            _captions[_captions.Count - 1].TextOpacity = 1.0;
        }
        else
        {
            // 添加新的最终结果
            var caption = new StreamingCaption
            {
                SpeakerName = $"Speaker {speakerId}",
                SpeakerColor = GetSpeakerColor(speakerId),
                Text = text,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                TextOpacity = 1.0
            };

            _captions.Add(caption);
        }

        _segmentCount++;
        TxtSegmentCount.Text = _segmentCount.ToString();

        // 更新说话人列表（如果是新说话人）
        EnsureSpeakerExists(speakerId);

        // 自动滚动到底部
        if (CaptionsList.Items.Count > 0)
        {
            CaptionsList.ScrollIntoView(CaptionsList.Items[CaptionsList.Items.Count - 1]);
        }

        Debug.WriteLine($"[StreamingMeeting] Final transcript: {text}");
    }

    // ========== 说话人管理 ==========
    private void EnsureSpeakerExists(int speakerId)
    {
        string speakerName = $"Speaker {speakerId}";

        if (!_speakers.Any(s => s.Name == speakerName))
        {
            var speaker = new Speaker
            {
                Name = speakerName,
                ColorBrush = GetSpeakerColor(speakerId)
            };

            _speakers.Add(speaker);
            TxtSpeakerCount.Text = _speakers.Count.ToString();
        }
    }

    private SolidColorBrush GetSpeakerColor(int speakerId)
    {
        var color = _speakerColors[speakerId % _speakerColors.Length];
        return new SolidColorBrush(color);
    }

    // ========== 定时器更新 ==========
    private void UpdateDuration(object? sender, object e)
    {
        var duration = DateTime.Now - _meetingStartTime;
        TxtDuration.Text = $"{duration:hh\\:mm\\:ss}";
    }

    // ========== 导出功能 ==========
    private async Task ExportTranscriptAsync()
    {
        try
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();

            // 初始化窗口句柄（WinUI 3 必需）
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Text File", new[] { ".txt" });
            savePicker.FileTypeChoices.Add("Markdown File", new[] { ".md" });
            savePicker.SuggestedFileName = $"meeting_transcript_{DateTime.Now:yyyyMMdd_HHmmss}";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            // 生成导出内容
            var content = GenerateTranscriptContent();

            await Windows.Storage.FileIO.WriteTextAsync(file, content);

            Debug.WriteLine($"[StreamingMeeting] Transcript exported to: {file.Path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Export failed: {ex.Message}");
            await ShowErrorAsync($"Export failed: {ex.Message}");
        }
    }

    private string GenerateTranscriptContent()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Meeting Transcript");
        sb.AppendLine($"Date: {_meetingStartTime:yyyy-MM-dd}");
        sb.AppendLine($"Duration: {DateTime.Now - _meetingStartTime:hh\\:mm\\:ss}");
        sb.AppendLine($"Speakers: {_speakers.Count}");
        sb.AppendLine($"Segments: {_segmentCount}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var caption in _captions.Where(c => c.TextOpacity >= 1.0))
        {
            sb.AppendLine($"[{caption.Timestamp}] {caption.SpeakerName}: {caption.Text}");
        }

        return sb.ToString();
    }

    // ========== 清理功能 ==========
    private void ClearAll()
    {
        _captions.Clear();
        _speakers.Clear();
        _segmentCount = 0;

        TxtDuration.Text = "00:00:00";
        TxtSpeakerCount.Text = "0";
        TxtSegmentCount.Text = "0";

        BtnExport.IsEnabled = false;
        BtnClear.IsEnabled = false;

        Debug.WriteLine("[StreamingMeeting] All data cleared");
    }

    private void CleanupResources()
    {
        try
        {
            _microphone?.Dispose();
            _microphone = null;

            Debug.WriteLine("[StreamingMeeting] Resources cleaned up");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMeeting] Cleanup failed: {ex.Message}");
        }
    }

    // ========== 错误对话框 ==========
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
