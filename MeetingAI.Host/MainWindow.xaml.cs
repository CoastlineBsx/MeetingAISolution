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

        // 初始化 Granite 对话
        InitializeGranite();
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

    // ========== 搜索功能 ==========
    private readonly List<(string Name, string Tag, string[] Keywords)> _searchItems = new()
    {
        ("主页", "home", new[] { "主页", "home", "worker", "启动", "录音", "转录", "麦克风" }),
        ("智能对话", "chat", new[] { "智能对话", "对话", "chat", "ai", "聊天", "问答", "普通" }),
        ("文档助手", "quickqa", new[] { "文档助手", "快速问答", "quickqa", "文档", "问答", "上传" }),
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
        // 隐藏所有页面
        HomePage.Visibility = Visibility.Collapsed;
        ChatPage.Visibility = Visibility.Collapsed;
        QuickQAPage.Visibility = Visibility.Collapsed;
        IEPage.Visibility = Visibility.Collapsed;
        RAGPage.Visibility = Visibility.Collapsed;
        LLaVAPage.Visibility = Visibility.Collapsed;
        HelpPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        // 显示目标页面
        switch (tag)
        {
            case "home":
                HomePage.Visibility = Visibility.Visible;
                SelectNavigationItem("home");
                break;
            case "chat":
                ChatPage.Visibility = Visibility.Visible;
                ChatHistoryListChat.ItemsSource = _normalChatHistory;
                _currentDialogMode = "normal";
                _isRAGMode = false;
                SelectNavigationItem("chat");
                break;
            case "quickqa":
                QuickQAPage.Visibility = Visibility.Visible;
                SelectNavigationItem("quickqa");
                break;
            case "ie":
                IEPage.Visibility = Visibility.Visible;
                SelectNavigationItem("ie");
                break;
            case "rag":
                RAGPage.Visibility = Visibility.Visible;
                SelectNavigationItem("rag");
                break;
            case "llava":
                LLaVAPage.Visibility = Visibility.Visible;
                SelectNavigationItem("llava");
                break;
            case "help":
                HelpPage.Visibility = Visibility.Visible;
                SelectNavigationItem("help");
                break;
            case "settings":
                SettingsPage.Visibility = Visibility.Visible;
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

