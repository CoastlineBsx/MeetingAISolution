using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnMeetingBeta_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMeetingBeta)
            {
                await StartMeetingBetaAsync();
            }
            else
            {
                await StopMeetingBetaAndTranscribeAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 综合模式（beta）录音异常：{ex.Message}");
        }
    }

    private async Task StartMeetingBetaAsync()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingBetaMicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta_mic_{timestamp}.wav");
        _meetingBetaLoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta_speaker_{timestamp}.wav");

        if (_selectedMeetingBetaSpeakerId == null || _selectedMeetingBetaSpeakerId == "default")
        {
            _meetingBetaLoopback = new WasapiLoopbackCapture();
            await AppendLineAsync("[Host] [Beta] 使用默认扬声器");
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingBetaSpeakerId);
            _meetingBetaLoopback = new WasapiLoopbackCapture(device);
            await AppendLineAsync($"[Host] [Beta] 使用扬声器: {device.FriendlyName}");
        }
        _meetingBetaLoopbackWriter = new WaveFileWriter(_meetingBetaLoopbackTempFile, _meetingBetaLoopback.WaveFormat);

        await AppendLineAsync($"[Host] [Beta] 扬声器录制格式: {_meetingBetaLoopback.WaveFormat.SampleRate}Hz, " +
            $"{_meetingBetaLoopback.WaveFormat.BitsPerSample}bit, {_meetingBetaLoopback.WaveFormat.Channels}声道");

        _meetingBetaLoopback.DataAvailable += (_, args) =>
        {
            _meetingBetaLoopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };

        _meetingBetaLoopback.RecordingStopped += async (_, __) =>
        {
            try { _meetingBetaLoopbackWriter?.Dispose(); } catch { }
            _meetingBetaLoopbackWriter = null;
            await AppendLineAsync($"[Host] [Beta] 扬声器录音已停止");
        };

        if (_selectedMeetingBetaMicrophoneId == null || _selectedMeetingBetaMicrophoneId == "default")
        {
            _meetingBetaMicrophone = new WasapiCapture();
            await AppendLineAsync("[Host] [Beta] 使用默认麦克风");
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingBetaMicrophoneId);
            _meetingBetaMicrophone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] [Beta] 使用麦克风: {device.FriendlyName}");
        }

        _meetingBetaMicrophoneWriter = new WaveFileWriter(_meetingBetaMicrophoneTempFile, _meetingBetaMicrophone.WaveFormat);

        await AppendLineAsync($"[Host] [Beta] 麦克风录制格式: {_meetingBetaMicrophone.WaveFormat.SampleRate}Hz, " +
            $"{_meetingBetaMicrophone.WaveFormat.BitsPerSample}bit, {_meetingBetaMicrophone.WaveFormat.Channels}声道");

        _meetingBetaMicrophone.DataAvailable += (_, args) =>
        {
            _meetingBetaMicrophoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };

        _meetingBetaMicrophone.RecordingStopped += async (_, __) =>
        {
            try { _meetingBetaMicrophoneWriter?.Dispose(); } catch { }
            _meetingBetaMicrophoneWriter = null;
            await AppendLineAsync($"[Host] [Beta] 麦克风录音已停止");
        };

        var startTime = DateTime.Now;
        _meetingBetaLoopback.StartRecording();
        _meetingBetaMicrophone.StartRecording();
        var endTime = DateTime.Now;

        var delay = (endTime - startTime).TotalMilliseconds;
        await AppendLineAsync($"[Host] [Beta] ✓ 两路音频同步启动（延迟 {delay:F2}ms）");

        _isMeetingBeta = true;
        BtnMeetingBeta.Content = "🛑 停止综合录音Beta";
        await AppendLineAsync("[Host] [Beta] 综合模式（beta）已启动（方案A：硬件级同步）");
    }

    private async Task StopMeetingBetaAndTranscribeAsync()
    {
        if (_meetingBetaLoopback != null)
        {
            _meetingBetaLoopback.StopRecording();
        }
        if (_meetingBetaMicrophone != null)
        {
            _meetingBetaMicrophone.StopRecording();
        }

        _isMeetingBeta = false;
        BtnMeetingBeta.Content = "📞 综合转录Beta";
        await Task.Delay(500);

        if (string.IsNullOrEmpty(_meetingBetaMicrophoneTempFile) || !File.Exists(_meetingBetaMicrophoneTempFile))
        {
            await AppendLineAsync("[Host] [Beta] 未找到麦克风录制文件，取消转录。");
            return;
        }
        if (string.IsNullOrEmpty(_meetingBetaLoopbackTempFile) || !File.Exists(_meetingBetaLoopbackTempFile))
        {
            await AppendLineAsync("[Host] [Beta] 未找到扬声器录制文件，取消转录。");
            return;
        }

        await AppendLineAsync($"[Host] [Beta] 麦克风文件: {_meetingBetaMicrophoneTempFile}");
        await AppendLineAsync($"[Host] [Beta] 扬声器文件: {_meetingBetaLoopbackTempFile}");

        var micDur = new AudioFileReader(_meetingBetaMicrophoneTempFile).TotalTime;
        var spkDur = new AudioFileReader(_meetingBetaLoopbackTempFile).TotalTime;
        double durationDiff = micDur.TotalSeconds - spkDur.TotalSeconds;
        if (durationDiff > 0.1)
        {
            await AppendLineAsync($"[Host] [Beta] 扬声器文件提前 {durationDiff:F2}s, 预添加静音");
            await PrependSilenceToWavFileAsync(_meetingBetaLoopbackTempFile, durationDiff);
        }

        var mixedFile = await MixAudioFilesAsync(_meetingBetaMicrophoneTempFile, _meetingBetaLoopbackTempFile);
        if (string.IsNullOrEmpty(mixedFile) || !File.Exists(mixedFile))
        {
            await AppendLineAsync("[Host] [Beta] 混音失败，取消转录。");
            return;
        }

        string mode = CmbMeetingBetaMode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string language = CmbMeetingBetaLanguage.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };

        await EnsurePipeAsync();
        var cmd = new TranscribeFileCommand { path = mixedFile, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] [Beta] 已发送综合模式转录命令（模式: {mode}，语言: {language}）：{mixedFile}");

        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
