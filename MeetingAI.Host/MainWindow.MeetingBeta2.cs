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
    private async void BtnMeetingBeta2_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMeetingBeta2)
                await StartMeetingBeta2Async();
            else
                await StopMeetingBeta2AndTranscribeAsync();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] [Beta2] 异常: {ex.Message}");
        }
    }

    private async Task StartMeetingBeta2Async()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingBeta2MicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta2_mic_{timestamp}.wav");
        _meetingBeta2LoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta2_speaker_{timestamp}.wav");

        if (_selectedMeetingBeta2SpeakerId == null || _selectedMeetingBeta2SpeakerId == "default")
            _meetingBeta2Loopback = new WasapiLoopbackCapture();
        else
        {
            var en = new MMDeviceEnumerator(); var dev = en.GetDevice(_selectedMeetingBeta2SpeakerId); _meetingBeta2Loopback = new WasapiLoopbackCapture(dev);
        }
        _meetingBeta2LoopbackWriter = new WaveFileWriter(_meetingBeta2LoopbackTempFile, _meetingBeta2Loopback.WaveFormat);
        await AppendLineAsync($"[Host] [Beta2] 扬声器录制格式: {_meetingBeta2Loopback.WaveFormat.SampleRate}Hz, {_meetingBeta2Loopback.WaveFormat.BitsPerSample}bit, {_meetingBeta2Loopback.WaveFormat.Channels}");
        _meetingBeta2Loopback.DataAvailable += (_, args) => { _meetingBeta2LoopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded); };
        _meetingBeta2Loopback.RecordingStopped += async (_, __) => { try { _meetingBeta2LoopbackWriter?.Dispose(); } catch { } _meetingBeta2LoopbackWriter = null; await AppendLineAsync("[Host] [Beta2] 扬声器录音已停止"); };

        if (_selectedMeetingBeta2MicrophoneId == null || _selectedMeetingBeta2MicrophoneId == "default")
        {
            _meetingBeta2Microphone = new WasapiCapture();
            await AppendLineAsync("[Host] [Beta2] 使用默认麦克风");
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingBeta2MicrophoneId);
            _meetingBeta2Microphone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] [Beta2] 使用麦克风: {device.FriendlyName}");
        }
        _meetingBeta2MicrophoneWriter = new WaveFileWriter(_meetingBeta2MicrophoneTempFile, _meetingBeta2Microphone.WaveFormat);
        await AppendLineAsync($"[Host] [Beta2] 麦克风录制格式: {_meetingBeta2Microphone.WaveFormat.SampleRate}Hz, {_meetingBeta2Microphone.WaveFormat.BitsPerSample}bit, {_meetingBeta2Microphone.WaveFormat.Channels}");
        _meetingBeta2Microphone.DataAvailable += (_, args) => { _meetingBeta2MicrophoneWriter?.Write(args.Buffer, 0, args.BytesRecorded); };
        _meetingBeta2Microphone.RecordingStopped += async (_, __) => { try { _meetingBeta2MicrophoneWriter?.Dispose(); } catch { } _meetingBeta2MicrophoneWriter = null; await AppendLineAsync("[Host] [Beta2] 麦克风录音已停止"); };

        _meetingBeta2Loopback.StartRecording();
        _meetingBeta2Microphone.StartRecording();
        _isMeetingBeta2 = true;
        BtnMeetingBeta2.Content = "🛑 停止综合录音Beta2";
        await AppendLineAsync("[Host] [Beta2] 启动完成 (方案A)");
    }

    private async Task StopMeetingBeta2AndTranscribeAsync()
    {
        if (_meetingBeta2Loopback != null) _meetingBeta2Loopback.StopRecording();
        if (_meetingBeta2Microphone != null) _meetingBeta2Microphone.StopRecording();
        _isMeetingBeta2 = false;
        BtnMeetingBeta2.Content = "📞 综合转录Beta2";
        await Task.Delay(500);

        if (string.IsNullOrEmpty(_meetingBeta2MicrophoneTempFile) || !File.Exists(_meetingBeta2MicrophoneTempFile)) { await AppendLineAsync("[Host] [Beta2] 未找到麦克风录制文件 "); return; }
        if (string.IsNullOrEmpty(_meetingBeta2LoopbackTempFile) || !File.Exists(_meetingBeta2LoopbackTempFile)) { await AppendLineAsync("[Host] [Beta2] 未找到扬声器录制文件 "); return; }

        await AppendLineAsync($"[Host] [Beta2] 麦克风: {_meetingBeta2MicrophoneTempFile}");
        await AppendLineAsync($"[Host] [Beta2] 扬声器: {_meetingBeta2LoopbackTempFile}");

        TimeSpan micDur2, spkDur2;
        using (var mr = new AudioFileReader(_meetingBeta2MicrophoneTempFile)) { micDur2 = mr.TotalTime; }
        using (var sr = new AudioFileReader(_meetingBeta2LoopbackTempFile)) { spkDur2 = sr.TotalTime; }
        double diff = micDur2.TotalSeconds - spkDur2.TotalSeconds;
        if (diff > 0.1)
        {
            await AppendLineAsync($"[Host] [Beta2] 扬声器滞后 {diff:F2}s, 预添加静音 ");
            await PrependSilenceToWavFileAsync(_meetingBeta2LoopbackTempFile, diff);
        }

        var mixed = await MixAudioFilesAsync(_meetingBeta2MicrophoneTempFile, _meetingBeta2LoopbackTempFile);
        if (string.IsNullOrEmpty(mixed) || !File.Exists(mixed)) { await AppendLineAsync("[Host] [Beta2] 混音失败 "); return; }

        string mode = CmbMeetingBeta2Mode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string language = CmbMeetingBeta2Language.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };

        await EnsurePipeAsync();
        var cmd = new TranscribeFileCommand { path = mixed!, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] [Beta2] 已发送混音转录 (mode={mode}, lang={language}): {mixed}");
        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
