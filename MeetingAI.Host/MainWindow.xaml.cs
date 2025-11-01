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
using Windows.Storage.Pickers;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
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

    // 麦克风录音相关
    private WasapiCapture? _microphone;
    private WaveFileWriter? _microphoneWriter;
    private string? _microphoneTempFile;
    private bool _isMicrophone;
    private string? _selectedMicrophoneId;
    private string? _selectedLoopbackDeviceId;
    private string? _selectedMeetingSpeakerId;
    private string? _selectedMeetingBetaSpeakerId;
    private string? _selectedMeetingBeta2SpeakerId;
    private string? _selectedStreamingSpeakerId;

    // 综合转录（方案B：定时器同步）相关
    private WasapiCapture? _meetingMicrophone;
    private WasapiLoopbackCapture? _meetingLoopback;
    private WaveFileWriter? _meetingMicrophoneWriter;
    private WaveFileWriter? _meetingLoopbackWriter;
    private string? _meetingMicrophoneTempFile;
    private string? _meetingLoopbackTempFile;
    private bool _isMeeting;
    private string? _selectedMeetingMicrophoneId;
    private System.Timers.Timer? _meetingSyncTimer;
    private readonly object _meetingSyncLock = new object();
    private DateTime _meetingStartTime;
    private DateTime _meetingLoopbackLastDataTime;
    private long _meetingLoopbackTotalBytes;
    private int _meetingSyncFillCount;
    private bool _meetingLoopbackHasData; // 标志：扬声器是否收到过真实数据

    // 综合转录Beta（方案A：事后对齐）相关
    private WasapiCapture? _meetingBetaMicrophone;
    private WasapiLoopbackCapture? _meetingBetaLoopback;
    private WaveFileWriter? _meetingBetaMicrophoneWriter;
    private WaveFileWriter? _meetingBetaLoopbackWriter;
    private string? _meetingBetaMicrophoneTempFile;
    private string? _meetingBetaLoopbackTempFile;
    private bool _isMeetingBeta;
    private string? _selectedMeetingBetaMicrophoneId;

    //  23222f22a Beta2
    private WasapiCapture? _meetingBeta2Microphone;
    private WasapiLoopbackCapture? _meetingBeta2Loopback;
    private WaveFileWriter? _meetingBeta2MicrophoneWriter;
    private WaveFileWriter? _meetingBeta2LoopbackWriter;
    private string? _meetingBeta2MicrophoneTempFile;
    private string? _meetingBeta2LoopbackTempFile;
    private bool _isMeetingBeta2;
    private string? _selectedMeetingBeta2MicrophoneId;

    // Streaming (双流 near/far)
    private WasapiCapture? _streamMic;
    private WasapiLoopbackCapture? _streamLoop;
    private NAudio.Wave.BufferedWaveProvider? _streamMicBuffer;
    private NAudio.Wave.BufferedWaveProvider? _streamLoopBuffer;
    private System.Threading.CancellationTokenSource? _streamCts;
    private Task? _streamMicSendTask;
    private Task? _streamLoopSendTask;
    private string? _selectedStreamingMicId;
    private bool _isStreaming;

    // ==================== Beta3: AEC 双流实时字幕 ====================
    private AudioCaptureQpc? _beta3Microphone;
    private AudioCaptureQpc? _beta3Speaker;
    private AudioResampler? _beta3MicResampler;
    private AudioResampler? _beta3SpeakerResampler;
    private StreamStabilizer? _beta3Stabilizer;
    private CaptionMerger? _beta3Merger;
    private CancellationTokenSource? _beta3Cts;
    private Task? _beta3MicSendTask;
    private Task? _beta3SpeakerSendTask;
    private long _beta3BaseQpc; // 统一时钟基准
    private bool _beta3BaseQpcInitialized;
    private bool _isBeta3Running;
    private string? _selectedBeta3MicrophoneId;
    private string? _selectedBeta3SpeakerId;

    // Debug: 保存音频文件
    private WaveFileWriter? _beta3DebugMicWriter;
    private WaveFileWriter? _beta3DebugSpeakerWriter;

    private const string PipeName = "MeetingAI_Pipe";

    public MainWindow()
    {
        InitializeComponent();

        LblStatus.Text = "未连接";
        OutputBox.Text += $"[Host] BaseDir = {AppContext.BaseDirectory}\n";

        // 窗口关闭时自动清理 Worker 和管道
        this.Closed += (_, _) => StopWorkerOnExit();

        // 枚举麦克风设备
        EnumerateMicrophoneDevices();
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

            // 停止麦克风录音（若在录制中）
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
            BtnMicrophone.IsEnabled = true;
            BtnMeeting.IsEnabled = true;
            BtnMeetingBeta.IsEnabled = true;
            BtnStreaming.IsEnabled = true;
            BtnMeetingBeta2.IsEnabled = true;
            BtnMeetingBeta3.IsEnabled = true;
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

                // streaming near/far incremental segments
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

                        // Beta3 专用：送入稳定器
                        if (streamId.StartsWith("beta3_") && _beta3Stabilizer != null)
                        {
                            long qpcStart = (long)(t0 * AudioCaptureQpc.GetQpcFrequency()) + _beta3BaseQpc;
                            long qpcEnd = (long)(t1 * AudioCaptureQpc.GetQpcFrequency()) + _beta3BaseQpc;
                            _beta3Stabilizer.OnSegmentReceived(streamId, src, text, (long)(t0 * 1000), (long)(t1 * 1000), qpcStart, qpcEnd);
                        }
                        else
                        {
                            // 其他流式转录：直接显示
                            await AppendLineAsync($"[Stream {src}] [{t0:F2}-{t1:F2}s] {text}");
                        }
                    }
                    catch { }
                    continue;
                }

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

    // ==================== 麦克风录音功能 ====================

    // 枚举所有麦克风设备
    private void EnumerateMicrophoneDevices()
    {
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);

            // 清除除了默认项之外的所有项 - 麦克风转录
            while (CmbMicrophoneDevice.Items.Count > 1)
            {
                CmbMicrophoneDevice.Items.RemoveAt(1);
            }

            // 添加分隔符
            var separator = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMicrophoneDevice.Items.Add(separator);

            // 添加所有麦克风设备
            foreach (var device in devices)
            {
                var item = new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID };
                CmbMicrophoneDevice.Items.Add(item);
            }

            // 枚举扬声器（渲染）设备
            var enumerator2 = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var renders = enumerator2.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var rd in renders)
            {
                var it = new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID };
                CmbLoopbackDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingSpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingBetaSpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingBeta2SpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbStreamingSpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingBeta3SpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
            }

            // 清除除了默认项之外的所有项 - 综合转录（方案B）
            while (CmbMeetingDevice.Items.Count > 1)
            {
                CmbMeetingDevice.Items.RemoveAt(1);
            }

            // 添加分隔符
            var separator2 = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMeetingDevice.Items.Add(separator2);

            // 添加所有麦克风设备
            foreach (var device in devices)
            {
                var item = new ComboBoxItem
                {
                    Content = device.FriendlyName,
                    Tag = device.ID
                };
                CmbMeetingDevice.Items.Add(item);
            }

            // 清除除了默认项之外的所有项 - 综合转录Beta（方案A）
            while (CmbMeetingBetaDevice.Items.Count > 1)
            {
                CmbMeetingBetaDevice.Items.RemoveAt(1);
            }

            // 添加分隔符
            var separator3 = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMeetingBetaDevice.Items.Add(separator3);

            // 添加所有麦克风设备
            foreach (var device in devices)
            {
                var item = new ComboBoxItem
                {
                    Content = device.FriendlyName,
                    Tag = device.ID
                };
                CmbMeetingBetaDevice.Items.Add(item);
                CmbStreamingDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
                CmbMeetingBeta2Device.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
                CmbMeetingBeta3Device.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
            }

            _ = AppendLineAsync($"[Host] 已枚举 {devices.Count} 个麦克风设备");
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync($"[Host] 枚举麦克风设备失败: {ex.Message}");
        }
    }

    // 麦克风设备选择变化
    private void CmbMicrophoneDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMicrophoneDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId) _selectedMicrophoneId = deviceId; else _selectedMicrophoneId = null;
    }
    private void CmbLoopbackDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLoopbackDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedLoopbackDeviceId = id; else _selectedLoopbackDeviceId = null;
    }
    private void CmbMeetingSpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingSpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingSpeakerId = id; else _selectedMeetingSpeakerId = null;
    }
    private void CmbMeetingBetaSpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBetaSpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingBetaSpeakerId = id; else _selectedMeetingBetaSpeakerId = null;
    }
    private void CmbMeetingBeta2SpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta2SpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingBeta2SpeakerId = id; else _selectedMeetingBeta2SpeakerId = null;
    }
    private void CmbStreamingSpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbStreamingSpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedStreamingSpeakerId = id; else _selectedStreamingSpeakerId = null;
    }

    // 麦克风录音：开始/停止并在停止后发送转录
    private async void BtnMicrophone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMicrophone)
            {
                await StartMicrophoneAsync();
            }
            else
            {
                await StopMicrophoneAndTranscribeAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 麦克风录音异常：{ex.Message}");
        }
    }

    private async Task StartMicrophoneAsync()
    {
        _microphoneTempFile = Path.Combine(Path.GetTempPath(), $"microphone_{DateTime.Now:yyyyMMdd_HHmmss}_raw.wav");

        // 根据选择的设备 ID 创建 WasapiCapture
        if (_selectedMicrophoneId == null || _selectedMicrophoneId == "default")
        {
            // 使用默认麦克风
            _microphone = new WasapiCapture();
            await AppendLineAsync("[Host] 使用默认麦克风");
        }
        else
        {
            // 使用指定的麦克风设备
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMicrophoneId);
            _microphone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] 使用麦克风: {device.FriendlyName}");
        }

        // ★ 保存原始格式
        _microphoneWriter = new WaveFileWriter(_microphoneTempFile, _microphone.WaveFormat);

        await AppendLineAsync($"[Host] 录制格式: {_microphone.WaveFormat.SampleRate}Hz, " +
            $"{_microphone.WaveFormat.BitsPerSample}bit, {_microphone.WaveFormat.Channels}声道, " +
            $"{_microphone.WaveFormat.Encoding}");

        _microphone.DataAvailable += (_, args) =>
        {
            _microphoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };

        _microphone.RecordingStopped += async (_, __) =>
        {
            try { _microphoneWriter?.Dispose(); } catch { }
            _microphoneWriter = null;
            try { _microphone?.Dispose(); } catch { }
            _microphone = null;
            await AppendLineAsync($"[Host] 麦克风录音已停止，原始文件：{_microphoneTempFile}");
        };

        _microphone.StartRecording();
        _isMicrophone = true;
        BtnMicrophone.Content = "🛑 停止麦克风转录";
        await AppendLineAsync("[Host] 开始录制麦克风音频...");
    }

    private async Task StopMicrophoneAndTranscribeAsync()
    {
        if (_microphone != null)
        {
            _microphone.StopRecording();
        }
        _isMicrophone = false;
        BtnMicrophone.Content = "🎤 麦克风转录";

        var rawPath = _microphoneTempFile;
        if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath))
        {
            await AppendLineAsync("[Host] 未找到录制的文件，取消转录。");
            return;
        }

        // 等待文件完全写入
        await Task.Delay(500);

        // 获取麦克风转录的模式选择
        string mode = CmbMicrophoneMode.SelectedIndex switch
        {
            0 => "speech",  // 对话/会议
            1 => "music",   // 音乐/歌曲
            2 => "mixed",   // 混合模式
            _ => "speech"   // 默认对话
        };

        // 获取麦克风转录的语言选择
        string language = CmbMicrophoneLanguage.SelectedIndex switch
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

        // ★★★ 判断是否需要降噪
        bool needDenoise = false; // denoise removed

        // 对于 Speech/Mixed 模式，默认启用降噪（除非用户手动取消）
        // 对于 Music 模式，默认不启用（除非用户手动勾选）
        // denoise removed

        string processedPath = rawPath;

        // ★★★ FFmpeg 降噪处理
        if (needDenoise)
        {
            processedPath = Path.Combine(Path.GetTempPath(), $"microphone_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            await AppendLineAsync($"[Host] 正在应用 FFmpeg 降噪处理...");

            try
            {
                await FFMpegArguments
                    .FromFileInput(rawPath)
                    .OutputToFile(processedPath, true, options => options
                        .WithAudioCodec("pcm_s16le")
                        .WithAudioSamplingRate(16000)
                        .WithCustomArgument("-ac 1")
                        // ★★★ 麦克风降噪滤镜链（会议/对话优化）
                        .WithCustomArgument("-af \"" +
                            "highpass=f=80," +                    // 1. 去除低频噪声（风扇、空调）
                            "lowpass=f=8000," +                   // 2. 去除高频噪声
                            "afftdn=nr=20:nf=-40:tn=1," +         // 3. FFT 降噪（去除平稳噪声）
                            "anlmdn=s=0.00001:p=0.002:r=0.002," + // 4. 非线性降噪（去除突发噪声）
                            "equalizer=f=2000:t=q:w=1:g=3," +     // 5. 提升人声频段
                            "compand=attacks=0.1:decays=0.3:points=-60/-60|-30/-20|-20/-10|0/-5," +  // 6. 动态压缩
                            "loudnorm=I=-16:TP=-1.5:LRA=11" +     // 7. 响度标准化
                        "\"")
                    )
                    .ProcessAsynchronously();

                await AppendLineAsync($"[Host] 降噪处理完成：{processedPath}");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Host] FFmpeg 降噪失败，使用原始文件: {ex.Message}");
                processedPath = rawPath; // 降噪失败，使用原始文件
            }
        }
        else
        {
            await AppendLineAsync($"[Host] 跳过降噪，使用原始录音");
        }

        // 发送转录命令
        await EnsurePipeAsync();

        var cmd = new TranscribeFileCommand { path = processedPath, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] 已发送麦克风录音转录命令（模式: {mode}，语言: {language}，降噪: {needDenoise}）：{processedPath}");

        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ==================== 综合转录（方案B：定时器同步）====================

    // 综合转录麦克风设备选择变化
    private void CmbMeetingDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            _selectedMeetingMicrophoneId = deviceId;
        }
        else
        {
            _selectedMeetingMicrophoneId = null;
        }
    }

    // 综合转录按钮点击
    private async void BtnMeeting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMeeting)
            {
                await StartMeetingAsync();
            }
            else
            {
                await StopMeetingAndTranscribeAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 综合模式录音异常：{ex.Message}");
        }
    }

    private async Task StartMeetingAsync()
    {
        // 生成临时文件路径
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingMicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_mic_{timestamp}.wav");
        _meetingLoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_speaker_{timestamp}.wav");

        // 启动扬声器录音
        if (_selectedMeetingSpeakerId == null || _selectedMeetingSpeakerId == "default")
        {
            _meetingLoopback = new WasapiLoopbackCapture();
            await AppendLineAsync("[Host] 综合模式使用默认扬声器");
        }
        else
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingSpeakerId);
            _meetingLoopback = new WasapiLoopbackCapture(device);
            await AppendLineAsync($"[Host] 综合模式使用扬声器: {device.FriendlyName}");
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
                    _meetingLoopbackHasData = true; // 标记已收到真实数据
                    _ = AppendLineAsync($"[Host] [DEBUG] ✓ 扬声器首次收到数据：{args.BytesRecorded} 字节");
                }
            }
        };

        _meetingLoopback.RecordingStopped += async (_, __) =>
        {
            try { _meetingLoopbackWriter?.Dispose(); } catch { }
            _meetingLoopbackWriter = null;
            await AppendLineAsync($"[Host] 扬声器录音已停止");
        };

        // 启动麦克风录音
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

        // 同时启动两路录音（工业级同步）
        _meetingStartTime = DateTime.Now;
        _meetingLoopbackLastDataTime = _meetingStartTime;
        _meetingLoopbackTotalBytes = 0;
        _meetingSyncFillCount = 0;
        _meetingLoopbackHasData = false; // 初始化：尚未收到数据

        var startTime = DateTime.Now;
        _meetingLoopback.StartRecording();
        _meetingMicrophone.StartRecording();
        var endTime = DateTime.Now;

        var delay = (endTime - startTime).TotalMilliseconds;
        if (delay > 10)
        {
            await AppendLineAsync($"[Host] 警告：两路音频启动延迟 {delay:F2}ms");
        }
        else
        {
            await AppendLineAsync($"[Host] ✓ 两路音频同步启动（延迟 {delay:F2}ms）");
        }

        // 方案B：启动定时器强制同步
        _meetingSyncTimer = new System.Timers.Timer(20); // 每20ms检查一次
        _meetingSyncTimer.Elapsed += MeetingSyncTimer_Elapsed;
        _meetingSyncTimer.Start();
        await AppendLineAsync("[Host] ✓ 定时器同步已启动（方案B）");

        _isMeeting = true;
        BtnMeeting.Content = "🛑 停止综合录音";
        await AppendLineAsync("[Host] 综合模式已启动（麦克风 + 扬声器同时录音）");
    }

    // 方案B：定时器回调，强制同步扬声器文件长度
    private void MeetingSyncTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isMeeting || _meetingLoopbackWriter == null || _meetingLoopback == null)
            return;

        lock (_meetingSyncLock)
        {
            try
            {
                // ⭐ 改进策略：平衡对齐和保留真实音频
                var now = DateTime.Now;

                // 如果扬声器从未收到过任何数据，不填充
                if (!_meetingLoopbackHasData)
                {
                    return;
                }

                var format = _meetingLoopback.WaveFormat;
                var timeSinceLastData = (now - _meetingLoopbackLastDataTime).TotalSeconds;

                // 计算从开始到现在经过的时间（秒）
                double elapsedSeconds = (now - _meetingStartTime).TotalSeconds;

                // 计算应该有多少字节数据
                long expectedBytes = (long)(elapsedSeconds * format.SampleRate * format.BlockAlign);

                // 计算需要填充的字节数
                long missingBytes = expectedBytes - _meetingLoopbackTotalBytes;

                // ⭐ 智能填充策略：
                // 1. 如果扬声器正在持续录音（最近0.5秒内有数据），只填充小的间隙
                // 2. 如果扬声器已经停止录音（超过0.5秒没数据），填充更大的间隙
                long threshold;
                if (timeSinceLastData < 0.5)
                {
                    // 正在录音：只填充超过200ms的间隙（保留真实音频）
                    threshold = format.SampleRate * format.BlockAlign / 5; // 200ms
                }
                else
                {
                    // 已停止录音：填充超过100ms的间隙（保持对齐）
                    threshold = format.SampleRate * format.BlockAlign / 10; // 100ms
                }

                if (missingBytes > threshold)
                {
                    // 创建静音缓冲区
                    byte[] silenceBuffer = new byte[missingBytes];
                    Array.Fill<byte>(silenceBuffer, 0);

                    // 写入静音
                    _meetingLoopbackWriter.Write(silenceBuffer, 0, silenceBuffer.Length);

                    // 更新总字节数和计数器
                    _meetingLoopbackTotalBytes += missingBytes;
                    _meetingSyncFillCount++;

                    // ⭐ 每次填充都输出日志，便于诊断
                    double fillDuration = (double)missingBytes / (format.SampleRate * format.BlockAlign);
                    double timeSinceLastDataNow = (DateTime.Now - _meetingLoopbackLastDataTime).TotalSeconds;
                    _ = AppendLineAsync($"[Host] [DEBUG] 定时器填充: {fillDuration:F2}秒静音（第{_meetingSyncFillCount}次，距上次数据{timeSinceLastDataNow:F2}秒）");
                }
            }
            catch
            {
                // 忽略定时器中的异常
            }
        }
    }

    private async Task StopMeetingAndTranscribeAsync()
    {
        // 方案B：停止定时器
        if (_meetingSyncTimer != null)
        {
            _meetingSyncTimer.Stop();
            _meetingSyncTimer.Dispose();
            _meetingSyncTimer = null;
            await AppendLineAsync($"[Host] [方案B] ✓ 定时器同步已停止（共填充 {_meetingSyncFillCount} 次）");
        }

        // 停止录音
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

        // 等待文件完全写入
        await Task.Delay(500);

        // 检查文件是否存在
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

        // [DEBUG] 检查文件大小
        var micFileInfo = new FileInfo(_meetingMicrophoneTempFile);
        var spkFileInfo = new FileInfo(_meetingLoopbackTempFile);
        await AppendLineAsync($"[Host] [DEBUG] 麦克风文件大小: {micFileInfo.Length / 1024.0:F2} KB");
        await AppendLineAsync($"[Host] [DEBUG] 扬声器文件大小: {spkFileInfo.Length / 1024.0:F2} KB");

        if (spkFileInfo.Length < 1024)
        {
            await AppendLineAsync($"[Host] [DEBUG] ⚠️ 警告：扬声器文件过小（< 1KB），可能没有录到声音！");
        }

        // 方案B：定时器已经确保两个文件等长，直接混音
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

        // 获取综合模式的模式选择
        string mode = CmbMeetingMode.SelectedIndex switch
        {
            0 => "speech",
            1 => "music",
            2 => "mixed",
            _ => "speech"
        };

        // 获取综合模式的语言选择
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

        // 发送转录命令
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

    // 工业级混音：直接混合两个等长的音频文件
    private async Task<string?> MixAudioFilesAsync(string micFile, string speakerFile)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var mixedFile = Path.Combine(Path.GetTempPath(), $"meeting_mixed_{timestamp}.wav");

                await AppendLineAsync("[Host] 使用 NAudio 进行音频混音（工业级流程）...");

                // 读取两个音频文件
                using var micReader = new AudioFileReader(micFile);
                using var speakerReader = new AudioFileReader(speakerFile);

                var micDuration = micReader.TotalTime;
                var speakerDuration = speakerReader.TotalTime;

                await AppendLineAsync($"[Host] 麦克风: {micReader.WaveFormat.SampleRate}Hz, {micReader.WaveFormat.Channels}声道, 时长 {micDuration.TotalSeconds:F2}秒");
                await AppendLineAsync($"[Host] 扬声器: {speakerReader.WaveFormat.SampleRate}Hz, {speakerReader.WaveFormat.Channels}声道, 时长 {speakerDuration.TotalSeconds:F2}秒");

                // 验证两个文件长度是否相同（允许0.1秒误差）
                double durationDiff = Math.Abs(micDuration.TotalSeconds - speakerDuration.TotalSeconds);
                if (durationDiff > 0.1)
                {
                    await AppendLineAsync($"[Host] 警告：两个文件时长差异 {durationDiff:F2}秒，可能导致对齐问题");
                }
                else
                {
                    await AppendLineAsync($"[Host] ✓ 两个文件时长一致（差异 {durationDiff:F3}秒）");
                }

                // 统一格式：48kHz, 单声道, float32（混音中间格式）
                var commonFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

                // 转换麦克风到统一格式
                var micResampler = new MediaFoundationResampler(micReader, commonFormat);
                var micProvider = micResampler.ToSampleProvider();

                // 转换扬声器到统一格式
                var speakerResampler = new MediaFoundationResampler(speakerReader, commonFormat);
                var speakerProvider = speakerResampler.ToSampleProvider();

                // 调整音量权重
                var micVolume = new VolumeSampleProvider(micProvider) { Volume = 0.7f };
                var speakerVolume = new VolumeSampleProvider(speakerProvider) { Volume = 0.5f };

                await AppendLineAsync("[Host] 混音权重: 麦克风 70%, 扬声器 50%");

                // [DEBUG] 检查扬声器音频是否有内容
                await AppendLineAsync("[Host] [DEBUG] 正在检查扬声器音频内容...");
                float[] spkTestSamples = new float[4800]; // 0.1秒
                int spkTestRead = speakerVolume.Read(spkTestSamples, 0, 4800);
                float spkTestMax = 0f;
                for (int i = 0; i < spkTestRead; i++)
                {
                    if (Math.Abs(spkTestSamples[i]) > spkTestMax)
                        spkTestMax = Math.Abs(spkTestSamples[i]);
                }
                await AppendLineAsync($"[Host] [DEBUG] 扬声器前0.1秒最大振幅: {spkTestMax:F4}");
                if (spkTestMax < 0.001f)
                {
                    await AppendLineAsync("[Host] [DEBUG] ⚠️ 警告：扬声器音频振幅过小，可能是静音或没录到声音！");
                }

                // 重置扬声器reader位置，重新创建provider
                speakerReader.Position = 0;
                speakerResampler.Dispose();
                var speakerResampler2 = new MediaFoundationResampler(speakerReader, commonFormat);
                var speakerProvider2 = speakerResampler2.ToSampleProvider();
                var speakerVolume2 = new VolumeSampleProvider(speakerProvider2) { Volume = 0.5f };

                // 直接混音（无需偏移，因为文件已等长）
                var mixer = new MixingSampleProvider(new[] { micVolume, speakerVolume2 });

                // 输出为 16kHz, 16-bit, 单声道（Whisper 标准格式）
                var outFormat = new WaveFormat(16000, 16, 1);
                using var finalResampler = new MediaFoundationResampler(mixer.ToWaveProvider(), outFormat);

                // 写入混音后的文件
                WaveFileWriter.CreateWaveFile16(mixedFile, finalResampler.ToSampleProvider());

                // 释放资源
                micResampler.Dispose();
                speakerResampler.Dispose();

                // 检查混音后的文件时长
                using var mixedReader = new AudioFileReader(mixedFile);
                var mixedDuration = mixedReader.TotalTime;
                await AppendLineAsync($"[Host] 混音完成: {outFormat.SampleRate}Hz, {outFormat.BitsPerSample}bit, {outFormat.Channels}声道, 时长 {mixedDuration.TotalSeconds:F2}秒");

                return mixedFile;
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Host] 混音失败: {ex.Message}");
                await AppendLineAsync($"[Host] 详细错误: {ex.StackTrace}");
                return null;
            }
        });
    }

    // ==================== 综合转录Beta（方案A：事后对齐）====================

    // 综合转录Beta麦克风设备选择变化
    private void CmbMeetingBetaDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBetaDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            _selectedMeetingBetaMicrophoneId = deviceId;
        }
        else
        {
            _selectedMeetingBetaMicrophoneId = null;
        }
    }

    // 综合转录Beta按钮点击
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

        // 启动扬声器录音
        if (_selectedMeetingBetaSpeakerId == null || _selectedMeetingBetaSpeakerId == "default")
        {
            _meetingBetaLoopback = new WasapiLoopbackCapture();
            await AppendLineAsync("[Host] [Beta] 使用默认扬声器");
        }
        else
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
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

        // 启动麦克风录音
        if (_selectedMeetingBetaMicrophoneId == null || _selectedMeetingBetaMicrophoneId == "default")
        {
            _meetingBetaMicrophone = new WasapiCapture();
            await AppendLineAsync("[Host] [Beta] 使用默认麦克风");
        }
        else
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
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

        // 同时启动两路录音
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
        // 停止录音
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

        // 检查文件
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

        // 方案A核心：事后对齐（在扬声器前面填充静音）
        await AppendLineAsync("[Host] [Beta] 检查文件长度并前置对齐...");

        TimeSpan micDuration, speakerDuration;
        using (var micReader = new AudioFileReader(_meetingBetaMicrophoneTempFile))
        {
            micDuration = micReader.TotalTime;
        }
        using (var speakerReader = new AudioFileReader(_meetingBetaLoopbackTempFile))
        {
            speakerDuration = speakerReader.TotalTime;
        }

        await Task.Delay(100);  // 确保文件释放

        await AppendLineAsync($"[Host] [Beta] 麦克风时长: {micDuration.TotalSeconds:F2}秒");
        await AppendLineAsync($"[Host] [Beta] 扬声器时长: {speakerDuration.TotalSeconds:F2}秒");

        double durationDiff = micDuration.TotalSeconds - speakerDuration.TotalSeconds;

        if (durationDiff > 0.1)
        {
            // 麦克风更长，说明扬声器跳过了前面的静音，在扬声器前面填充静音
            await AppendLineAsync($"[Host] [Beta] 扬声器较短 {durationDiff:F2}秒，在前面填充静音（假设扬声器声音在后）...");
            await PrependSilenceToWavFileAsync(_meetingBetaLoopbackTempFile, durationDiff);
            await AppendLineAsync($"[Host] [Beta] ✓ 已为扬声器前置填充 {durationDiff:F2}秒静音");
        }
        else if (durationDiff < -0.1)
        {
            await AppendLineAsync($"[Host] [Beta] 警告：扬声器比麦克风长，可能对齐有误");
        }
        else
        {
            await AppendLineAsync($"[Host] [Beta] ✓ 文件长度一致（差异 {Math.Abs(durationDiff):F3}秒）");
        }

        await AppendLineAsync("[Host] [Beta] 正在混音两路音频...");
        var mixedFile = await MixAudioFilesAsync(_meetingBetaMicrophoneTempFile, _meetingBetaLoopbackTempFile);

        if (string.IsNullOrEmpty(mixedFile) || !File.Exists(mixedFile))
        {
            await AppendLineAsync("[Host] [Beta] 混音失败，取消转录。");
            return;
        }

        await AppendLineAsync($"[Host] [Beta] 混音完成: {mixedFile}");

        // 获取模式和语言
        string mode = CmbMeetingBetaMode.SelectedIndex switch
        {
            0 => "speech",
            1 => "music",
            2 => "mixed",
            _ => "speech"
        };

        string language = CmbMeetingBetaLanguage.SelectedIndex switch
        {
            0 => "auto", 1 => "zh", 2 => "en", 3 => "ja",
            4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto"
        };

        // 发送转录命令
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

    // 为 WAV 文件前面插入静音（方案A专用）
    // ===== Beta2 UI handlers =====
    private void CmbMeetingBeta2Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta2Device.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
            _selectedMeetingBeta2MicrophoneId = deviceId;
        else
            _selectedMeetingBeta2MicrophoneId = null;
    }

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
            await AppendLineAsync($"[Host] [Beta2] 22f22f23022a23422f22f23022f22c22f230: {ex.Message}");
        }
    }

    private async Task StartMeetingBeta2Async()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _meetingBeta2MicrophoneTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta2_mic_{timestamp}.wav");
        _meetingBeta2LoopbackTempFile = Path.Combine(Path.GetTempPath(), $"meeting_beta2_speaker_{timestamp}.wav");

        // speaker loopback
        if (_selectedMeetingBeta2SpeakerId == null || _selectedMeetingBeta2SpeakerId == "default")
            _meetingBeta2Loopback = new WasapiLoopbackCapture();
        else
        {
            var en = new MMDeviceEnumerator(); var dev = en.GetDevice(_selectedMeetingBeta2SpeakerId); _meetingBeta2Loopback = new WasapiLoopbackCapture(dev);
        }
        _meetingBeta2LoopbackWriter = new WaveFileWriter(_meetingBeta2LoopbackTempFile, _meetingBeta2Loopback.WaveFormat);
        await AppendLineAsync($"[Host] [Beta2] 22a23222f22c22f22b 22f235236: {_meetingBeta2Loopback.WaveFormat.SampleRate}Hz, {_meetingBeta2Loopback.WaveFormat.BitsPerSample}bit, {_meetingBeta2Loopback.WaveFormat.Channels}");
        _meetingBeta2Loopback.DataAvailable += (_, args) => { _meetingBeta2LoopbackWriter?.Write(args.Buffer, 0, args.BytesRecorded); };
        _meetingBeta2Loopback.RecordingStopped += async (_, __) => { try { _meetingBeta2LoopbackWriter?.Dispose(); } catch { } _meetingBeta2LoopbackWriter = null; await AppendLineAsync("[Host] [Beta2] 22a23222f22c22f22b 22f22c22f23522f"); };

        // microphone
        if (_selectedMeetingBeta2MicrophoneId == null || _selectedMeetingBeta2MicrophoneId == "default")
        {
            _meetingBeta2Microphone = new WasapiCapture();
            await AppendLineAsync("[Host] [Beta2] 22a22c23122f22f22a22f23022a22c");
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMeetingBeta2MicrophoneId);
            _meetingBeta2Microphone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] [Beta2] 22a22c23122f22f: {device.FriendlyName}");
        }
        _meetingBeta2MicrophoneWriter = new WaveFileWriter(_meetingBeta2MicrophoneTempFile, _meetingBeta2Microphone.WaveFormat);
        await AppendLineAsync($"[Host] [Beta2] 22a22c23122f22f 22f235236: {_meetingBeta2Microphone.WaveFormat.SampleRate}Hz, {_meetingBeta2Microphone.WaveFormat.BitsPerSample}bit, {_meetingBeta2Microphone.WaveFormat.Channels}");
        _meetingBeta2Microphone.DataAvailable += (_, args) => { _meetingBeta2MicrophoneWriter?.Write(args.Buffer, 0, args.BytesRecorded); };
        _meetingBeta2Microphone.RecordingStopped += async (_, __) => { try { _meetingBeta2MicrophoneWriter?.Dispose(); } catch { } _meetingBeta2MicrophoneWriter = null; await AppendLineAsync("[Host] [Beta2] 22a22c23122f22f 22f22c22f23522f"); };

        _meetingBeta2Loopback.StartRecording();
        _meetingBeta2Microphone.StartRecording();
        _isMeetingBeta2 = true;
        BtnMeetingBeta2.Content = "6d1 23122f22c23122f22f22a23422f22fBeta2";
        await AppendLineAsync("[Host] [Beta2] 23222f22a23422f22f (23122f22f22f230A) 22f22b22f22a");
    }

    private async Task StopMeetingBeta2AndTranscribeAsync()
    {
        if (_meetingBeta2Loopback != null) _meetingBeta2Loopback.StopRecording();
        if (_meetingBeta2Microphone != null) _meetingBeta2Microphone.StopRecording();
        _isMeetingBeta2 = false;
        BtnMeetingBeta2.Content = "4de 23122f23622a23422f22f23022eBeta2";
        await Task.Delay(500);

        if (string.IsNullOrEmpty(_meetingBeta2MicrophoneTempFile) || !File.Exists(_meetingBeta2MicrophoneTempFile)) { await AppendLineAsync("[Host] [Beta2] 26c1f22f22f22a22e 22e22f22c "); return; }
        if (string.IsNullOrEmpty(_meetingBeta2LoopbackTempFile) || !File.Exists(_meetingBeta2LoopbackTempFile)) { await AppendLineAsync("[Host] [Beta2] 22a23222f22c22f22b 22e22f22c "); return; }

        await AppendLineAsync($"[Host] [Beta2] 26c1f: {_meetingBeta2MicrophoneTempFile}");
        await AppendLineAsync($"[Host] [Beta2] 22a23222f22c22f22b: {_meetingBeta2LoopbackTempFile}");

        // optional prepend silence like Beta
        TimeSpan micDur2, spkDur2;
        using (var mr = new AudioFileReader(_meetingBeta2MicrophoneTempFile)) { micDur2 = mr.TotalTime; }
        using (var sr = new AudioFileReader(_meetingBeta2LoopbackTempFile)) { spkDur2 = sr.TotalTime; }
        double diff = micDur2.TotalSeconds - spkDur2.TotalSeconds;
        if (diff > 0.1)
        {
            await AppendLineAsync($"[Host] [Beta2] 22a23222f22c22f22b 22f22c22f 227 {diff:F2}s, 22f22b23023022f ");
            await PrependSilenceToWavFileAsync(_meetingBeta2LoopbackTempFile, diff);
        }

        var mixed = await MixAudioFilesAsync(_meetingBeta2MicrophoneTempFile, _meetingBeta2LoopbackTempFile);
        if (string.IsNullOrEmpty(mixed) || !File.Exists(mixed)) { await AppendLineAsync("[Host] [Beta2] 22f22b22a22f22f "); return; }

        string mode = CmbMeetingBeta2Mode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string language = CmbMeetingBeta2Language.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };

        await EnsurePipeAsync();
        var cmd = new TranscribeFileCommand { path = mixed!, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] [Beta2] 23022f22f22f 22f22e23022f (mode={mode}, lang={language}): {mixed}");
        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ===== 流式 UI & 发送回路 =====
    private void CmbStreamingDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbStreamingDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedStreamingMicId = id; else _selectedStreamingMicId = null;
    }

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

        // ⭐ 等待缓冲区预填充（至少500ms数据）
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
            // 主动拉取直到凑满20ms，避免间歇空读
            int got = 0;
            while (got < frameSamples && !ct.IsCancellationRequested)
            {
                int n = resampled.Read(frame, got, frameSamples - got);
                if (n == 0) { await Task.Delay(5, ct).ConfigureAwait(false); continue; }
                got += n;
            }
            if (ct.IsCancellationRequested) break;
            int read = frameSamples;
            Buffer.BlockCopy(frame, 0, frameBytes, 0, frameBytes.Length);
            string b64 = Convert.ToBase64String(frameBytes); long tsMs = (long)(sw.ElapsedTicks * 1000.0 / freq);
            string json = $"{{\"type\":\"stream_chunk2\",\"stream_id\":\"{streamId}\",\"sample_rate\":16000,\"timestamp_ms\":{tsMs},\"data\":\"{b64}\"}}\n";
            await SendJsonAsync(json);
        }
    }

    private async Task SendJsonAsync(string json)
    {
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
    }

    private async Task PrependSilenceToWavFileAsync(string wavFilePath, double silenceDuration)
    {
        await Task.Run(() =>
        {
            if (silenceDuration < 0.001) return;

            var tempFile = Path.Combine(Path.GetTempPath(), $"temp_prepend_{Guid.NewGuid()}.wav");

            using (var reader = new WaveFileReader(wavFilePath))
            {
                var format = reader.WaveFormat;
                int silenceBytes = (int)(silenceDuration * format.SampleRate * format.BlockAlign);

                using (var writer = new WaveFileWriter(tempFile, format))
                {
                    // 先写入静音
                    byte[] silenceBuffer = new byte[silenceBytes];
                    Array.Fill<byte>(silenceBuffer, 0);
                    writer.Write(silenceBuffer, 0, silenceBuffer.Length);

                    // 再复制原始音频
                    byte[] buffer = new byte[format.SampleRate * format.BlockAlign];
                    int bytesRead;
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(100);

            File.Delete(wavFilePath);
            File.Move(tempFile, wavFilePath);
        });
    }

    // ==================== Beta3: AEC 双流实时字幕 ====================

    private void CmbMeetingBeta3Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta3Device.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _selectedBeta3MicrophoneId = tag == "default" ? null : tag;
        }
    }

    private void CmbMeetingBeta3SpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta3SpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _selectedBeta3SpeakerId = tag == "default" ? null : tag;
        }
    }

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

        // 确保管道已连接
        await EnsurePipeAsync();

        // 初始化稳定器和融合器
        _beta3Stabilizer = new StreamStabilizer();
        _beta3Merger = new CaptionMerger(AudioCaptureQpc.GetQpcFrequency());

        // 订阅事件
        _beta3Stabilizer.OnStableSegment += Beta3_OnStableSegment;
        _beta3Merger.OnNewCaption += Beta3_OnNewCaption;

        // 初始化 QPC 基准
        _beta3BaseQpcInitialized = false;

        // 创建麦克风采集器（启用 AEC）
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

        // 创建扬声器采集器（loopback）
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

        // 直接从采集器获取格式（不需要等待数据）
        // 根据 formatTag 正确创建 WaveFormat
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

        // Debug: 创建音频文件保存原始数据
        string tempPath = Path.GetTempPath();
        string micDebugFile = Path.Combine(tempPath, $"beta3_mic_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        string speakerDebugFile = Path.Combine(tempPath, $"beta3_speaker_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _beta3DebugMicWriter = new WaveFileWriter(micDebugFile, micFormat);
        _beta3DebugSpeakerWriter = new WaveFileWriter(speakerDebugFile, speakerFormat);
        await AppendLineAsync($"[Host] [Beta3] 调试文件:\n  麦克风: {micDebugFile}\n  扬声器: {speakerDebugFile}");

        // 订阅数据事件
        _beta3Microphone.DataAvailable += Beta3_OnMicrophoneData;
        _beta3Speaker.DataAvailable += Beta3_OnSpeakerData;

        // 启动采集
        _beta3Microphone.Start();
        _beta3Speaker.Start();

        // 发送 start_stream2 命令到 Worker
        string mode = CmbMeetingBeta3Mode.SelectedIndex switch { 0 => "speech", 1 => "music", 2 => "mixed", _ => "speech" };
        string language = CmbMeetingBeta3Language.SelectedIndex switch { 0 => "auto", 1 => "zh", 2 => "en", 3 => "ja", 4 => "ko", 5 => "es", 6 => "fr", 7 => "de", _ => "auto" };

        // 麦克风流
        string micStreamCmd = $"{{\"type\":\"start_stream2\",\"stream_id\":\"beta3_near\",\"source\":\"near\",\"mode\":\"{mode}\",\"language\":\"{language}\"}}\n";
        await _pipe!.WriteAsync(Encoding.UTF8.GetBytes(micStreamCmd));
        await _pipe.FlushAsync();

        // 扬声器流
        string speakerStreamCmd = $"{{\"type\":\"start_stream2\",\"stream_id\":\"beta3_far\",\"source\":\"far\",\"mode\":\"{mode}\",\"language\":\"{language}\"}}\n";
        await _pipe.WriteAsync(Encoding.UTF8.GetBytes(speakerStreamCmd));
        await _pipe.FlushAsync();

        await AppendLineAsync($"[Host] [Beta3] 已发送双流启动命令（模式: {mode}，语言: {language}）");

        // 启动发送任务
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

        // 停止采集
        _beta3Microphone?.Stop();
        _beta3Speaker?.Stop();

        // 停止发送任务
        _beta3Cts?.Cancel();
        if (_beta3MicSendTask != null) await _beta3MicSendTask;
        if (_beta3SpeakerSendTask != null) await _beta3SpeakerSendTask;

        // 发送 stop_stream2 命令
        if (_pipe != null && _pipe.IsConnected)
        {
            string stopMicCmd = "{\"type\":\"stop_stream2\",\"stream_id\":\"beta3_near\"}\n";
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(stopMicCmd));
            await _pipe.FlushAsync();

            string stopSpeakerCmd = "{\"type\":\"stop_stream2\",\"stream_id\":\"beta3_far\"}\n";
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(stopSpeakerCmd));
            await _pipe.FlushAsync();
        }

        // 清理资源
        _beta3Microphone?.Dispose();
        _beta3Speaker?.Dispose();
        _beta3MicResampler?.Dispose();
        _beta3SpeakerResampler?.Dispose();

        // Debug: 关闭音频文件
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

        // 重置计数器
        _beta3MicDataCount = 0;
        _beta3SpeakerDataCount = 0;

        _isBeta3Running = false;
        BtnMeetingBeta3.Content = "🎯 综合转录Beta3 (AEC)";
        await AppendLineAsync("[Host] [Beta3] ✅ 已停止");
    }

    private int _beta3MicDataCount = 0;
    private int _beta3SpeakerDataCount = 0;

    private void Beta3_OnMicrophoneData(object? sender, AudioCaptureQpc.AudioDataEventArgs e)
    {
        // 初始化 QPC 基准
        if (!_beta3BaseQpcInitialized)
        {
            _beta3BaseQpc = e.QpcTimestamp;
            _beta3BaseQpcInitialized = true;
        }

        // 调试日志（每100帧输出一次）
        _beta3MicDataCount++;
        if (_beta3MicDataCount % 100 == 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OutputBox.Text += $"[Beta3] 麦克风收到数据: {_beta3MicDataCount} 帧, {e.BytesRecorded} 字节\n";
            });
        }

        // Debug: 保存到文件
        _beta3DebugMicWriter?.Write(e.Data, 0, e.BytesRecorded);

        // 添加到重采样器
        _beta3MicResampler?.AddSamples(e.Data, 0, e.BytesRecorded);
    }

    private void Beta3_OnSpeakerData(object? sender, AudioCaptureQpc.AudioDataEventArgs e)
    {
        // 初始化 QPC 基准
        if (!_beta3BaseQpcInitialized)
        {
            _beta3BaseQpc = e.QpcTimestamp;
            _beta3BaseQpcInitialized = true;
        }

        // 调试日志（每100帧输出一次）
        _beta3SpeakerDataCount++;
        if (_beta3SpeakerDataCount % 100 == 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OutputBox.Text += $"[Beta3] 扬声器收到数据: {_beta3SpeakerDataCount} 帧, {e.BytesRecorded} 字节\n";
            });
        }

        // Debug: 保存到文件
        _beta3DebugSpeakerWriter?.Write(e.Data, 0, e.BytesRecorded);

        // 添加到重采样器
        _beta3SpeakerResampler?.AddSamples(e.Data, 0, e.BytesRecorded);
    }

    private async Task Beta3_SendLoop(string streamId, AudioResampler resampler, CancellationToken token)
    {
        await Task.Delay(500, token); // 预填充缓冲区

        int sendCount = 0;
        await AppendLineAsync($"[Host] [Beta3] 发送循环启动: {streamId}");

        while (!token.IsCancellationRequested)
        {
            try
            {
                // 读取 20ms 帧
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

                // 获取当前 QPC 时间戳
                long qpcNow;
                if (!AudioCaptureQpc.GetQpcTimestamp(out qpcNow))
                {
                    await Task.Delay(10, token);
                    continue;
                }

                long relativeQpc = qpcNow - _beta3BaseQpc;
                long timestampMs = (long)AudioCaptureQpc.QpcTicksToMilliseconds(relativeQpc);

                // Base64 编码
                string base64Data = Convert.ToBase64String(frame);

                // 发送 stream_chunk2 命令
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

                // 20ms 节奏
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
        // 将稳定的片段送入融合器
        _beta3Merger?.AddCaption(e.Source, e.Text, e.QpcStart, e.QpcEnd);
    }

    private void Beta3_OnNewCaption(object? sender, CaptionMerger.MergedCaptionEventArgs e)
    {
        // 增量更新 UI
        DispatcherQueue.TryEnqueue(() =>
        {
            OutputBox.Text += e.FormattedText + "\n";
        });
    }
}

