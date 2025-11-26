using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FFMpegCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAI.Host.Pages;

public sealed partial class OpenVINOWhisperPage : Page
{
    private string? _selectedAudioPath;
    private readonly ObservableCollection<TranscriptSegment> _segments = new();
    private MainWindow? _mainWindow;
    private string _currentMode = "file"; // file, speaker, microphone
    private bool _isRecording = false;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiCapture? _microphoneCapture;
    private WaveFileWriter? _waveWriter;
    private string? _recordingTempFile;

    public OpenVINOWhisperPage()
    {
        InitializeComponent();
        ResultListView.ItemsSource = _segments;

        // 获取 MainWindow 引用
        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    private void RadioTranscriptionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioTranscriptionMode?.SelectedItem is RadioButton selectedButton)
        {
            _currentMode = selectedButton.Tag?.ToString() ?? "file";

            // 根据模式更新 UI
            switch (_currentMode)
            {
                case "file":
                    FileInfoBar.Visibility = Visibility.Visible;
                    BtnStart.Content = "🚀 开始转录";
                    break;
                case "speaker":
                    FileInfoBar.Visibility = Visibility.Collapsed;
                    BtnStart.Content = "🔴 开始录音（扬声器）";
                    break;
                case "microphone":
                    FileInfoBar.Visibility = Visibility.Collapsed;
                    BtnStart.Content = "🔴 开始录音（麦克风）";
                    break;
            }
        }
    }

    private async void BtnSelectFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".flac");
            picker.FileTypeFilter.Add(".ogg");

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            _selectedAudioPath = file.Path;
            TxtSelectedFile.Text = file.Path;
            FileInfoBar.Severity = InfoBarSeverity.Success;
            FileInfoBar.Title = "✅ 已选择文件";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"文件选择失败：{ex.Message}");
        }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        // 如果正在录音，先停止
        if (_isRecording)
        {
            StopRecording();
            return;
        }

        // 根据模式执行不同的操作
        switch (_currentMode)
        {
            case "file":
                await StartFileTranscription();
                break;
            case "speaker":
                StartSpeakerRecording();
                break;
            case "microphone":
                StartMicrophoneRecording();
                break;
        }
    }

    private async Task StartFileTranscription()
    {
        if (string.IsNullOrEmpty(_selectedAudioPath))
        {
            await ShowErrorAsync("请先选择音频文件");
            return;
        }

        if (_mainWindow == null)
        {
            await ShowErrorAsync("无法访问主窗口");
            return;
        }

        try
        {
            // 清空之前的结果
            _segments.Clear();
            StatsPanel.Visibility = Visibility.Collapsed;

            // 禁用按钮，显示进度
            BtnStart.IsEnabled = false;
            BtnSelectFile.IsEnabled = false;
            BtnExport.IsEnabled = false;
            BtnClear.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            TxtProgress.Text = "准备中...";

            // 注册消息处理器
            _mainWindow.OpenVINOWhisperMessageHandler = OnMessageReceived;

            // 转换音频格式（如果需要）
            string audioPath = _selectedAudioPath;
            if (_selectedAudioPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                _selectedAudioPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                _selectedAudioPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                _selectedAudioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                TxtProgress.Text = "正在转换音频格式...";
                ProgressBar.Value = 5;
                audioPath = await ConvertToWavAsync(_selectedAudioPath);
                if (string.IsNullOrEmpty(audioPath))
                {
                    await ShowErrorAsync("音频格式转换失败");
                    ResetUI();
                    return;
                }
            }

            // 获取语言设置
            string language = CmbLanguage.SelectedIndex switch
            {
                0 => "auto",
                1 => "zh",
                2 => "en",
                3 => "ja",
                4 => "ko",
                5 => "es",
                6 => "fr",
                7 => "de",
                _ => "auto"
            };

            // 发送转录命令
            var cmd = new TranscribeOpenVINOCommand
            {
                path = audioPath,
                language = language
            };

            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeOpenVINOCommand) + "\n";
            await _mainWindow.SendJsonAsync(json);

            TxtProgress.Text = $"转录中... (语言: {language})";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"转录启动失败：{ex.Message}");
            ResetUI();
            _mainWindow.OpenVINOWhisperMessageHandler = null;
        }
    }

    private void OnMessageReceived(string json)
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
                case "progress":
                    if (root.TryGetProperty("value", out var valueElement))
                    {
                        int progress = valueElement.GetInt32();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            ProgressBar.Value = progress;
                            TxtProgress.Text = progress switch
                            {
                                <= 10 => "加载音频中...",
                                <= 20 => "加载模型中...",
                                <= 30 => "准备推理...",
                                <= 90 => "推理中...",
                                _ => "处理结果中..."
                            };
                        });
                    }
                    break;

                case "asr_segment":
                    string text = root.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? ""
                        : "";
                    int t0_ms = root.TryGetProperty("t0_ms", out var t0Element)
                        ? t0Element.GetInt32()
                        : 0;
                    int t1_ms = root.TryGetProperty("t1_ms", out var t1Element)
                        ? t1Element.GetInt32()
                        : 0;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var segment = new TranscriptSegment
                        {
                            Text = text,
                            StartMs = t0_ms,
                            EndMs = t1_ms,
                            ShowTimestamp = ToggleShowTimestamps.IsOn ? Visibility.Visible : Visibility.Collapsed
                        };
                        _segments.Add(segment);

                        // 自动滚动到最新
                        if (_segments.Count > 0)
                        {
                            ResultListView.ScrollIntoView(_segments[_segments.Count - 1]);
                        }
                    });
                    break;

                case "transcribe_complete":
                    int segmentCount = root.TryGetProperty("segments", out var segElement)
                        ? segElement.GetInt32()
                        : 0;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressBar.Value = 100;
                        TxtProgress.Text = $"✅ 转录完成！共 {segmentCount} 个片段";

                        // 显示统计信息
                        if (_segments.Count > 0)
                        {
                            double totalSeconds = _segments[_segments.Count - 1].EndMs / 1000.0;
                            TxtStats.Text = $"总片段数：{_segments.Count}\n" +
                                          $"音频时长：{totalSeconds:F2} 秒\n" +
                                          $"平均片段长度：{totalSeconds / _segments.Count:F2} 秒";
                            StatsPanel.Visibility = Visibility.Visible;
                        }

                        BtnExport.IsEnabled = _segments.Count > 0;
                        BtnClear.IsEnabled = _segments.Count > 0;

                        // 2秒后重置UI
                        Task.Delay(2000).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(ResetUI);
                        });

                        // 清理消息处理器
                        if (_mainWindow != null)
                        {
                            _mainWindow.OpenVINOWhisperMessageHandler = null;
                        }
                    });
                    break;

                case "error":
                    string errorMsg = root.TryGetProperty("message", out var msgElement)
                        ? msgElement.GetString() ?? "未知错误"
                        : "未知错误";

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await ShowErrorAsync($"Worker 错误：{errorMsg}");
                        ResetUI();

                        // 清理消息处理器
                        if (_mainWindow != null)
                        {
                            _mainWindow.OpenVINOWhisperMessageHandler = null;
                        }
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowErrorAsync($"处理消息失败：{ex.Message}");
                ResetUI();

                // 清理消息处理器
                if (_mainWindow != null)
                {
                    _mainWindow.OpenVINOWhisperMessageHandler = null;
                }
            });
        }
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var savePicker = new FileSavePicker();
            savePicker.SuggestedFileName = $"transcript_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            savePicker.FileTypeChoices.Add("文本文件", new[] { ".txt" });

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"OpenVINO Whisper 转录结果");
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"总片段数：{_segments.Count}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            foreach (var segment in _segments)
            {
                if (ToggleShowTimestamps.IsOn)
                {
                    sb.AppendLine(segment.Timestamp);
                }
                sb.AppendLine(segment.Text);
                sb.AppendLine();
            }

            await FileIO.WriteTextAsync(file, sb.ToString());

            var dialog = new ContentDialog
            {
                Title = "导出成功",
                Content = $"转录结果已保存至：\n{file.Path}",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"导出失败：{ex.Message}");
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _segments.Clear();
        StatsPanel.Visibility = Visibility.Collapsed;
        BtnExport.IsEnabled = false;
        BtnClear.IsEnabled = false;
    }

    private void ResetUI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BtnStart.IsEnabled = true;
            BtnSelectFile.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        });
    }

    private async Task<string?> ConvertToWavAsync(string sourcePath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var tempWav = Path.Combine(Path.GetTempPath(), $"openvino_whisper_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(tempWav, true, options => options
                        .WithAudioCodec("pcm_s16le")
                        .WithAudioSamplingRate(16000)
                        .WithCustomArgument("-ac 1")
                        .WithCustomArgument("-af \"highpass=f=200,lowpass=f=3000,loudnorm=I=-16:TP=-1.5:LRA=11\"")
                    )
                    .ProcessAsynchronously();

                return tempWav;
            }
            catch
            {
                return null;
            }
        });
    }

    private async Task ShowErrorAsync(string message)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "错误",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        });
    }

    private void StartSpeakerRecording()
    {
        try
        {
            // 创建临时文件
            _recordingTempFile = Path.Combine(Path.GetTempPath(), $"openvino_speaker_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            // 初始化扬声器录音（loopback）
            _loopbackCapture = new WasapiLoopbackCapture();

            // 创建 WAV 文件写入器
            _waveWriter = new WaveFileWriter(_recordingTempFile, _loopbackCapture.WaveFormat);

            // 数据可用事件
            _loopbackCapture.DataAvailable += (s, e) =>
            {
                if (_waveWriter != null && e.BytesRecorded > 0)
                {
                    _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            // 录音停止事件
            _loopbackCapture.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Dispose();
                _waveWriter = null;
                _loopbackCapture?.Dispose();
                _loopbackCapture = null;
            };

            // 开始录音
            _loopbackCapture.StartRecording();
            _isRecording = true;

            // 更新 UI
            DispatcherQueue.TryEnqueue(() =>
            {
                BtnStart.Content = "⏺️ 录音中...";
                BtnStart.IsEnabled = true;
                BtnStopRecording.Visibility = Visibility.Visible;
                BtnSelectFile.IsEnabled = false;
                BtnExport.IsEnabled = false;
                BtnClear.IsEnabled = false;
                FileInfoBar.Title = "🔴 正在录音（扬声器）";
                FileInfoBar.Severity = InfoBarSeverity.Warning;
                FileInfoBar.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowErrorAsync($"扬声器录音启动失败：{ex.Message}");
            });
        }
    }

    private void StartMicrophoneRecording()
    {
        try
        {
            // 创建临时文件
            _recordingTempFile = Path.Combine(Path.GetTempPath(), $"openvino_mic_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            // 获取默认麦克风设备
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            // 初始化麦克风录音
            _microphoneCapture = new WasapiCapture(device);

            // 创建 WAV 文件写入器
            _waveWriter = new WaveFileWriter(_recordingTempFile, _microphoneCapture.WaveFormat);

            // 数据可用事件
            _microphoneCapture.DataAvailable += (s, e) =>
            {
                if (_waveWriter != null && e.BytesRecorded > 0)
                {
                    _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            // 录音停止事件
            _microphoneCapture.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Dispose();
                _waveWriter = null;
                _microphoneCapture?.Dispose();
                _microphoneCapture = null;
            };

            // 开始录音
            _microphoneCapture.StartRecording();
            _isRecording = true;

            // 更新 UI
            DispatcherQueue.TryEnqueue(() =>
            {
                BtnStart.Content = "⏺️ 录音中...";
                BtnStart.IsEnabled = true;
                BtnStopRecording.Visibility = Visibility.Visible;
                BtnSelectFile.IsEnabled = false;
                BtnExport.IsEnabled = false;
                BtnClear.IsEnabled = false;
                FileInfoBar.Title = "🔴 正在录音（麦克风）";
                FileInfoBar.Severity = InfoBarSeverity.Warning;
                FileInfoBar.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowErrorAsync($"麦克风录音启动失败：{ex.Message}");
            });
        }
    }

    private async void StopRecording()
    {
        try
        {
            _isRecording = false;

            // 停止录音
            if (_loopbackCapture != null)
            {
                _loopbackCapture.StopRecording();
            }
            if (_microphoneCapture != null)
            {
                _microphoneCapture.StopRecording();
            }

            // 等待文件写入完成
            await Task.Delay(500);

            // 更新 UI
            BtnStart.Content = _currentMode == "speaker" ? "🔴 开始录音（扬声器）" : "🔴 开始录音（麦克风）";
            BtnStopRecording.Visibility = Visibility.Collapsed;
            FileInfoBar.Title = "✅ 录音完成";
            FileInfoBar.Severity = InfoBarSeverity.Success;

            // 检查录音文件
            if (string.IsNullOrEmpty(_recordingTempFile) || !File.Exists(_recordingTempFile))
            {
                await ShowErrorAsync("录音文件未找到");
                ResetUI();
                return;
            }

            // 显示文件信息
            var fileInfo = new FileInfo(_recordingTempFile);
            TxtSelectedFile.Text = $"{_recordingTempFile} ({fileInfo.Length / 1024} KB)";

            // 自动开始转录
            _selectedAudioPath = _recordingTempFile;
            await StartFileTranscription();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"停止录音失败：{ex.Message}");
            ResetUI();
        }
    }

    private void BtnStopRecording_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
    }
}

// 数据模型
public class TranscriptSegment
{
    public string Text { get; set; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public Visibility ShowTimestamp { get; set; } = Visibility.Visible;

    public string Timestamp =>
        $"[{FormatTime(StartMs)} - {FormatTime(EndMs)}]";

    private static string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }
}
