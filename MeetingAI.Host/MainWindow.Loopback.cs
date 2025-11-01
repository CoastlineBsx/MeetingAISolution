using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.Wave;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnLoopback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isLoopback)
            {
                await StartLoopbackAsync();
            }
            else
            {
                await StopLoopbackAndTranscribeAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 扬声器录音异常：{ex.Message}");
        }
    }

    private async Task StartLoopbackAsync()
    {
        _loopbackTempFile = Path.Combine(Path.GetTempPath(), $"speaker_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _loopback = new WasapiLoopbackCapture();

        _loopbackWriter = new WaveFileWriter(_loopbackTempFile, _loopback.WaveFormat);

        await AppendLineAsync($"[Host] 录制格式: {_loopback.WaveFormat.SampleRate}Hz, " +
            $"{_loopback.WaveFormat.BitsPerSample}bit, {_loopback.WaveFormat.Channels}声道, " +
            $"{_loopback.WaveFormat.Encoding}");

        _loopback.DataAvailable += (_, args) =>
        {
            _loopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };

        _loopback.RecordingStopped += async (_, __) =>
        {
            try { _loopbackWriter?.Dispose(); } catch { }
            _loopbackWriter = null;
            try { _loopback?.Dispose(); } catch { }
            _loopback = null;
            await AppendLineAsync($"[Host] 扬声器录音已停止，文件：{_loopbackTempFile}");
        };

        _loopback.StartRecording();
        _isLoopback = true;
        BtnLoopback.Content = "停止扬声器转录";
        await AppendLineAsync("[Host] 开始录制扬声器音频...");
    }

    private async Task StopLoopbackAndTranscribeAsync()
    {
        if (_loopback != null)
        {
            _loopback.StopRecording();
        }
        _isLoopback = false;
        BtnLoopback.Content = "扬声器转录";

        var path = _loopbackTempFile;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            await EnsurePipeAsync();

            string mode = CmbLoopbackMode.SelectedIndex switch
            {
                0 => "speech",
                1 => "music",
                2 => "mixed",
                _ => "auto"
            };

            string language = CmbLoopbackLanguage.SelectedIndex switch
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

            var cmd = new TranscribeFileCommand { path = path!, mode = mode, language = language };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();
            await AppendLineAsync($"[Host] 已发送扬声器录音转录命令（模式: {mode}，语言: {language}）：{path}");

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else
        {
            await AppendLineAsync("[Host] 未找到录制的文件，取消转录。");
        }
    }
}
