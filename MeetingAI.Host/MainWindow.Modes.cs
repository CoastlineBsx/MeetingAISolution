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
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

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
    /// 快速问答模式
    /// </summary>
    private async void BtnQuickQA_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "quickqa";
            _isRAGMode = false;

            // 隐藏 RAG 控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            // 显示 QuickQA 控件
            BtnQuickQALoad.Visibility = Visibility.Visible;
            BtnQuickQALoad.IsEnabled = true;
            LblQuickQADoc.Visibility = Visibility.Visible;
            BtnQuickQAClear.Visibility = Visibility.Visible;
            // BtnQuickQAClear.IsEnabled 由 UpdateQuickQAUI() 控制

            // 更新状态提示
            LblModeStatus.Text = "⚡ 快速问答模式";

            // 更新文档显示
            UpdateQuickQAUI();

            await AppendLineAsync("[模式] 已切换到快速问答模式");
            await AppendLineAsync("[模式] 💡 快速问答适合处理单个小型文档（<50K tokens）");
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
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

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

            // 隐藏 QuickQA 控件
            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

            // 显示RAG专用控件
            BtnRAGTest.Visibility = Visibility.Visible;
            BtnRAGTest.IsEnabled = true;
            BtnDocumentManage.Visibility = Visibility.Visible;
            BtnDocumentManage.IsEnabled = true;
            DocumentExpander.Visibility = Visibility.Visible;
            DocumentExpander.IsExpanded = false; // 不自动展开，等用户点击按钮

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

    /// <summary>
    /// 文档管理按钮点击 - 切换展开/折叠
    /// </summary>
    private void BtnDocumentManage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 切换文档管理区域的展开状态
            DocumentExpander.IsExpanded = !DocumentExpander.IsExpanded;
        }
        catch (Exception ex)
        {
            // 静默处理，不影响用户
        }
    }
}
