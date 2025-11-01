using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnMeetingBeta3_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBeta3Running)
        {
            await StartBeta3Async();
        }
        else
        {
            await StopBeta3Async();
        }
    }

    private async Task StartBeta3Async()
    {
        await AppendLineAsync("[Host] [Beta3] 启动 AEC 双流实时字幕...");
        await EnsurePipeAsync();

        _beta3Stabilizer = new StreamStabilizer();
        _beta3Merger = new CaptionMerger(AudioCaptureQpc.GetQpcFrequency());

        _beta3Stabilizer.OnStableSegment += Beta3_OnStableSegment;
        _beta3Merger.OnNewCaption += Beta3_OnNewCaption;

        _beta3BaseQpcInitialized = false;

        try
        {
            _beta3Microphone = new AudioCaptureQpc(_selectedBeta3MicrophoneId, isLoopback: false, enableAec: true);
            await AppendLineAsync("[Host] [Beta3] ✓ 麦克风采集器已创建（AEC 已启用）");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] [Beta3] ❌ 麦克风初始化失败: {ex.Message}");
            return;
        }

        try
        {
            _beta3Speaker = new AudioCaptureQpc(_selectedBeta3SpeakerId, isLoopback: true, enableAec: false);
            await AppendLineAsync("[Host] [Beta3] ✓ 扬声器采集器已创建");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] [Beta3] ❌ 扬声器初始化失败: {ex.Message}");
            _beta3Microphone?.Dispose();
            return;
        }

        WaveFormat micFormat = _beta3Microphone.IsIeeeFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(_beta3Microphone.SampleRate, _beta3Microphone.Channels)
            : new WaveFormat(_beta3Microphone.SampleRate, _beta3Microphone.BitsPerSample, _beta3Microphone.Channels);

        _beta3MicResampler = new AudioResampler(micFormat);
        string micFormatStr = _beta3Microphone.IsIeeeFloat ? "IEEE Float" : "PCM";
        await AppendLineAsync($"[Host] [Beta3] 麦克风格式: {_beta3Microphone.SampleRate}Hz, {_beta3Microphone.Channels}ch, {_beta3Microphone.BitsPerSample}bit ({micFormatStr})");

        WaveFormat speakerFormat = _beta3Speaker.IsIeeeFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(_beta3Speaker.SampleRate, _beta3Speaker.Channels)
            : new WaveFormat(_beta3Speaker.SampleRate, _beta3Speaker.BitsPerSample, _beta3Speaker.Channels);

        _beta3SpeakerResampler = new AudioResampler(speakerFormat);
        string speakerFormatStr = _beta3Speaker.IsIeeeFloat ? "IEEE Float" : "PCM";
        await AppendLineAsync($"[Host] [Beta3] 扬声器格式: {_beta3Speaker.SampleRate}Hz, {_beta3Speaker.Channels}ch, {_beta3Speaker.BitsPerSample}bit ({speakerFormatStr})");

        string tempPath = Path.GetTempPath();
        string micDebugFile = Path.Combine(tempPath, $"beta3_mic_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        string speakerDebugFile = Path.Combine(tempPath, $"beta3_speaker_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _beta3DebugMicWriter = new WaveFileWriter(micDebugFile, micFormat);
        _beta3DebugSpeakerWriter = new WaveFileWriter(speakerDebugFile, speakerFormat);
        await AppendLineAsync($"[Host] [Beta3] 调试文件:\n  麦克风: {micDebugFile}\n  扬声器: {speakerDebugFile}");

        _beta3Microphone.DataAvailable += Beta3_OnMicrophoneData;
        _beta3Speaker.DataAvailable += Beta3_OnSpeakerData;

        _beta3Microphone.Start();
        _beta3Speaker.Start();

        string mode = CmbMeetingBeta3Mode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string language = CmbMeetingBeta3Language.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };

        string micStreamCmd = $"{{\"type\":\"start_stream2\",\"stream_id\":\"beta3_near\",\"source\":\"near\",\"mode\":\"{mode}\",\"language\":\"{language}\"}}\n";
        await _pipe!.WriteAsync(Encoding.UTF8.GetBytes(micStreamCmd));
        await _pipe.FlushAsync();

        string speakerStreamCmd = $"{{\"type\":\"start_stream2\",\"stream_id\":\"beta3_far\",\"source\":\"far\",\"mode\":\"{mode}\",\"language\":\"{language}\"}}\n";
        await _pipe.WriteAsync(Encoding.UTF8.GetBytes(speakerStreamCmd));
        await _pipe.FlushAsync();

        await AppendLineAsync($"[Host] [Beta3] 已发送双流启动命令（模式: {mode}，语言: {language}）");

        _beta3Cts = new CancellationTokenSource();
        _beta3MicSendTask = Task.Run(() => Beta3_SendLoop("beta3_near", _beta3MicResampler, _beta3Cts.Token));
        _beta3SpeakerSendTask = Task.Run(() => Beta3_SendLoop("beta3_far", _beta3SpeakerResampler, _beta3Cts.Token));

        _isBeta3Running = true;
        BtnMeetingBeta3.Content = "🛑 停止 Beta3";
        await AppendLineAsync("[Host] [Beta3] ✅ 双流实时字幕已启动");
    }

    private async Task StopBeta3Async()
    {
        await AppendLineAsync("[Host] [Beta3] 停止双流实时字幕...");

        _beta3Microphone?.Stop();
        _beta3Speaker?.Stop();

        _beta3Cts?.Cancel();
        if (_beta3MicSendTask != null) await _beta3MicSendTask;
        if (_beta3SpeakerSendTask != null) await _beta3SpeakerSendTask;

        if (_pipe != null && _pipe.IsConnected)
        {
            string stopMicCmd = "{\"type\":\"stop_stream2\",\"stream_id\":\"beta3_near\"}\n";
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(stopMicCmd));
            await _pipe.FlushAsync();

            string stopSpeakerCmd = "{\"type\":\"stop_stream2\",\"stream_id\":\"beta3_far\"}\n";
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(stopSpeakerCmd));
            await _pipe.FlushAsync();
        }

        _beta3Microphone?.Dispose();
        _beta3Speaker?.Dispose();
        _beta3MicResampler?.Dispose();
        _beta3SpeakerResampler?.Dispose();

        _beta3DebugMicWriter?.Dispose();
        _beta3DebugSpeakerWriter?.Dispose();
        _beta3DebugMicWriter = null;
        _beta3DebugSpeakerWriter = null;

        _beta3Stabilizer?.FlushStream("beta3_near");
        _beta3Stabilizer?.FlushStream("beta3_far");

        _beta3Microphone = null;
        _beta3Speaker = null;
        _beta3MicResampler = null;
        _beta3SpeakerResampler = null;
        _beta3Stabilizer = null;
        _beta3Merger = null;

        _beta3MicDataCount = 0;
        _beta3SpeakerDataCount = 0;

        _isBeta3Running = false;
        BtnMeetingBeta3.Content = "🎯 综合转录Beta3 (AEC)";
        await AppendLineAsync("[Host] [Beta3] ✅ 已停止");
    }

    private void Beta3_OnMicrophoneData(object? sender, AudioCaptureQpc.AudioDataEventArgs e)
    {
        if (!_beta3BaseQpcInitialized)
        {
            _beta3BaseQpc = e.QpcTimestamp;
            _beta3BaseQpcInitialized = true;
        }

        _beta3MicDataCount++;
        if (_beta3MicDataCount % 100 == 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OutputBox.Text += $"[Beta3] 麦克风收到数据: {_beta3MicDataCount} 帧, {e.BytesRecorded} 字节\n";
            });
        }

        _beta3DebugMicWriter?.Write(e.Data, 0, e.BytesRecorded);
        _beta3MicResampler?.AddSamples(e.Data, 0, e.BytesRecorded);
    }

    private void Beta3_OnSpeakerData(object? sender, AudioCaptureQpc.AudioDataEventArgs e)
    {
        if (!_beta3BaseQpcInitialized)
        {
            _beta3BaseQpc = e.QpcTimestamp;
            _beta3BaseQpcInitialized = true;
        }

        _beta3SpeakerDataCount++;
        if (_beta3SpeakerDataCount % 100 == 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OutputBox.Text += $"[Beta3] 扬声器收到数据: {_beta3SpeakerDataCount} 帧, {e.BytesRecorded} 字节\n";
            });
        }

        _beta3DebugSpeakerWriter?.Write(e.Data, 0, e.BytesRecorded);
        _beta3SpeakerResampler?.AddSamples(e.Data, 0, e.BytesRecorded);
    }

    private async Task Beta3_SendLoop(string streamId, AudioResampler resampler, CancellationToken token)
    {
        await Task.Delay(500, token);

        int sendCount = 0;
        await AppendLineAsync($"[Host] [Beta3] 发送循环启动: {streamId}");

        while (!token.IsCancellationRequested)
        {
            try
            {
                byte[]? frame = resampler.ReadFrame();
                if (frame == null)
                {
                    await Task.Delay(10, token);
                    continue;
                }

                sendCount++;
                if (sendCount % 50 == 0)
                {
                    await AppendLineAsync($"[Host] [Beta3] {streamId} 已发送 {sendCount} 帧");
                }

                long qpcNow;
                if (!AudioCaptureQpc.GetQpcTimestamp(out qpcNow))
                {
                    await Task.Delay(10, token);
                    continue;
                }

                long relativeQpc = qpcNow - _beta3BaseQpc;
                long timestampMs = (long)AudioCaptureQpc.QpcTicksToMilliseconds(relativeQpc);

                string base64Data = Convert.ToBase64String(frame);
                string cmd = $"{{\"type\":\"stream_chunk2\",\"stream_id\":\"{streamId}\",\"data\":\"{base64Data}\",\"sample_rate\":16000,\"timestamp_ms\":{timestampMs}}}\n";
                byte[] cmdBytes = Encoding.UTF8.GetBytes(cmd);

                if (_pipe != null && _pipe.IsConnected)
                {
                    await _pipe.WriteAsync(cmdBytes, 0, cmdBytes.Length, token);
                    await _pipe.FlushAsync(token);
                }
                else
                {
                    await AppendLineAsync($"[Host] [Beta3] 管道未连接！");
                    break;
                }

                await Task.Delay(20, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Host] [Beta3] 发送错误 ({streamId}): {ex.Message}");
                await Task.Delay(100, token);
            }
        }

        await AppendLineAsync($"[Host] [Beta3] 发送循环结束: {streamId}, 共发送 {sendCount} 帧");
    }

    private void Beta3_OnStableSegment(object? sender, StreamStabilizer.SegmentEventArgs e)
    {
        _beta3Merger?.AddCaption(e.Source, e.Text, e.QpcStart, e.QpcEnd);
    }

    private void Beta3_OnNewCaption(object? sender, CaptionMerger.MergedCaptionEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            OutputBox.Text += e.FormattedText + "\n";
        });
    }
}

