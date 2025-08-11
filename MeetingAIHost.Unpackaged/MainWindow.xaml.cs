

using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;  // ★ 新增

namespace MeetingAIHost;

public sealed partial class MainWindow : Window
{
    private Process? _worker;
    private NamedPipeClientStream? _pipe;   // ★ 复用的管道连接
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

    private void StopWorkerOnExit()
    {
        try
        {
            _pipe?.Dispose();
            _pipe = null;
            if (_worker is { HasExited: false }) _worker.Kill();
        }
        catch { }
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
        if (_pipe is { IsConnected: true }) return;

        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // 连接 Worker
        await _pipe.ConnectAsync(10000); // 10s 超时

        // 连接成功后，发一次空 ping 确认 Worker 就绪
        var testCmd = new { type = "ping", payload = "init-check" };
        var testJson = JsonSerializer.Serialize(testCmd) + "\n";
        var testBuf = Encoding.UTF8.GetBytes(testJson);
        await _pipe.WriteAsync(testBuf, 0, testBuf.Length);
        await _pipe.FlushAsync();

        // 等 Worker 回一条确认，避免第一次消息丢失
        using var reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var ack = await reader.ReadLineAsync();
        OutputBox.Text += $"[Worker ACK] {ack}\n";
    }


    private async void BtnPing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            var cmd = new { type = "ping", payload = "hello from host" };
            var json = JsonSerializer.Serialize(cmd) + "\n";
            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, leaveOpen: true);

            var readTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(2000)); // 2 秒超时

            if (completed == readTask && readTask.Result != null)
            {
                OutputBox.Text += $"[Worker] {readTask.Result}\n";
            }
            else
            {
                OutputBox.Text += "[Host] 等待 Worker 响应超时\n";
            }
        }
        catch (Exception ex)
        {
            OutputBox.Text += $"[Host] 发送失败：{ex.Message}\n";
            _pipe?.Dispose();
            _pipe = null; // 下次再连
        }
    }


    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 如果管道还在，先发 quit 请求
            if (_pipe is { IsConnected: true })
            {
                var cmd = new { type = "quit" };
                var json = JsonSerializer.Serialize(cmd) + "\n";
                var buf = Encoding.UTF8.GetBytes(json);
                await _pipe.WriteAsync(buf, 0, buf.Length);
                await _pipe.FlushAsync();
            }

            _pipe?.Dispose();
            _pipe = null;

            // 等待 Worker 自己退出，最多 2 秒
            if (_worker != null && !_worker.HasExited)
            {
                if (!_worker.WaitForExit(2000))
                {
                    _worker.Kill(); // 超时仍未退出，强制杀
                }
            }
        }
        catch { /* 忽略 */ }

        BtnPing.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnStart.IsEnabled = true;   // 允许再次启动
        LblStatus.Text = "已停止";
        OutputBox.Text += "[Host] Worker 已停止\n";
    }

}
