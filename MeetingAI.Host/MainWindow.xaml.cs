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

        // 初始化 Granite 对话
        InitializeGranite();
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

    // ========== 主题切换 ==========
    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeRadioButtons.SelectedItem is RadioButton selectedButton)
        {
            var tag = selectedButton.Tag?.ToString();
            ElementTheme theme = tag switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            // 应用到整个窗口内容
            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme;
            }

            // 可选：保存设置
            // Windows.Storage.ApplicationData.Current.LocalSettings.Values["AppTheme"] = tag;
        }
    }

    // ========== 导航切换 ==========
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // 隐藏所有页面
        HomePage.Visibility = Visibility.Collapsed;
        ChatPage.Visibility = Visibility.Collapsed;
        QuickQAPage.Visibility = Visibility.Collapsed;
        IEPage.Visibility = Visibility.Collapsed;
        RAGPage.Visibility = Visibility.Collapsed;
        LLaVAPage.Visibility = Visibility.Collapsed;
        HelpPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        if (args.IsSettingsSelected)
        {
            // 显示设置页面
            SettingsPage.Visibility = Visibility.Visible;
        }
        else if (args.SelectedItemContainer != null)
        {
            var tag = args.SelectedItemContainer.Tag?.ToString();

            switch (tag)
            {
                case "home":
                    HomePage.Visibility = Visibility.Visible;
                    break;
                case "chat":
                    ChatPage.Visibility = Visibility.Visible;
                    // 绑定聊天历史到普通对话模式
                    ChatHistoryListChat.ItemsSource = _normalChatHistory;
                    // 设置为普通对话模式
                    _currentDialogMode = "normal";
                    _isRAGMode = false;
                    break;
                case "quickqa":
                    QuickQAPage.Visibility = Visibility.Visible;
                    break;
                case "ie":
                    IEPage.Visibility = Visibility.Visible;
                    break;
                case "rag":
                    RAGPage.Visibility = Visibility.Visible;
                    break;
                case "llava":
                    LLaVAPage.Visibility = Visibility.Visible;
                    break;
                case "help":
                    HelpPage.Visibility = Visibility.Visible;
                    break;
            }
        }
    }
}

