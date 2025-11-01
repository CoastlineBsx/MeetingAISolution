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
    private async void BtnMeeting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMeeting)
                await StartMeetingAsync();
            else
                await StopMeetingAndTranscribeAsync();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 综合模式录音异常：{ex.Message}");
        }
    }

    private async Task StartMeetingAsync()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingMicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_mic_{timestamp}.wav");
        _meetingLoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_speaker_{timestamp}.wav");

        MMDevice? defaultMultimedia = null;
        MMDevice? defaultCommunications = null;
        try
        {
            var dbgEnum = new MMDeviceEnumerator();
            defaultMultimedia = dbgEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultCommunications = dbgEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            await AppendLineAsync($"[Host] [DEBUG] 默认多媒体输出: {defaultMultimedia.FriendlyName} ({defaultMultimedia.ID})");
            await AppendLineAsync($"[Host] [DEBUG] 默认通信输出: {defaultCommunications.FriendlyName} ({defaultCommunications.ID})");
        }
        catch { }

        if (_selectedMeetingSpeakerId == null || _selectedMeetingSpeakerId == "default")
        {
            _meetingLoopback = new WasapiLoopbackCapture();
            await AppendLineAsync("[Host] 综合模式使用默认扬声器");
            if (defaultMultimedia != null)
            {
                await AppendLineAsync($"[Host] [DEBUG] 选定扬声器: {defaultMultimedia.FriendlyName} ({defaultMultimedia.ID}) | 默认多媒体=True, 默认通信={(defaultCommunications != null && defaultMultimedia.ID == defaultCommunications.ID)}");
            }
        }
        else
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingSpeakerId);
            _meetingLoopback = new WasapiLoopbackCapture(device);
            await AppendLineAsync($"[Host] 综合模式使用扬声器: {device.FriendlyName}");
            var isDefaultMM = defaultMultimedia != null && string.Equals(device.ID, defaultMultimedia.ID, StringComparison.OrdinalIgnoreCase);
            var isDefaultComm = defaultCommunications != null && string.Equals(device.ID, defaultCommunications.ID, StringComparison.OrdinalIgnoreCase);
            await AppendLineAsync($"[Host] [DEBUG] 选定扬声器: {device.FriendlyName} ({device.ID}) | 默认多媒体={isDefaultMM}, 默认通信={isDefaultComm}");
        }
        _meetingLoopbackWriter = new WaveFileWriter(_meetingLoopbackTempFile, _meetingLoopback.WaveFormat);

        await AppendLineAsync($"[Host] 扬声器录制格式: {_meetingLoopback.WaveFormat.SampleRate}Hz, " +
            $"{_meetingLoopback.WaveFormat.BitsPerSample}bit, {_meetingLoopback.WaveFormat.Channels}声道");

        _meetingLoopback.DataAvailable += async (_, args) =>
        {
            lock (_meetingSyncLock)
            {
                _meetingLoopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded);
                _meetingLoopbackTotalBytes += args.BytesRecorded;
                _meetingLoopbackLastDataTime = DateTime.Now;
                if (!_meetingLoopbackHasData)
                {
                    _meetingLoopbackHasData = true;
                    float peak = 0f;
                    try { peak = EstimatePeak(args.Buffer, args.BytesRecorded, _meetingLoopback.WaveFormat); } catch { }
                    _ = AppendLineAsync($"[Host] [DEBUG] ✓ 扬声器首次收到数据：{args.BytesRecorded} 字节，峰值≈{peak:F3}");
                }
            }
        };

        _meetingLoopback.RecordingStopped += async (_, __) =>
        {
            try { _meetingLoopbackWriter?.Dispose(); } catch { }
            _meetingLoopbackWriter = null;
            await AppendLineAsync($"[Host] 扬声器录音已停止");
            await AppendLineAsync($"[Host] [DEBUG] 扬声器录音停止：总字节数 {_meetingLoopbackTotalBytes}");
        };

        if (_selectedMeetingMicrophoneId == null || _selectedMeetingMicrophoneId == "default")
        {
            _meetingMicrophone = new WasapiCapture();
            await AppendLineAsync("[Host] 综合模式使用默认麦克风");
        }
        else
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingMicrophoneId);
            _meetingMicrophone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] 综合模式使用麦克风: {device.FriendlyName}");
        }

        _meetingMicrophoneWriter = new WaveFileWriter(_meetingMicrophoneTempFile, _meetingMicrophone.WaveFormat);

        await AppendLineAsync($"[Host] 麦克风录制格式: {_meetingMicrophone.WaveFormat.SampleRate}Hz, " +
            $"{_meetingMicrophone.WaveFormat.BitsPerSample}bit, {_meetingMicrophone.WaveFormat.Channels}声道");

        var _meetingMicrophoneHasData = false;
        _meetingMicrophone.DataAvailable += (_, args) =>
        {
            _meetingMicrophoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
            if (!_meetingMicrophoneHasData)
            {
                _meetingMicrophoneHasData = true;
                _ = AppendLineAsync($"[Host] [DEBUG] ✓ 麦克风首次收到数据：{args.BytesRecorded} 字节");
            }
        };

        _meetingMicrophone.RecordingStopped += async (_, __) =>
        {
            try { _meetingMicrophoneWriter?.Dispose(); } catch { }
            _meetingMicrophoneWriter = null;
            await AppendLineAsync($"[Host] 麦克风录音已停止");
        };

        _meetingStartTime = DateTime.Now;
        _meetingLoopbackLastDataTime = _meetingStartTime;
        _meetingLoopbackTotalBytes = 0;
        _meetingSyncFillCount = 0;
        _meetingLoopbackHasData = false;

        var startTime = DateTime.Now;
        _meetingLoopback.StartRecording();
        _meetingMicrophone.StartRecording();
        var endTime = DateTime.Now;

        var delay = (endTime - startTime).TotalMilliseconds;
        if (delay > 10)
            await AppendLineAsync($"[Host] 警告：两路音频启动延迟 {delay:F2}ms");
        else
            await AppendLineAsync($"[Host] ✓ 两路音频同步启动（延迟 {delay:F2}ms）");

        _meetingSyncTimer = new System.Timers.Timer(20);
        _meetingSyncTimer.Elapsed += MeetingSyncTimer_Elapsed;
        _meetingSyncTimer.Start();
        await AppendLineAsync("[Host] ✓ 定时器同步已启动（方案B）");

        _isMeeting = true;
        BtnMeeting.Content = "🛑 停止综合录音";
        await AppendLineAsync("[Host] 综合模式已启动（麦克风 + 扬声器同时录音）");
    }

    private void MeetingSyncTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isMeeting || _meetingLoopbackWriter == null || _meetingLoopback == null)
            return;

        lock (_meetingSyncLock)
        {
            try
            {
                var now = DateTime.Now;
                if (!_meetingLoopbackHasData)
                {
                    return;
                }

                var format = _meetingLoopback.WaveFormat;
                var timeSinceLastData = (now - _meetingLoopbackLastDataTime).TotalSeconds;
                double elapsedSeconds = (now - _meetingStartTime).TotalSeconds;
                long expectedBytes = (long)(elapsedSeconds * format.SampleRate * format.BlockAlign);
                long missingBytes = expectedBytes - _meetingLoopbackTotalBytes;

                long threshold;
                if (timeSinceLastData < 0.5)
                {
                    threshold = format.SampleRate * format.BlockAlign / 5;
                }
                else
                {
                    threshold = format.SampleRate * format.BlockAlign / 10;
                }

                if (missingBytes > threshold)
                {
                    byte[] silenceBuffer = new byte[missingBytes];
                    Array.Fill<byte>(silenceBuffer, 0);
                    _meetingLoopbackWriter.Write(silenceBuffer, 0, silenceBuffer.Length);
                    _meetingLoopbackTotalBytes += missingBytes;
                    _meetingSyncFillCount++;

                    double fillDuration = (double)missingBytes / (format.SampleRate * format.BlockAlign);
                    double timeSinceLastDataNow = (DateTime.Now - _meetingLoopbackLastDataTime).TotalSeconds;
                    _ = AppendLineAsync($"[Host] [DEBUG] 定时器填充: {fillDuration:F2}秒静音（第{_meetingSyncFillCount}次，距上次数据{timeSinceLastDataNow:F2}秒）");
                }
            }
            catch
            {
            }
        }
    }

    private async Task StopMeetingAndTranscribeAsync()
    {
        if (_meetingSyncTimer != null)
        {
            _meetingSyncTimer.Stop();
            _meetingSyncTimer.Dispose();
            _meetingSyncTimer = null;
            await AppendLineAsync($"[Host] [方案B] ✓ 定时器同步已停止（共填充 {_meetingSyncFillCount} 次）");
        }

        if (_meetingLoopback != null)
        {
            _meetingLoopback.StopRecording();
        }
        if (_meetingMicrophone != null)
        {
            _meetingMicrophone.StopRecording();
        }

        _isMeeting = false;
        BtnMeeting.Content = "📞 综合转录";

        await Task.Delay(500);

        if (string.IsNullOrEmpty(_meetingMicrophoneTempFile) || !File.Exists(_meetingMicrophoneTempFile))
        {
            await AppendLineAsync("[Host] 未找到麦克风录制文件，取消转录。");
            return;
        }
        if (string.IsNullOrEmpty(_meetingLoopbackTempFile) || !File.Exists(_meetingLoopbackTempFile))
        {
            await AppendLineAsync("[Host] 未找到扬声器录制文件，取消转录。");
            return;
        }

        await AppendLineAsync($"[Host] 麦克风文件: {_meetingMicrophoneTempFile}");
        await AppendLineAsync($"[Host] 扬声器文件: {_meetingLoopbackTempFile}");

        var micFileInfo = new FileInfo(_meetingMicrophoneTempFile);
        var spkFileInfo = new FileInfo(_meetingLoopbackTempFile);
        await AppendLineAsync($"[Host] [DEBUG] 麦克风文件大小: {micFileInfo.Length / 1024.0:F2} KB");
        await AppendLineAsync($"[Host] [DEBUG] 扬声器文件大小: {spkFileInfo.Length / 1024.0:F2} KB");

        if (spkFileInfo.Length < 1024)
        {
            await AppendLineAsync($"[Host] [DEBUG] ⚠️ 警告：扬声器文件过小（< 1KB），可能没有录到声音！");
        }

        await AppendLineAsync("[Host] [方案B] 检查录制文件时长...");

        TimeSpan micDur, spkDur;
        using (var mr = new AudioFileReader(_meetingMicrophoneTempFile)) { micDur = mr.TotalTime; }
        using (var sr = new AudioFileReader(_meetingLoopbackTempFile)) { spkDur = sr.TotalTime; }

        await AppendLineAsync($"[Host] [方案B] 麦克风: {micDur.TotalSeconds:F2}秒");
        await AppendLineAsync($"[Host] [方案B] 扬声器: {spkDur.TotalSeconds:F2}秒");
        await AppendLineAsync($"[Host] [方案B] 差值: {Math.Abs(micDur.TotalSeconds - spkDur.TotalSeconds):F3}秒");

        await AppendLineAsync("[Host] [方案B] 正在混音两路音频（定时器已确保等长）...");
        var mixedFile = await MixAudioFilesAsync(_meetingMicrophoneTempFile, _meetingLoopbackTempFile);

        if (string.IsNullOrEmpty(mixedFile) || !File.Exists(mixedFile))
        {
            await AppendLineAsync("[Host] 混音失败，取消转录。");
            return;
        }

        await AppendLineAsync($"[Host] 混音完成: {mixedFile}");

        string mode = CmbMeetingMode.SelectedIndex switch
        {
            0 => "speech",
            1 => "music",
            2 => "mixed",
            _ => "speech"
        };

        string language = CmbMeetingLanguage.SelectedIndex switch
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

        await EnsurePipeAsync();

        var cmd = new TranscribeFileCommand { path = mixedFile, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] 已发送综合模式转录命令（模式: {mode}，语言: {language}）：{mixedFile}");

        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private float EstimatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0) return 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int samples = bytesRecorded / 4;
            float max = 0f;
            for (int i = 0; i < samples; i++)
            {
                float v = BitConverter.ToSingle(buffer, i * 4);
                float a = Math.Abs(v);
                if (a > max) max = a;
            }
            return max; // already 0..1
        }
        if (format.BitsPerSample == 16)
        {
            int samples = bytesRecorded / 2;
            int maxAbs = 0;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(buffer, i * 2);
                int a = Math.Abs((int)s);
                if (a > maxAbs) maxAbs = a;
            }
            return maxAbs / 32768f;
        }
        // Fallback: any non-zero proportion
        for (int i = 0; i < bytesRecorded; i++)
        {
            if (buffer[i] != 0) return 0.1f;
        }
        return 0f;
    }
}
