using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host;

/// <summary>
/// 4个对话模式的按钮事件处理
/// </summary>
public partial class MainWindow
{
    private string _currentDialogMode = ""; // "normal", "quickqa", "ie", "rag"

    /// <summary>
    /// 普通对话模式
    /// </summary>
    private async void BtnNormalMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "normal";
            _isRAGMode = false;

            // 隐藏所有模式特定控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            // 更新状态提示
            LblModeStatus.Text = "✅ 普通对话模式";

            await AppendLineAsync("[模式] 已切换到普通对话模式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[模式] 切换失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 快速问答模式（占位）
    /// </summary>
    private async void BtnQuickQA_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "quickqa";
            _isRAGMode = false;

            // 隐藏所有模式特定控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            // 更新状态提示
            LblModeStatus.Text = "⚡ 快速问答模式（开发中）";

            await AppendLineAsync("[模式] 快速问答模式暂未实现");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[模式] 切换失败：{ex.Message}");
        }
    }

    /// <summary>
    /// IE模式（占位）
    /// </summary>
    private async void BtnIEMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "ie";
            _isRAGMode = false;

            // 隐藏所有模式特定控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            // 更新状态提示
            LblModeStatus.Text = "🔍 IE模式（开发中）";

            await AppendLineAsync("[模式] IE模式暂未实现");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[模式] 切换失败：{ex.Message}");
        }
    }

    /// <summary>
    /// RAG模式
    /// </summary>
    private async void BtnRAGMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "rag";

            // 显示RAG专用控件
            BtnRAGTest.Visibility = Visibility.Visible;
            BtnRAGTest.IsEnabled = true;
            DocumentExpander.Visibility = Visibility.Visible;
            DocumentExpander.IsExpanded = true;

            // 更新状态提示
            LblModeStatus.Text = "📚 RAG模式";

            // 自动初始化RAG（如果还没初始化）
            if (!_isRAGInitialized)
            {
                await AppendLineAsync("[RAG] 自动初始化 RAG 服务...");
                await InitializeRAGAsync();
            }

            _isRAGMode = true;
            await AppendLineAsync("[模式] 已切换到 RAG 模式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[模式] 切换失败：{ex.Message}");
        }
    }
}
