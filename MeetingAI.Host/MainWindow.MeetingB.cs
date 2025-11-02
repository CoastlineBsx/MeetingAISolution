using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // 只保留逻辑；不声明任何字段/常量。复用你项目里已存在的字段：
    // _isMeeting, _meetingMicrophone, _meetingLoopback, _meetingMicrophoneWriter,
    // _meetingLoopbackWriter, _meetingMicrophoneTempFile, _meetingLoopbackTempFile,
    // _selectedMeetingMicrophoneId, _selectedMeetingSpeakerId,
    // _meetingSyncTimer, _meetingSyncLock, _meetingStartTime,
    // _meetingLoopbackLastActiveTime, _meetingLoopbackTotalBytes,
    // _pipe, _transcribeTcs, BtnMeeting, CmbMeetingMode, CmbMeetingLanguage,
    // EnsurePipeAsync(), MixAudioFilesAsync()

    private async void BtnMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMeeting)
            await StartMeetingAsync();
        else
            await StopMeetingAndTranscribeAsync();
    }

    private async Task StartMeetingAsync()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingMicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_mic_{timestamp}.wav");
        _meetingLoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_speaker_{timestamp}.wav");

        // 扬声器回采设备
        if (_selectedMeetingSpeakerId == null || _selectedMeetingSpeakerId == "default")
            _meetingLoopback = new WasapiLoopbackCapture();
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingSpeakerId);
            _meetingLoopback = new WasapiLoopbackCapture(device);
        }

        // 麦克风设备
        if (_selectedMeetingMicrophoneId == null || _selectedMeetingMicrophoneId == "default")
            _meetingMicrophone = new WasapiCapture();
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingMicrophoneId);
            _meetingMicrophone = new WasapiCapture(device);
        }

        _meetingLoopbackWriter = new WaveFileWriter(_meetingLoopbackTempFile!, _meetingLoopback!.WaveFormat);
        _meetingMicrophoneWriter = new WaveFileWriter(_meetingMicrophoneTempFile!, _meetingMicrophone!.WaveFormat);

        // 初始化时间线
        _meetingStartTime = DateTime.Now;
        _meetingLoopbackLastActiveTime = _meetingStartTime;
        _meetingLoopbackTotalBytes = 0;

        // 扬声器：写入真实数据 + 仅用 0.001f 幅度阈值做“有声”标记
        _meetingLoopback.DataAvailable += (_, args) =>
        {
            lock (_meetingSyncLock)
            {
                _meetingLoopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded);
                _meetingLoopbackTotalBytes += args.BytesRecorded;

                if (HasSound(args.Buffer, args.BytesRecorded, _meetingLoopback.WaveFormat, 0.001f))
                    _meetingLoopbackLastActiveTime = DateTime.Now;
            }
        };
        _meetingLoopback.RecordingStopped += (_, __) =>
        {
            try { _meetingLoopbackWriter?.Dispose(); } catch { }
            _meetingLoopbackWriter = null;
        };

        // 麦克风：直接写入
        _meetingMicrophone.DataAvailable += (_, args) =>
        {
            _meetingMicrophoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };
        _meetingMicrophone.RecordingStopped += (_, __) =>
        {
            try { _meetingMicrophoneWriter?.Dispose(); } catch { }
            _meetingMicrophoneWriter = null;
        };

        // 启动采集
        _meetingLoopback.StartRecording();
        _meetingMicrophone.StartRecording();

        // 定时器：仅基于“是否有声”决定是否写入静音
        _meetingSyncTimer = new System.Timers.Timer(20); // 20ms
        _meetingSyncTimer.Elapsed += MeetingSyncTimer_Elapsed;
        _meetingSyncTimer.Start();

        _isMeeting = true;
        BtnMeeting.Content = "🛑 停止综合录音";
    }

    private void MeetingSyncTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isMeeting || _meetingLoopbackWriter == null || _meetingLoopback == null)
            return;

        lock (_meetingSyncLock)
        {
            var now = DateTime.Now;

            // 最近 300ms 内检测到“有声” → 不补静音
            if ((now - _meetingLoopbackLastActiveTime).TotalMilliseconds <= 300)
                return;

            // 无声状态下：按 20ms 固定步长写入静音块到扬声器文件
            var fmt = _meetingLoopback.WaveFormat;
            int bytesPerSec = fmt.SampleRate * fmt.BlockAlign;
            int bytesPerTick = (int)Math.Round(bytesPerSec * (20 / 1000.0));
            if (bytesPerTick <= 0) return;

            var zeros = new byte[bytesPerTick];
            _meetingLoopbackWriter.Write(zeros, 0, zeros.Length);
            _meetingLoopbackTotalBytes += bytesPerTick;
        }
    }

    private async Task StopMeetingAndTranscribeAsync()
    {
        if (_meetingSyncTimer != null)
        {
            _meetingSyncTimer.Stop();
            _meetingSyncTimer.Dispose();
            _meetingSyncTimer = null;
        }

        _meetingLoopback?.StopRecording();
        _meetingMicrophone?.StopRecording();

        _isMeeting = false;
        BtnMeeting.Content = "📞 综合转录";

        await Task.Delay(200);

        if (string.IsNullOrEmpty(_meetingMicrophoneTempFile) || !File.Exists(_meetingMicrophoneTempFile)) return;
        if (string.IsNullOrEmpty(_meetingLoopbackTempFile) || !File.Exists(_meetingLoopbackTempFile)) return;

        var mixedFile = await MixAudioFilesAsync(_meetingMicrophoneTempFile!, _meetingLoopbackTempFile!);
        if (string.IsNullOrEmpty(mixedFile) || !File.Exists(mixedFile))
            return;

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

        var cmd = new MeetingAI.Host.Contracts.Messages.TranscribeFileCommand
        {
            path = mixedFile!,
            mode = mode,
            language = language
        };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();

        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // 幅度阈值检测（仅此判据）
    private static bool HasSound(byte[] buffer, int bytesRecorded, WaveFormat format, float threshold)
    {
        if (bytesRecorded <= 0) return false;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int n = bytesRecorded / 4;
            for (int i = 0; i < n; i++)
            {
                float s = BitConverter.ToSingle(buffer, i * 4);
                if (Math.Abs(s) > threshold) return true;
            }
            return false;
        }
        else if (format.BitsPerSample == 16)
        {
            int n = bytesRecorded / 2;
            for (int i = 0; i < n; i++)
            {
                short v = BitConverter.ToInt16(buffer, i * 2);
                float s = v / 32768f;
                if (Math.Abs(s) > threshold) return true;
            }
            return false;
        }
        else
        {
            for (int i = 0; i < bytesRecorded; i++)
                if (buffer[i] != 0) return true;
            return false;
        }
    }
}
