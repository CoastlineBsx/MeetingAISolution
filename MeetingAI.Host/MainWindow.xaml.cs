using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
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
}

