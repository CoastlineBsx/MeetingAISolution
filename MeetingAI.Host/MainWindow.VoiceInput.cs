using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

public sealed partial class MainWindow
{
    // 语音输入按钮点击事件
    public void BtnVoiceInput_Click(object sender, RoutedEventArgs e)
    {
        if (_isVoiceInputRecording)
        {
            // 正在录音，点击停止
            _ = StopVoiceInputRecordingAsync();
        }
        else
        {
            // 未录音，点击开始
            StartVoiceInputRecording();
        }
    }

    // 开始语音输入录音
    private void StartVoiceInputRecording()
    {
        try
        {
            // 创建临时文件
            _voiceInputTempFile = Path.Combine(Path.GetTempPath(), $"voice_input_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            // 获取默认麦克风设备
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            // 初始化麦克风录音
            _voiceInputCapture = new WasapiCapture(device);

            // 创建 WAV 文件写入器
            _voiceInputWriter = new WaveFileWriter(_voiceInputTempFile, _voiceInputCapture.WaveFormat);

            // 数据可用事件
            _voiceInputCapture.DataAvailable += (s, e) =>
            {
                if (_voiceInputWriter != null && e.BytesRecorded > 0)
                {
                    _voiceInputWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            // 录音停止事件
            _voiceInputCapture.RecordingStopped += (s, e) =>
            {
                _voiceInputWriter?.Dispose();
                _voiceInputWriter = null;
                _voiceInputCapture?.Dispose();
                _voiceInputCapture = null;
            };

            // 开始录音
            _voiceInputCapture.StartRecording();
            _isVoiceInputRecording = true;

            // 更新 UI（录音时按钮保持启用，以便再次点击停止）
            UpdateVoiceInputUI("🔴", "Recording...", true);

            Debug.WriteLine("[VoiceInput] 开始录音");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceInput] 录音启动失败: {ex.Message}");
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowErrorDialogAsync("录音失败", $"无法启动麦克风录音: {ex.Message}");
            });
        }
    }

