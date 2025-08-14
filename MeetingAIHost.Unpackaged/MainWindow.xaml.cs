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
using Windows.Storage.Pickers;  // 新增：文件选择器

namespace MeetingAIHost;

public sealed partial class MainWindow : Window
{
    private Process? _worker;
    private NamedPipeClientStream? _pipe;   // ★ 复用的管道连接
                                            // ★ 新增：复用的 Reader、后台读循环和控制
    private StreamReader? _reader;
    private Task? _readLoopTask;
    private CancellationTokenSource? _pipeCts;

    // ★ 新增：本次转录的“完成”信号（由后台读循环在收到 complete/error 时置位）
    private TaskCompletionSource<bool>? _transcribeTcs;

    private const string PipeName = "MeetingAI_Pipe";

    public MainWindow()
    {
        InitializeComponent();

        LblStatus.Text = "未连接";
        // 启动时打印 Host 实际运行目录，帮你确认拷贝位置
        OutputBox.Text += $"[Host] BaseDir = {AppContext.BaseDirectory}\n";
        // 在窗口关闭时自动停止 Worker
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

    // 新增：测试转录功能
    private async void BtnTranscribe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 文件选择部分保留你原来代码
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                OutputBox.Text += "[Host] 未选择文件\n";
                return;
            }

            OutputBox.Text += $"[Host] 选择的音频文件: {file.Path}\n";

            // 确保管道连接（含后台读循环启动）
            await EnsurePipeAsync();

