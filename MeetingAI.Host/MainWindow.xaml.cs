using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 启用 Mica 材质背景（Win11 Settings 风格）
        this.SystemBackdrop = new MicaBackdrop()
        {
            Kind = MicaKind.BaseAlt // 使用 MicaAlt 效果（稍深）
        };

        // 启用自定义标题栏
        ExtendsContentIntoTitleBar = true;

        // 设置标题栏颜色（自适应主题）
        SetupTitleBar();

        LblStatus.Text = "未连接";
        OutputBox.Text += $"[Host] BaseDir = {AppContext.BaseDirectory}\n";

        // 窗口关闭时自动清理 Worker 和管道
        this.Closed += (_, _) => StopWorkerOnExit();

        // 枚举麦克风设备
        EnumerateMicrophoneDevices();

        // 预加载所有页面（避免懒加载带来的复杂性）
        // 必须在 InitializeGranite() 之前调用，以便绑定聊天历史
        LoadAllPages();

        // 初始化 Granite 对话
        InitializeGranite();

        // 初始化 SD 页面
        InitializeSDPage();

        // 初始化 IE Chat 页面
        InitializeIEChatPage();

        // 创建调试日志文件
        try
        {
            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAI");
            if (!System.IO.Directory.Exists(logDir))
                System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, "ragchat_debug.log");
            System.IO.File.WriteAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} APPLICATION STARTED\n");
        }
        catch { }
    }

    private void SetupTitleBar()
    {
        // 设置自定义标题栏的可拖拽区域
        SetTitleBar(CustomTitleBar);

        // 获取标题栏配置
        var titleBar = this.AppWindow.TitleBar;

        // 设置标题栏透明（配合 Mica 材质）
        titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ForegroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        // 设置按钮背景透明
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        // 保持按钮前景色和悬停色使用系统默认（自动适配主题）
        // 这样按钮会根据系统主题自动调整颜色
    }

    private void LoadAllPages()
    {
        // 预加载所有页面，避免懒加载带来的 null 检查复杂性
        StartupFrame.Navigate(typeof(Pages.StartupPage));
        ChatFrame.Navigate(typeof(Pages.ChatPage));
        QuickQAFrame.Navigate(typeof(Pages.QuickQAPage));
        IEChatFrame.Navigate(typeof(Pages.IEChatPage));
        LLaVAFrame.Navigate(typeof(Pages.LLaVAPage));
        SDFrame.Navigate(typeof(Pages.SDPage));
        SettingsFrame.Navigate(typeof(Pages.SettingsPage));
        HelpFrame.Navigate(typeof(Pages.HelpPage));
        OpenVINOWhisperFrame.Navigate(typeof(Pages.OpenVINOWhisperPage));
    }

    private Task AppendLineAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (OutputBox != null)
                {
                    OutputBox.Text += text + "\n";
                }
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                // Log to file if OutputBox access fails
                try
                {
                    var logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingAI", "ragchat_debug.log");
                    System.IO.File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [AppendLineAsync] EXCEPTION: {ex}\n");
                }
                catch { }
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ========== 搜索功能 ==========
    private readonly List<(string Name, string Tag, string[] Keywords)> _searchItems = new()
    {
        ("主页", "home", new[] { "主页", "home", "worker", "启动", "录音", "转录", "麦克风" }),
        ("智能对话", "chat", new[] { "智能对话", "对话", "chat", "ai", "聊天", "问答", "普通" }),
        ("文档助手", "quickqa", new[] { "文档助手", "快速问答", "quickqa", "文档", "问答", "上传" }),
        ("信息提取", "ie_chat", new[] { "信息提取", "ie_chat", "extraction", "提取", "解析", "模板", "json", "结构化" }),
        ("智能解析", "ie", new[] { "智能解析", "ie", "提取", "解析", "模板", "信息提取" }),
        ("知识库", "rag", new[] { "知识库", "rag", "检索", "文档管理", "向量" }),
        ("视觉理解", "llava", new[] { "视觉理解", "llava", "图片", "视觉", "图像", "看图" }),
        ("帮助", "help", new[] { "帮助", "help", "文档" }),
        ("设置", "settings", new[] { "设置", "settings", "主题", "外观" })
    };

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.ToLower();
            if (string.IsNullOrWhiteSpace(query))
            {
                sender.ItemsSource = null;
                return;
            }

            // 搜索匹配的功能
            var matches = _searchItems
                .Where(item => item.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Name)
                .ToList();

            sender.ItemsSource = matches;
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.ChosenSuggestion?.ToString() ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(query))
            return;

        // 查找匹配的页面
        var match = _searchItems.FirstOrDefault(item =>
            item.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            item.Keywords.Any(k => k.Equals(query, StringComparison.OrdinalIgnoreCase)));

        if (match != default)
        {
            // 跳转到对应页面
            NavigateToPage(match.Tag);

            // 清空搜索框
            sender.Text = "";
        }
    }

    private void NavigateToPage(string tag)
    {
        // 隐藏所有页面容器
        HomePage.Visibility = Visibility.Collapsed;
        StartupPageContainer.Visibility = Visibility.Collapsed;
        ChatPageContainer.Visibility = Visibility.Collapsed;
        QuickQAPageContainer.Visibility = Visibility.Collapsed;
        IEChatPageContainer.Visibility = Visibility.Collapsed;
        LLaVAPageContainer.Visibility = Visibility.Collapsed;
        SDPageContainer.Visibility = Visibility.Collapsed;
        HelpPageContainer.Visibility = Visibility.Collapsed;
        OpenVINOWhisperPageContainer.Visibility = Visibility.Collapsed;
        SettingsPageContainer.Visibility = Visibility.Collapsed;

        // 显示目标页面
        switch (tag)
        {
            case "home":
                HomePage.Visibility = Visibility.Visible;
                SelectNavigationItem("home");
                break;
            case "startup":
                StartupPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("startup");
                break;
            case "chat":
                ChatPageContainer.Visibility = Visibility.Visible;
                _currentDialogMode = "normal";
                _isRAGMode = false;
                SelectNavigationItem("chat");
                break;
            case "quickqa":
                QuickQAPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("quickqa");
                break;
            case "ie_chat":
                IEChatPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("ie_chat");
                break;
            case "llava":
                LLaVAPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("llava");
                break;
            case "sd":
                SDPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("sd");
                break;
            case "openvino_whisper":
                OpenVINOWhisperPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("openvino_whisper");
                break;
            case "help":
                HelpPageContainer.Visibility = Visibility.Visible;
                SelectNavigationItem("help");
                break;
            case "settings":
                SettingsPageContainer.Visibility = Visibility.Visible;
                // 选中设置项 - 通过设置SelectedItem为null来让系统选中设置
                NavView.SelectedItem = NavView.SettingsItem;
                break;
        }
    }

    private void SelectNavigationItem(string tag)
    {
        // 取消设置项的选中状态
        if (NavView.SelectedItem == NavView.SettingsItem)
        {
            NavView.SelectedItem = null;
        }

        // 查找并选中对应的导航项
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == tag)
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
    }

    // ========== 主题切换 ==========
    // Note: ThemeComboBox_SelectionChanged has been moved to SettingsPage.xaml.cs
    // This method is no longer needed as the ThemeComboBox control is now in SettingsPage

    // ========== 导航切换 ==========
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // 隐藏所有页面容器
        HomePage.Visibility = Visibility.Collapsed;
        StartupPageContainer.Visibility = Visibility.Collapsed;
        OpenVINOWhisperPageContainer.Visibility = Visibility.Collapsed;
        StreamingMeetingPageContainer.Visibility = Visibility.Collapsed;
        ChatPageContainer.Visibility = Visibility.Collapsed;
        QuickQAPageContainer.Visibility = Visibility.Collapsed;
        IEChatPageContainer.Visibility = Visibility.Collapsed;
        LLaVAPageContainer.Visibility = Visibility.Collapsed;
        SDPageContainer.Visibility = Visibility.Collapsed;
        HelpPageContainer.Visibility = Visibility.Collapsed;
        SettingsPageContainer.Visibility = Visibility.Collapsed;

        if (args.IsSettingsSelected)
        {
            // 显示设置页面
            SettingsPageContainer.Visibility = Visibility.Visible;
            if (SettingsFrame.Content == null)
            {
                SettingsFrame.Navigate(typeof(Pages.SettingsPage));
            }
        }
        else if (args.SelectedItemContainer != null)
        {
            var tag = args.SelectedItemContainer.Tag?.ToString();

            switch (tag)
            {
                case "home":
                    HomePage.Visibility = Visibility.Visible;
                    break;
                case "startup":
                    StartupPageContainer.Visibility = Visibility.Visible;
                    if (StartupFrame.Content == null)
                    {
                        StartupFrame.Navigate(typeof(Pages.StartupPage));
                    }
                    break;
                case "openvino_whisper":
                    OpenVINOWhisperPageContainer.Visibility = Visibility.Visible;
                    if (OpenVINOWhisperFrame.Content == null)
                    {
                        OpenVINOWhisperFrame.Navigate(typeof(Pages.OpenVINOWhisperPage));
                    }
                    break;
                case "streaming_meeting":
                    StreamingMeetingPageContainer.Visibility = Visibility.Visible;
                    if (StreamingMeetingFrame.Content == null)
                    {
                        StreamingMeetingFrame.Navigate(typeof(Pages.StreamingMeetingPage));
                    }
                    break;
                case "chat":
                    ChatPageContainer.Visibility = Visibility.Visible;
                    if (ChatFrame.Content == null)
                    {
                        ChatFrame.Navigate(typeof(Pages.ChatPage));
                    }
                    // 设置为普通对话模式
                    _currentDialogMode = "normal";
                    _isRAGMode = false;
                    // 清理其他模式的流式消息状态
                    if (_quickQAStreamingMessage != null)
                    {
                        _quickQAStreamingMessage.IsStreaming = false;
                        _quickQAStreamingMessage = null;
                    }
                    if (_ieStreamingMessage != null)
                    {
                        _ieStreamingMessage.IsStreaming = false;
                        _ieStreamingMessage = null;
                    }
                    if (_ragStreamingMessage != null)
                    {
                        _ragStreamingMessage.IsStreaming = false;
                        _ragStreamingMessage = null;
                    }
                    break;
                case "quickqa":
                    QuickQAPageContainer.Visibility = Visibility.Visible;
                    if (QuickQAFrame.Content == null)
                    {
                        QuickQAFrame.Navigate(typeof(Pages.QuickQAPage));
                    }
                    // 设置为QuickQA模式
                    _currentDialogMode = "quickqa";
                    _isRAGMode = false;
                    // 清理其他模式的流式消息状态
                    if (_normalStreamingMessage != null)
                    {
                        _normalStreamingMessage.IsStreaming = false;
                        _normalStreamingMessage = null;
                    }
                    if (_ieStreamingMessage != null)
                    {
                        _ieStreamingMessage.IsStreaming = false;
                        _ieStreamingMessage = null;
                    }
                    if (_ragStreamingMessage != null)
                    {
                        _ragStreamingMessage.IsStreaming = false;
                        _ragStreamingMessage = null;
                    }
                    break;
                case "ie_chat":
                    IEChatPageContainer.Visibility = Visibility.Visible;
                    if (IEChatFrame.Content == null)
                    {
                        IEChatFrame.Navigate(typeof(Pages.IEChatPage));
                    }
                    // Set to IE Chat mode
                    _currentDialogMode = "ie_chat";
                    _isRAGMode = false;
                    // Update _chatHistory pointer
                    _chatHistory = _ieChatHistory;
                    // Clean up other modes' streaming messages
                    if (_normalStreamingMessage != null)
                    {
                        _normalStreamingMessage.IsStreaming = false;
                        _normalStreamingMessage = null;
                    }
                    if (_quickQAStreamingMessage != null)
                    {
                        _quickQAStreamingMessage.IsStreaming = false;
                        _quickQAStreamingMessage = null;
                    }
                    if (_ieStreamingMessage != null)
                    {
                        _ieStreamingMessage.IsStreaming = false;
                        _ieStreamingMessage = null;
                    }
                    if (_ragStreamingMessage != null)
                    {
                        _ragStreamingMessage.IsStreaming = false;
                        _ragStreamingMessage = null;
                    }
                    if (_ragChatStreamingMessage != null)
                    {
                        _ragChatStreamingMessage.IsStreaming = false;
                        _ragChatStreamingMessage = null;
                    }
                    break;
                case "llava":
                    LLaVAPageContainer.Visibility = Visibility.Visible;
                    if (LLaVAFrame.Content == null)
                    {
                        LLaVAFrame.Navigate(typeof(Pages.LLaVAPage));
                    }
                    // Set to Visual Understanding mode
                    _currentDialogMode = "visual";
                    _isRAGMode = false;
                    // Clean up other modes' streaming messages
                    if (_normalStreamingMessage != null)
                    {
                        _normalStreamingMessage.IsStreaming = false;
                        _normalStreamingMessage = null;
                    }
                    if (_quickQAStreamingMessage != null)
                    {
                        _quickQAStreamingMessage.IsStreaming = false;
                        _quickQAStreamingMessage = null;
                    }
                    if (_ieStreamingMessage != null)
                    {
                        _ieStreamingMessage.IsStreaming = false;
                        _ieStreamingMessage = null;
                    }
                    if (_ragStreamingMessage != null)
                    {
                        _ragStreamingMessage.IsStreaming = false;
                        _ragStreamingMessage = null;
                    }
                    break;
                case "sd":
                    SDPageContainer.Visibility = Visibility.Visible;
                    if (SDFrame.Content == null)
                    {
                        SDFrame.Navigate(typeof(Pages.SDPage));
                    }
                    break;
                case "help":
                    HelpPageContainer.Visibility = Visibility.Visible;
                    if (HelpFrame.Content == null)
                    {
                        HelpFrame.Navigate(typeof(Pages.HelpPage));
                    }
                    break;
            }
        }
    }
}

