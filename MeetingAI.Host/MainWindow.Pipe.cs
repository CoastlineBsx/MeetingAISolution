using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private void StopWorkerOnExit()
    {
        try
        {
            try { if (_loopback is not null) _loopback.StopRecording(); } catch { }
            _isLoopback = false;
            _loopbackWriter = null;
            _loopback = null;

            try { if (_microphone is not null) _microphone.StopRecording(); } catch { }
            _isMicrophone = false;
            _microphoneWriter = null;
            _microphone = null;

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            if (_worker is { HasExited: false })
            {
                _worker.Kill();
            }
            _worker = null;

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch
        {
        }
    }

    private string? FindWorkerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "MeetingAI.Worker.exe"),
        };

        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(baseDir, "..", ".."))
        };
        foreach (var r in roots)
        {
            try
            {
                if (Directory.Exists(r))
                {
                    var hit = Directory.EnumerateFiles(r, "MeetingAI.Worker.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit)) candidates.Add(hit);
                }
            }
            catch { }
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            _ = AppendLineAsync("[Host] 未找到 MeetingAI.Worker.exe。\n候选路径（依次尝试）：\n"
                                + string.Join("\n", candidates.Distinct()));
        }
        else
        {
            _ = AppendLineAsync($"[Host] Worker 位置：{found}");
        }
        return found;
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_worker is { HasExited: false })
            {
                await AppendLineAsync("[Host] Worker 已在运行，忽略重复启动。");
                return;
            }

            var workerPath = FindWorkerExe();
            if (string.IsNullOrEmpty(workerPath))
            {
                await AppendLineAsync("[Host] 请确认已生成 Worker，并把 MeetingAI.Worker.exe 复制到 Host 的输出目录，或按提示的候选路径检查。");
                return;
            }

            _worker = Process.Start(new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"--ppid {Environment.ProcessId}"
            });

            await Task.Delay(700);
            BtnPing.IsEnabled = true;
            BtnTranscribe.IsEnabled = true;
            BtnLoopback.IsEnabled = true;
            BtnMicrophone.IsEnabled = true;
            BtnMeeting.IsEnabled = true;
            BtnMeetingBeta.IsEnabled = true;
            BtnMeetingBeta2.IsEnabled = true;
            BtnStop.IsEnabled = true;
            BtnStart.IsEnabled = false;
            LblStatus.Text = "Worker 已启动";
            await AppendLineAsync("[Host] Worker 启动完成");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 启动失败：{ex.Message}");
        }
    }

    private async Task EnsurePipeAsync()
    {
        if (_pipe is { IsConnected: true } && _reader != null && _readLoopTask != null)
            return;

        _pipeCts?.Cancel();
        _pipeCts = null;
        _readLoopTask = null;
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;

        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(30_000);

        _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        var testCmd = new PingMessage { payload = "init-check" };
        var testJson = JsonSerializer.Serialize(testCmd, AppJsonContext.Default.PingMessage) + "\n";

        var testBuf = Encoding.UTF8.GetBytes(testJson);
        await _pipe.WriteAsync(testBuf, 0, testBuf.Length);
        await _pipe.FlushAsync();

        var ack = await _reader.ReadLineAsync();
        await AppendLineAsync($"[Worker ACK] {ack}");

        _pipeCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => PipeReadLoopAsync(_pipeCts.Token));
    }

    private async Task PipeReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe is { IsConnected: true } && _reader != null)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null)
                {
                    await AppendLineAsync("[Host] Worker 连接已关闭");
                    break;
                }
                if (line.Length == 0) continue;

                await AppendLineAsync($"[Pipe] {line}");

                if (line.Contains("\"type\":\"stream_segment2\""))
                {
                    try
                    {
                        using var jd = JsonDocument.Parse(line);
                        var root = jd.RootElement;
                        string streamId = root.TryGetProperty("stream_id", out var sid) ? (sid.GetString() ?? "") : "";
                        string src = root.TryGetProperty("source", out var s) ? (s.GetString() ?? "") : "";
                        double t0 = root.TryGetProperty("t0_ms", out var t0e) ? t0e.GetDouble() / 1000.0 : 0.0;
                        double t1 = root.TryGetProperty("t1_ms", out var t1e) ? t1e.GetDouble() / 1000.0 : 0.0;
                        string text = root.TryGetProperty("text", out var te) ? (te.GetString() ?? "") : "";

                        await AppendLineAsync($"[Stream {src}] [{t0:F2}-{t1:F2}s] {text}");
                    }
                    catch { }
                    continue;
                }

                if (line.Contains("\"type\":\"asr_segment\""))
                {
                    continue;
                }
                if (line.Contains("\"type\":\"transcribe_complete\"") ||
                    line.Contains("\"type\":\"error\""))
                {
                    _transcribeTcs?.TrySetResult(true);
                    _transcribeTcs = null;
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 读循环异常：{ex.Message}");
        }
    }

    private async void BtnPing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            var cmd = new PingMessage { payload = "hello from host" };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.PingMessage) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync("[Host] 已发送测试 ping");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 发送失败：{ex.Message}");
            _pipeCts?.Cancel(); _pipeCts = null; _readLoopTask = null;
            _reader = null; _pipe?.Dispose(); _pipe = null;
        }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            try { if (_loopback is not null) _loopback.StopRecording(); } catch { }
            _isLoopback = false;
            BtnLoopback.Content = "扬声器转录";

            if (_pipe is { IsConnected: true })
            {
                var cmd = new QuitMessage();
                var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.QuitMessage) + "\n";

                var buf = Encoding.UTF8.GetBytes(json);
                await _pipe.WriteAsync(buf, 0, buf.Length);
                await _pipe.FlushAsync();
            }

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            if (_worker != null && !_worker.HasExited)
            {
                if (!_worker.WaitForExit(2000))
                {
                    _worker.Kill();
                }
            }
            _worker = null;

            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnLoopback.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止";
            await AppendLineAsync("[Host] Worker 已停止");

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 停止时异常：{ex.Message}");

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            try
            {
                if (_worker is { HasExited: false }) _worker.Kill();
            }
            catch { }
            _worker = null;

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;

            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnLoopback.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止(异常)";
        }
    }

    private async Task SendJsonAsync(string json)
    {
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
    }
}
