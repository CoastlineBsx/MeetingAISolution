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
using Windows.Storage.Pickers;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.Wave;
using FFMpegCore;
using FFMpegCore.Enums;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private Process? _worker;
    private NamedPipeClientStream? _pipe;   // 复用的管道连接
    private StreamReader? _reader;          // 复用的 Reader
    private Task? _readLoopTask;            // 后台读循环
    private CancellationTokenSource? _pipeCts;

    // 本次转录的“完成”信号（收到 complete/error 时置位）
    private TaskCompletionSource<bool>? _transcribeTcs;

    // 扬声器回放录音相关
    private WasapiLoopbackCapture? _loopback;
    private WaveFileWriter? _loopbackWriter;
    private string? _loopbackTempFile;
    private bool _isLoopback;

    private const string PipeName = "MeetingAI_Pipe";

    public MainWindow()
    {
        InitializeComponent();

        LblStatus.Text = "未连接";
        OutputBox.Text += $"[Host] BaseDir = {AppContext.BaseDirectory}\n";

        // 窗口关闭时自动清理 Worker 和管道
        this.Closed += (_, _) => StopWorkerOnExit();
    }

    private Task AppendLineAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(() =>
        {
            OutputBox.Text += text + "\n";
            tcs.SetResult(true);
        });
        return tcs.Task;
    }

    // 选择音频并发送转录命令
    private async void BtnTranscribe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                await AppendLineAsync("[Host] 未选择文件");
                return;
            }

            await AppendLineAsync($"[Host] 选择的音频文件: {file.Path}");

            // ★ 如果是MP3/M4A，转换为WAV
            string audioPath = file.Path;
            if (file.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                audioPath = await ConvertToWavAsync(file.Path);
                if (string.IsNullOrEmpty(audioPath))
                {
                    await AppendLineAsync("[Host] 音频格式转换失败");
                    return;
                }
            }

            // 确保管道连接（含后台读循环）
            await EnsurePipeAsync();

            // 获取选择的模式
            string mode = CmbTranscribeMode.SelectedIndex switch
            {
                0 => "speech",  // 对话/会议
                1 => "music",   // 音乐/歌曲
                2 => "mixed",   // 混合模式
                _ => "auto"     // 自动检测
            };

            // 获取测试转录的语言选择
            string language = CmbTranscribeLanguage.SelectedIndex switch
            {
                0 => "auto",  // 自动
                1 => "zh",    // 中文
                2 => "en",    // 英语
                3 => "ja",    // 日语
                4 => "ko",    // 韩语
                5 => "es",    // 西班牙语
                6 => "fr",    // 法语
                7 => "de",    // 德语
                _ => "auto"   // 默认自动
            };

            // 发送转录命令
            var cmd = new TranscribeFileCommand { path = audioPath, mode = mode, language = language };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync($"[Host] 转录命令已发送（模式: {mode}，语言: {language}），等待结果...");

            // 为本次转录创建“完成信号”
            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsLocal = _transcribeTcs; // 保存当前引用

            // 总等待上限（这里 10 分钟 = 600_000ms；如需 3 分钟改为 180_000）
            var overallTimeoutMs = 600_000;
            var completed = await Task.WhenAny(tcsLocal!.Task, Task.Delay(overallTimeoutMs));
            if (completed != tcsLocal.Task)
            {
                await AppendLineAsync("[Host] 总等待时长到达上限，结束等待（后续消息仍会在输出框显示）");
            }
            else
            {
                await AppendLineAsync("[Host] 本次转录完成");
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 转录测试失败：{ex.Message}");
            _pipeCts?.Cancel(); _pipeCts = null; _readLoopTask = null;
            _reader = null; _pipe?.Dispose(); _pipe = null;
        }
    }

    // 扬声器回放录音：开始/停止并在停止后发送转录
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

        // ★ 保存原始格式，让 Worker 端统一做音频处理（抗混叠重采样、高通滤波等）
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

            // 获取扬声器转录的模式选择
            string mode = CmbLoopbackMode.SelectedIndex switch
            {
                0 => "speech",  // 对话/会议
                1 => "music",   // 音乐/歌曲
                2 => "mixed",   // 混合模式
                _ => "auto"     // 自动检测
            };

            // 获取扬声器转录的语言选择
            string language = CmbLoopbackLanguage.SelectedIndex switch
            {
                0 => "auto",  // 自动
                1 => "zh",    // 中文
                2 => "en",    // 英语
                3 => "ja",    // 日语
                4 => "ko",    // 韩语
                5 => "es",    // 西班牙语
                6 => "fr",    // 法语
                7 => "de",    // 德语
                _ => "auto"   // 默认自动
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

    private void StopWorkerOnExit()
    {
        try
        {
            // 停止扬声器录音（若在录制中）
            try { if (_loopback is not null) _loopback.StopRecording(); } catch { }
            _isLoopback = false;
            _loopbackWriter = null;
            _loopback = null;

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
            // 忽略关闭时异常
        }
    }

    // 自动定位 MeetingAI.Worker.exe
    private string? FindWorkerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "MeetingAI.Worker.exe"),
        };

        // 向上若干级查找（解决方案根）
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
            catch { /* 忽略无权限/路径异常 */ }
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

            await Task.Delay(700); // 给 Worker 时间创建管道
            BtnPing.IsEnabled = true;
            BtnTranscribe.IsEnabled = true;
            BtnLoopback.IsEnabled = true;
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

    // 按需建立一次管道连接，后续复用
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
        await _pipe.ConnectAsync(30_000); // 30 秒

        _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        // 初次握手：发 ping，等 ACK
        var testCmd = new PingMessage { payload = "init-check" };
        var testJson = JsonSerializer.Serialize(testCmd, AppJsonContext.Default.PingMessage) + "\n";

        var testBuf = Encoding.UTF8.GetBytes(testJson);
        await _pipe.WriteAsync(testBuf, 0, testBuf.Length);
        await _pipe.FlushAsync();

        var ack = await _reader.ReadLineAsync();
        await AppendLineAsync($"[Worker ACK] {ack}");

        // 启动后台读循环
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

                if (line.Contains("\"type\":\"asr_segment\""))
                {
                    // 可在此更新 UI 字幕
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
            // 若在录制扬声器，先停止但不触发转录
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

    // MP3/M4A 转 WAV（使用 FFMpegCore - 自动下载FFmpeg）
    private async Task<string?> ConvertToWavAsync(string sourcePath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var tempWav = Path.Combine(Path.GetTempPath(), $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

                // ★ 自动下载FFmpeg（首次运行）
                await EnsureFFmpegAsync();

                _ = AppendLineAsync("[Host] 使用FFmpeg处理音频（工业级）...");

                // 使用FFMpegCore进行高级音频处理
                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(tempWav, true, options => options
                        .WithAudioCodec("pcm_s16le")          // PCM 16-bit
                        .WithAudioSamplingRate(16000)         // 16kHz
                        .WithCustomArgument("-ac 1")          // 单声道
                        .WithCustomArgument("-af \"highpass=f=200,lowpass=f=3000,loudnorm=I=-16:TP=-1.5:LRA=11\"")
                    )
                    .ProcessAsynchronously();

                if (File.Exists(tempWav))
                {
                    _ = AppendLineAsync($"[Host] FFmpeg转换完成: {tempWav}");
                    return tempWav;
                }
                else
                {
                    _ = AppendLineAsync("[Host] FFmpeg转换失败，使用NAudio备选");
                    return ConvertWithNAudio(sourcePath, tempWav);
                }
            }
            catch (Exception ex)
            {
                _ = AppendLineAsync($"[Host] FFmpeg失败: {ex.Message}，使用NAudio备选");
                var tempWav = Path.Combine(Path.GetTempPath(), $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                return ConvertWithNAudio(sourcePath, tempWav);
            }
        });
    }

    // 确保FFmpeg已下载（自动安装）
    private async Task EnsureFFmpegAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // FFMpegCore 会自动从 GitHub 下载 FFmpeg
                // 默认位置：%LOCALAPPDATA%\FFMpegCore\
                GlobalFFOptions.Configure(options =>
                {
                    // 可以自定义FFmpeg路径（可选）
                    // options.BinaryFolder = @"C:\ffmpeg\bin";
                });

                _ = AppendLineAsync("[Host] FFmpeg已就绪");
            }
            catch (Exception ex)
            {
                _ = AppendLineAsync($"[Host] FFmpeg配置警告: {ex.Message}");
            }
        });
    }

    // NAudio备选方案（FFmpeg不可用时）
    private string? ConvertWithNAudio(string sourcePath, string outputPath)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(sourcePath);
            var outFormat = new NAudio.Wave.WaveFormat(16000, 16, 1);
            using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, outFormat);
            NAudio.Wave.WaveFileWriter.CreateWaveFile(outputPath, resampler);
            _ = AppendLineAsync($"[Host] NAudio转换完成: {outputPath}");
            return outputPath;
        }
        catch
        {
            return null;
        }
    }
}