    // 停止语音输入录音并开始转录
    private async Task StopVoiceInputRecordingAsync()
    {
        try
        {
            _isVoiceInputRecording = false;

            // 停止录音
            if (_voiceInputCapture != null)
            {
                _voiceInputCapture.StopRecording();
            }

            // 等待文件写入完成
            await Task.Delay(500);

            Debug.WriteLine("[VoiceInput] 录音停止，准备转录");

            // 检查录音文件
            if (string.IsNullOrEmpty(_voiceInputTempFile) || !File.Exists(_voiceInputTempFile))
            {
                Debug.WriteLine("[VoiceInput] 录音文件未找到");
                await ShowErrorDialogAsync("错误", "录音文件未找到");
                UpdateVoiceInputUI("🎤", "Voice Input", true);
                return;
            }

            // 检查文件大小
            var fileInfo = new FileInfo(_voiceInputTempFile);
            if (fileInfo.Length < 1024) // 小于 1KB
            {
                Debug.WriteLine($"[VoiceInput] 录音文件太小: {fileInfo.Length} bytes");
                await ShowErrorDialogAsync("错误", "录音时间太短，请重试");
                UpdateVoiceInputUI("🎤", "Voice Input", true);
                return;
            }

            Debug.WriteLine($"[VoiceInput] 录音文件: {_voiceInputTempFile}, 大小: {fileInfo.Length} bytes");

            // 开始转录
            await StartVoiceInputTranscriptionAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceInput] 停止录音失败: {ex.Message}");
            await ShowErrorDialogAsync("错误", $"停止录音失败: {ex.Message}");
            UpdateVoiceInputUI("🎤", "Voice Input", true);
        }
    }

    // 开始语音输入转录
    private async Task StartVoiceInputTranscriptionAsync()
    {
        try
        {
            // 更新 UI
            UpdateVoiceInputUI("⏳", "Transcribing...", false);
            _isVoiceInputTranscribing = true;
            _voiceInputTranscriptBuffer = new StringBuilder();

            // 确保管道连接
            await EnsurePipeAsync();

            // 注册消息处理器
            OpenVINOWhisperMessageHandler = OnVoiceInputMessageReceived;

            // 发送转录命令（使用自动语言检测）
            var cmd = new TranscribeOpenVINOCommand
            {
                path = _voiceInputTempFile!,
                language = "auto"
            };

            var json = JsonSerializer.Serialize(cmd, Contracts.AppJsonContext.Default.TranscribeOpenVINOCommand) + "\n";
            await _pipe!.WriteAsync(Encoding.UTF8.GetBytes(json));
            await _pipe.FlushAsync();

            Debug.WriteLine("[VoiceInput] 转录命令已发送");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceInput] 转录启动失败: {ex.Message}");
            await ShowErrorDialogAsync("转录失败", $"无法启动转录: {ex.Message}");
            UpdateVoiceInputUI("🎤", "Voice Input", true);
            _isVoiceInputTranscribing = false;
            OpenVINOWhisperMessageHandler = null;
        }
    }

    // 处理语音输入转录消息
    private void OnVoiceInputMessageReceived(string json)
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
                case "asr_segment":
                    // 收集转录片段
                    string text = root.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? ""
                        : "";

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _voiceInputTranscriptBuffer?.Append(text);
                        Debug.WriteLine($"[VoiceInput] 收到片段: {text}");
                    }
                    break;

                case "transcribe_complete":
                    // 转录完成
                    string finalTranscript = _voiceInputTranscriptBuffer?.ToString().Trim() ?? "";
                    Debug.WriteLine($"[VoiceInput] 转录完成: {finalTranscript}");

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // 填充到输入框
                        var chatPage = GetChatPage();
                        if (chatPage != null && !string.IsNullOrWhiteSpace(finalTranscript))
                        {
                            chatPage.TxtGraniteInputChat.Text = finalTranscript;
                            Debug.WriteLine("[VoiceInput] 文本已填充到输入框");
                        }

                        // 恢复 UI
                        UpdateVoiceInputUI("🎤", "Voice Input", true);
                        _isVoiceInputTranscribing = false;
                        OpenVINOWhisperMessageHandler = null;

                        // 清理临时文件
                        if (!string.IsNullOrEmpty(_voiceInputTempFile) && File.Exists(_voiceInputTempFile))
                        {
                            try
                            {
                                File.Delete(_voiceInputTempFile);
                                Debug.WriteLine("[VoiceInput] 临时文件已删除");
                            }
                            catch { }
                        }
                    });
                    break;

                case "error":
                    string errorMsg = root.TryGetProperty("message", out var msgElement)
                        ? msgElement.GetString() ?? "未知错误"
                        : "未知错误";

                    Debug.WriteLine($"[VoiceInput] 转录错误: {errorMsg}");

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await ShowErrorDialogAsync("转录错误", errorMsg);
                        UpdateVoiceInputUI("🎤", "Voice Input", true);
                        _isVoiceInputTranscribing = false;
                        OpenVINOWhisperMessageHandler = null;
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceInput] 处理消息失败: {ex.Message}");
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowErrorDialogAsync("错误", $"处理转录消息失败: {ex.Message}");
                UpdateVoiceInputUI("🎤", "Voice Input", true);
                _isVoiceInputTranscribing = false;
                OpenVINOWhisperMessageHandler = null;
            });
        }
    }

    // 更新语音输入按钮 UI
    private void UpdateVoiceInputUI(string icon, string status, bool enabled)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var chatPage = GetChatPage();
            if (chatPage != null)
            {
                chatPage.TxtVoiceIconChat.Text = icon;
                chatPage.TxtVoiceStatusChat.Text = status;
                chatPage.BtnVoiceInputChat.IsEnabled = enabled;
            }
        });
    }

    // 显示错误对话框
    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