            // 发送转录命令
            var cmd = new TranscribeFileCommand { path = file.Path };
            var json = System.Text.Json.JsonSerializer.Serialize(
                           cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync("[Host] 转录命令已发送，等待结果...");
            // 为本次转录创建“完成信号”
            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsLocal = _transcribeTcs; // ★关键：保存当前引用，避免后台把字段清空导致空引用

            // 总等待上限（后台循环保证“滑动超时”）
            var overallTimeoutMs = 600000; // 这里 600000ms 实际是 10 分钟；若要 3 分钟应改 180000
            var completed = await Task.WhenAny(tcsLocal.Task, Task.Delay(overallTimeoutMs));
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
            OutputBox.Text += $"[Host] 转录测试失败：{ex.Message}\n";
            _pipeCts?.Cancel(); _pipeCts = null; _readLoopTask = null;
            _reader = null; _pipe?.Dispose(); _pipe = null;
        }

    }

    private void StopWorkerOnExit()
    {
        try
        {
            // 1) 停掉后台读循环
            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            // 2) 释放管道读写资源
            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            // 3) 结束子进程
            if (_worker is { HasExited: false })
            {
                // 这里关闭窗口时不等优雅退出，直接 Kill，避免卡关闭
                _worker.Kill();
            }
            _worker = null;

            // 4) 清理“本次转录”的等待者
            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch
        {
            // 忽略关闭时的清理异常
        }
    }


    // ★ 新增：自动定位 WorkerNative.exe
    private string? FindWorkerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();

        // 1) 当前运行目录
        candidates.Add(Path.Combine(baseDir, "WorkerNative.exe"));

        // 2) 如果 Host 误从打包项目(MeetingAIHost)启动，映射到 Unpackaged 的 bin
        //    ...\MeetingAIHost\bin\... -> ...\MeetingAIHost.Unpackaged\bin\...
        var altFromPackaged = baseDir.Replace(
            Path.DirectorySeparatorChar + "MeetingAIHost" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "MeetingAIHost.Unpackaged" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );
        candidates.Add(Path.Combine(altFromPackaged, "WorkerNative.exe"));

        // 3) 往上几级做一次有限搜索（例如解决方案根）
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")), // 通常是解决方案根
            Path.GetFullPath(Path.Combine(baseDir, "..", ".."))               // 备选
        };
        foreach (var r in roots)
        {
            try
            {
                if (Directory.Exists(r))
                {
                    var hit = Directory.EnumerateFiles(r, "WorkerNative.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit)) candidates.Add(hit);
                }
            }
            catch { /* 忽略无权限/路径异常 */ }
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            OutputBox.Text += "[Host] 未找到 WorkerNative.exe。\n候选路径（依次尝试）：\n"
                              + string.Join("\n", candidates.Distinct()) + "\n";
        }
        else
        {
            OutputBox.Text += $"[Host] Worker 位置：{found}\n";
        }
        return found;
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 防重复启动
            if (_worker is { HasExited: false })
            {
                OutputBox.Text += "[Host] Worker 已在运行，忽略重复启动。\n";
                return;
            }

            // ★ 使用自动定位
            var workerPath = FindWorkerExe();
            if (string.IsNullOrEmpty(workerPath))
            {
                OutputBox.Text += "[Host] 请确认已生成 Worker，并把 WorkerNative.exe 复制到 Host 的输出目录，或按提示的候选路径检查。\n";
                return;
            }

            _worker = Process.Start(new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,   // 保持你原来的设置
                Arguments = $"--ppid {Environment.ProcessId}"
            });

            await Task.Delay(700); // 给 Worker 一点时间创建管道
            BtnPing.IsEnabled = true;
            BtnTranscribe.IsEnabled = true;  // 启用转录按钮
            BtnStop.IsEnabled = true;
            BtnStart.IsEnabled = false;   // 启动后禁用，避免误点多次
            LblStatus.Text = "Worker 已启动";
            OutputBox.Text += "[Host] Worker 启动完成\n";
        }
        catch (Exception ex)
        {
            OutputBox.Text += $"[Host] 启动失败：{ex.Message}\n";
        }
    }

    // 只在需要时建立一次管道连接，后续复用
    private async Task EnsurePipeAsync()
    {
        if (_pipe is { IsConnected: true } && _reader != null && _readLoopTask != null)
            return;

        // 清旧资源
        _pipeCts?.Cancel();
        _pipeCts = null;
        _readLoopTask = null;
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;

        // 连接管道
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(600000); // 放宽至 30s

        // 复用一个全局 reader
        _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        // 初次握手：发 ping，等 ACK（从 _reader 读）
        var testCmd = new PingMessage { payload = "init-check" };
        var testJson = System.Text.Json.JsonSerializer.Serialize(
                           testCmd, AppJsonContext.Default.PingMessage) + "\n";

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

                // 展示所有来自 Worker 的消息（保留你的测试/调试体验）
                await AppendLineAsync($"[Pipe] {line}");

                // 简单类型分发（无需引入 JSON 解析）
                if (line.Contains("\"type\":\"asr_segment\""))
                {
                    // 这里可做字幕逐行更新（现在先统一打印）
                    continue;
                }
                if (line.Contains("\"type\":\"transcribe_complete\"") ||
                    line.Contains("\"type\":\"error\""))
                {
                    // 通知“本次转录”已完成
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
            var json = System.Text.Json.JsonSerializer.Serialize(
                           cmd, AppJsonContext.Default.PingMessage) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync("[Host] 已发送测试 ping");
            // 回包会由后台读循环显示
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
            // 1) 如果管道还连着，先请求 Worker 优雅退出
            if (_pipe is { IsConnected: true })
            {
                var cmd = new QuitMessage();
                var json = System.Text.Json.JsonSerializer.Serialize(
                               cmd, AppJsonContext.Default.QuitMessage) + "\n";

                var buf = Encoding.UTF8.GetBytes(json);
                await _pipe.WriteAsync(buf, 0, buf.Length);
                await _pipe.FlushAsync();
            }

            // 2) 结束后台读循环 & 释放管道资源
            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            // 3) 等待 Worker 自行退出，给最多 2 秒；超时就强杀
            if (_worker != null && !_worker.HasExited)
            {
                if (!_worker.WaitForExit(2000))
                {
                    _worker.Kill();
                }
            }
            _worker = null;

            // 4) UI 状态复原
            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止";
            OutputBox.Text += "[Host] Worker 已停止\n";

            // 5) 把“本次转录”的等待者（如果有）也结束掉，避免悬挂
            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch (Exception ex)
        {
            OutputBox.Text += $"[Host] 停止时异常：{ex.Message}\n";

            // 出错时也要确保资源清理
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
            catch { /* ignore */ }
            _worker = null;

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;

            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止(异常)";
        }
    }

}
