using System;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnStreaming_Click(object sender, RoutedEventArgs e)
    {
        try { if (!_isStreaming) await StartStreamingAsync(); else await StopStreamingAsync(); }
        catch (Exception ex) { await AppendLineAsync($"[Host] [Stream] 异常: {ex.Message}"); }
    }

    private async Task StartStreamingAsync()
    {
        await EnsurePipeAsync();
        string mode = CmbStreamingMode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string lang = CmbStreamingLanguage.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };
        await SendJsonAsync($"{{\"type\":\"start_stream2\",\"stream_id\":\"near\",\"source\":\"near\",\"mode\":\"{mode}\",\"language\":\"{lang}\"}}\n");
        await SendJsonAsync($"{{\"type\":\"start_stream2\",\"stream_id\":\"far\",\"source\":\"far\",\"mode\":\"{mode}\",\"language\":\"{lang}\"}}\n");

        if (_selectedStreamingSpeakerId == null || _selectedStreamingSpeakerId == "default")
            _streamLoop = new WasapiLoopbackCapture();
        else
        {
            var en2 = new MMDeviceEnumerator(); var dev2 = en2.GetDevice(_selectedStreamingSpeakerId); _streamLoop = new WasapiLoopbackCapture(dev2);
        }
        _streamLoopBuffer = new BufferedWaveProvider(_streamLoop.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5), ReadFully = false };
        _streamLoop.DataAvailable += (_, a) => _streamLoopBuffer?.AddSamples(a.Buffer, 0, a.BytesRecorded);

        if (_selectedStreamingMicId == null || _selectedStreamingMicId == "default") { _streamMic = new WasapiCapture(); }
        else { var en = new MMDeviceEnumerator(); var dev = en.GetDevice(_selectedStreamingMicId); _streamMic = new WasapiCapture(dev); }
        _streamMicBuffer = new BufferedWaveProvider(_streamMic.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5), ReadFully = false };
        _streamMic.DataAvailable += (_, a) => _streamMicBuffer?.AddSamples(a.Buffer, 0, a.BytesRecorded);

        _streamCts = new CancellationTokenSource();
        _streamLoop.StartRecording();
        _streamMic.StartRecording();
        _isStreaming = true; BtnStreaming.Content = "⏹ 停止流式";
        await AppendLineAsync("[Host] [Stream] 已开始录音，等待缓冲区预填充...");

        await Task.Delay(500);
        await AppendLineAsync("[Host] [Stream] 缓冲区预填充完成（near/far 双流，20ms 帧）");

        _streamMicSendTask = Task.Run(() => StreamSenderLoopAsync(_streamMicBuffer!, "near", _streamMic!.WaveFormat, _streamCts!.Token));
        _streamLoopSendTask = Task.Run(() => StreamSenderLoopAsync(_streamLoopBuffer!, "far", _streamLoop!.WaveFormat, _streamCts!.Token));
    }

    private async Task StopStreamingAsync()
    {
        try { _streamMic?.StopRecording(); } catch { }
        try { _streamLoop?.StopRecording(); } catch { }
        _streamCts?.Cancel();
        _isStreaming = false; BtnStreaming.Content = "🚀 流式转录";
        await SendJsonAsync("{\"type\":\"stop_stream2\",\"stream_id\":\"near\"}\n");
        await SendJsonAsync("{\"type\":\"stop_stream2\",\"stream_id\":\"far\"}\n");
        await AppendLineAsync("[Host] [Stream] 已停止");
    }

    private async Task StreamSenderLoopAsync(BufferedWaveProvider src, string streamId, WaveFormat srcFormat, CancellationToken ct)
    {
        var sampleProv = src.ToSampleProvider();
        ISampleProvider mono = sampleProv;
        if (sampleProv.WaveFormat.Channels > 1) mono = new StereoToMonoSampleProvider(sampleProv);
        var resampled = new WdlResamplingSampleProvider(mono, 16000);

        const int frameSamples = 320; float[] frame = new float[frameSamples]; byte[] frameBytes = new byte[frameSamples * sizeof(float)];
        var sw = Stopwatch.StartNew(); long freq = Stopwatch.Frequency;
        while (!ct.IsCancellationRequested)
        {
            int got = 0;
            while (got < frameSamples && !ct.IsCancellationRequested)
            {
                int n = resampled.Read(frame, got, frameSamples - got);
                if (n == 0) { await Task.Delay(5, ct).ConfigureAwait(false); continue; }
                got += n;
            }
            if (ct.IsCancellationRequested) break;
            Buffer.BlockCopy(frame, 0, frameBytes, 0, frameBytes.Length);
            string b64 = Convert.ToBase64String(frameBytes); long tsMs = (long)(sw.ElapsedTicks * 1000.0 / freq);
            string json = $"{{\"type\":\"stream_chunk2\",\"stream_id\":\"{streamId}\",\"sample_rate\":16000,\"timestamp_ms\":{tsMs},\"data\":\"{b64}\"}}\n";
            await SendJsonAsync(json);
        }
    }
}
