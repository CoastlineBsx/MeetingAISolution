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
    /// 切换聊天历史（实现模式隔离）
    /// </summary>
    private void SwitchChatHistory(System.Collections.ObjectModel.ObservableCollection<Models.ChatMessage> targetHistory)
    {
        // 如果当前有流式输出正在进行，先结束它
        if (_currentStreamingMessage != null)
        {
            _currentStreamingMessage.IsStreaming = false;
            _currentStreamingMessage = null;
        }

        // 切换到目标模式的历史
        _chatHistory = targetHistory;

        // 更新UI绑定
        DispatcherQueue.TryEnqueue(() =>
        {
            ChatHistoryList.ItemsSource = _chatHistory;

            // 滚动到底部
            if (_chatHistory.Count > 0)
            {
                ChatHistoryList.ScrollIntoView(_chatHistory[^1]);
            }
        });
    }

    /// <summary>
    /// 普通对话模式
    /// </summary>
    private async void BtnNormalMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "normal";
            _isRAGMode = false;

            // 显示 Granite 面板，隐藏 LLaVA 面板
            ChatHistoryList.Visibility = Visibility.Visible;
            TxtGraniteInput.Visibility = Visibility.Visible;
            BtnGraniteSend.Visibility = Visibility.Visible;
            LLaVAPanel.Visibility = Visibility.Collapsed;

            // 切换到普通模式的聊天历史
            SwitchChatHistory(_normalChatHistory);

            // 隐藏所有模式特定控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

            BtnIELoad.Visibility = Visibility.Collapsed;
            CmbIETemplate.Visibility = Visibility.Collapsed;
            LblIEDoc.Visibility = Visibility.Collapsed;
            BtnIEExtract.Visibility = Visibility.Collapsed;
            LblIEStatus.Visibility = Visibility.Collapsed;
            BtnIECopyJSON.Visibility = Visibility.Collapsed;
            BtnIEExport.Visibility = Visibility.Collapsed;
            BtnIEReExtract.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog.Visibility = Visibility.Collapsed;

            // 主页的控件（带"2"后缀）也要隐藏
            BtnQuickQALoad2.Visibility = Visibility.Collapsed;
            LblQuickQADoc2.Visibility = Visibility.Collapsed;
            BtnQuickQAClear2.Visibility = Visibility.Collapsed;
            BtnRAGTest2.Visibility = Visibility.Collapsed;
            BtnDocumentManage2.Visibility = Visibility.Collapsed;
            BtnIELoad2.Visibility = Visibility.Collapsed;
            CmbIETemplate2.Visibility = Visibility.Collapsed;
            LblIEDoc2.Visibility = Visibility.Collapsed;
            BtnIEExtract2.Visibility = Visibility.Collapsed;
            LblIEStatus2.Visibility = Visibility.Collapsed;
            BtnIECopyJSON2.Visibility = Visibility.Collapsed;
            BtnIEExport2.Visibility = Visibility.Collapsed;
            BtnIEReExtract2.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog2.Visibility = Visibility.Collapsed;

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

            // 显示 Granite 面板，隐藏 LLaVA 面板
            ChatHistoryList.Visibility = Visibility.Visible;
            TxtGraniteInput.Visibility = Visibility.Visible;
            BtnGraniteSend.Visibility = Visibility.Visible;
            LLaVAPanel.Visibility = Visibility.Collapsed;

            // 切换到快速问答模式的聊天历史
            SwitchChatHistory(_quickQAChatHistory);

            // 隐藏 RAG 控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            // 隐藏 IE 控件
            BtnIELoad.Visibility = Visibility.Collapsed;
            CmbIETemplate.Visibility = Visibility.Collapsed;
            LblIEDoc.Visibility = Visibility.Collapsed;
            BtnIEExtract.Visibility = Visibility.Collapsed;
            LblIEStatus.Visibility = Visibility.Collapsed;
            BtnIECopyJSON.Visibility = Visibility.Collapsed;
            BtnIEExport.Visibility = Visibility.Collapsed;
            BtnIEReExtract.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog.Visibility = Visibility.Collapsed;

            // 显示 QuickQA 控件
            BtnQuickQALoad.Visibility = Visibility.Visible;
            BtnQuickQALoad.IsEnabled = true;
            LblQuickQADoc.Visibility = Visibility.Visible;
            BtnQuickQAClear.Visibility = Visibility.Visible;
            // BtnQuickQAClear.IsEnabled 由 UpdateQuickQAUI() 控制

            // 主页的控件（带"2"后缀）- 显示QuickQA控件，隐藏其他
            BtnQuickQALoad2.Visibility = Visibility.Visible;
            BtnQuickQALoad2.IsEnabled = true;
            LblQuickQADoc2.Visibility = Visibility.Visible;
            BtnQuickQAClear2.Visibility = Visibility.Visible;
            BtnRAGTest2.Visibility = Visibility.Collapsed;
            BtnDocumentManage2.Visibility = Visibility.Collapsed;
            BtnIELoad2.Visibility = Visibility.Collapsed;
            CmbIETemplate2.Visibility = Visibility.Collapsed;
            LblIEDoc2.Visibility = Visibility.Collapsed;
            BtnIEExtract2.Visibility = Visibility.Collapsed;
            LblIEStatus2.Visibility = Visibility.Collapsed;
            BtnIECopyJSON2.Visibility = Visibility.Collapsed;
            BtnIEExport2.Visibility = Visibility.Collapsed;
            BtnIEReExtract2.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog2.Visibility = Visibility.Collapsed;

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
    /// IE模式
    /// </summary>
    private async void BtnIEMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "ie";
            _isRAGMode = false;

            // 显示 Granite 面板，隐藏 LLaVA 面板
            ChatHistoryList.Visibility = Visibility.Visible;
            TxtGraniteInput.Visibility = Visibility.Visible;
            BtnGraniteSend.Visibility = Visibility.Visible;
            LLaVAPanel.Visibility = Visibility.Collapsed;

            // 切换到IE模式的聊天历史
            SwitchChatHistory(_ieChatHistory);

            // 隐藏其他模式控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

            // 显示 IE 控件
            BtnIELoad.Visibility = Visibility.Visible;
            BtnIELoad.IsEnabled = true;
            CmbIETemplate.Visibility = Visibility.Visible;
            CmbIETemplate.IsEnabled = true;
            LblIEDoc.Visibility = Visibility.Visible;
            BtnIEExtract.Visibility = Visibility.Visible;
            LblIEStatus.Visibility = Visibility.Visible;
            BtnIECopyJSON.Visibility = Visibility.Visible;
            BtnIEExport.Visibility = Visibility.Visible;
            BtnIEReExtract.Visibility = Visibility.Visible;
            BtnIEContinueDialog.Visibility = Visibility.Visible;

            // 填充模板下拉菜单
            CmbIETemplate.Items.Clear();
            foreach (var template in IETemplates.AllTemplates)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{template.Icon} {template.Name}",
                    Tag = template.Id
                };
                CmbIETemplate.Items.Add(item);
            }

            // 默认选择通用模板（最后一个）
            if (CmbIETemplate.Items.Count > 0)
            {
                CmbIETemplate.SelectedIndex = CmbIETemplate.Items.Count - 1;
            }

            // 主页的控件（带"2"后缀）- 显示IE控件，隐藏其他
            BtnQuickQALoad2.Visibility = Visibility.Collapsed;
            LblQuickQADoc2.Visibility = Visibility.Collapsed;
            BtnQuickQAClear2.Visibility = Visibility.Collapsed;
            BtnRAGTest2.Visibility = Visibility.Collapsed;
            BtnDocumentManage2.Visibility = Visibility.Collapsed;
            BtnIELoad2.Visibility = Visibility.Visible;
            BtnIELoad2.IsEnabled = true;
            CmbIETemplate2.Visibility = Visibility.Visible;
            CmbIETemplate2.IsEnabled = true;
            LblIEDoc2.Visibility = Visibility.Visible;
            BtnIEExtract2.Visibility = Visibility.Visible;
            LblIEStatus2.Visibility = Visibility.Visible;
            BtnIECopyJSON2.Visibility = Visibility.Visible;
            BtnIEExport2.Visibility = Visibility.Visible;
            BtnIEReExtract2.Visibility = Visibility.Visible;
            BtnIEContinueDialog2.Visibility = Visibility.Visible;

            // 填充主页模板下拉菜单
            CmbIETemplate2.Items.Clear();
            foreach (var template in IETemplates.AllTemplates)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{template.Icon} {template.Name}",
                    Tag = template.Id
                };
                CmbIETemplate2.Items.Add(item);
            }
            if (CmbIETemplate2.Items.Count > 0)
            {
                CmbIETemplate2.SelectedIndex = CmbIETemplate2.Items.Count - 1;
            }

            LblModeStatus.Text = "🔍 IE 模式";

            // 更新 IE UI 状态
            UpdateIEUI();

            await AppendLineAsync("[模式] 已切换到 IE 模式");
            await AppendLineAsync("[模式] 💡 IE模式适合从文档中提取结构化信息");
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

            // 显示 Granite 面板，隐藏 LLaVA 面板
            ChatHistoryList.Visibility = Visibility.Visible;
            TxtGraniteInput.Visibility = Visibility.Visible;
            BtnGraniteSend.Visibility = Visibility.Visible;
            LLaVAPanel.Visibility = Visibility.Collapsed;

            // 切换到RAG模式的聊天历史
            SwitchChatHistory(_ragChatHistory);

            // 隐藏 QuickQA 控件
            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

            // 隐藏 IE 控件
            BtnIELoad.Visibility = Visibility.Collapsed;
            CmbIETemplate.Visibility = Visibility.Collapsed;
            LblIEDoc.Visibility = Visibility.Collapsed;
            BtnIEExtract.Visibility = Visibility.Collapsed;
            LblIEStatus.Visibility = Visibility.Collapsed;
            BtnIECopyJSON.Visibility = Visibility.Collapsed;
            BtnIEExport.Visibility = Visibility.Collapsed;
            BtnIEReExtract.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog.Visibility = Visibility.Collapsed;

            // 显示RAG专用控件
            BtnRAGTest.Visibility = Visibility.Visible;
            BtnRAGTest.IsEnabled = true;
            BtnDocumentManage.Visibility = Visibility.Visible;
            BtnDocumentManage.IsEnabled = true;
            DocumentExpander.Visibility = Visibility.Visible;
            DocumentExpander.IsExpanded = false; // 不自动展开，等用户点击按钮

            // 主页的控件（带"2"后缀）- 显示RAG控件，隐藏其他
            BtnQuickQALoad2.Visibility = Visibility.Collapsed;
            LblQuickQADoc2.Visibility = Visibility.Collapsed;
            BtnQuickQAClear2.Visibility = Visibility.Collapsed;
            BtnRAGTest2.Visibility = Visibility.Visible;
            BtnRAGTest2.IsEnabled = true;
            BtnDocumentManage2.Visibility = Visibility.Visible;
            BtnDocumentManage2.IsEnabled = true;
            BtnIELoad2.Visibility = Visibility.Collapsed;
            CmbIETemplate2.Visibility = Visibility.Collapsed;
            LblIEDoc2.Visibility = Visibility.Collapsed;
            BtnIEExtract2.Visibility = Visibility.Collapsed;
            LblIEStatus2.Visibility = Visibility.Collapsed;
            BtnIECopyJSON2.Visibility = Visibility.Collapsed;
            BtnIEExport2.Visibility = Visibility.Collapsed;
            BtnIEReExtract2.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog2.Visibility = Visibility.Collapsed;

            LblModeStatus.Text = "📚 RAG 模式";

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

    /// <summary>
    /// LLaVA 视觉问答模式
    /// </summary>
    private async void BtnLLaVAMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDialogMode = "llava";
            _isRAGMode = false;

            // 隐藏 Granite 面板，显示 LLaVA 面板
            ChatHistoryList.Visibility = Visibility.Collapsed;
            TxtGraniteInput.Visibility = Visibility.Collapsed;
            BtnGraniteSend.Visibility = Visibility.Collapsed;
            RbSimple.Visibility = Visibility.Collapsed;
            RbNormal.Visibility = Visibility.Collapsed;
            RbProfessional.Visibility = Visibility.Collapsed;
            CmbTemperature.Visibility = Visibility.Collapsed;
            CmbMaxTokens.Visibility = Visibility.Collapsed;

            LLaVAPanel.Visibility = Visibility.Visible;

            // 隐藏其他模式控件
            BtnRAGTest.Visibility = Visibility.Collapsed;
            BtnDocumentManage.Visibility = Visibility.Collapsed;
            DocumentExpander.Visibility = Visibility.Collapsed;

            BtnQuickQALoad.Visibility = Visibility.Collapsed;
            LblQuickQADoc.Visibility = Visibility.Collapsed;
            BtnQuickQAClear.Visibility = Visibility.Collapsed;

            BtnIELoad.Visibility = Visibility.Collapsed;
            CmbIETemplate.Visibility = Visibility.Collapsed;
            LblIEDoc.Visibility = Visibility.Collapsed;
            BtnIEExtract.Visibility = Visibility.Collapsed;
            LblIEStatus.Visibility = Visibility.Collapsed;
            BtnIECopyJSON.Visibility = Visibility.Collapsed;
            BtnIEExport.Visibility = Visibility.Collapsed;
            BtnIEReExtract.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog.Visibility = Visibility.Collapsed;

            // 更新 LLaVA 聊天列表绑定
            LLaVAChatList.ItemsSource = _llavaChatHistory;

            // 启用 LLaVA 控件
            BtnUploadImage.IsEnabled = true;
            BtnLLaVASingle.IsEnabled = false;  // 默认单轮模式
            BtnLLaVAMulti.IsEnabled = true;
            BtnLLaVAClear.IsEnabled = true;
            BtnLLaVASend.IsEnabled = true;

            // 主页的控件（带"2"后缀）- 隐藏所有模式专用控件
            BtnQuickQALoad2.Visibility = Visibility.Collapsed;
            LblQuickQADoc2.Visibility = Visibility.Collapsed;
            BtnQuickQAClear2.Visibility = Visibility.Collapsed;
            BtnRAGTest2.Visibility = Visibility.Collapsed;
            BtnDocumentManage2.Visibility = Visibility.Collapsed;
            BtnIELoad2.Visibility = Visibility.Collapsed;
            CmbIETemplate2.Visibility = Visibility.Collapsed;
            LblIEDoc2.Visibility = Visibility.Collapsed;
            BtnIEExtract2.Visibility = Visibility.Collapsed;
            LblIEStatus2.Visibility = Visibility.Collapsed;
            BtnIECopyJSON2.Visibility = Visibility.Collapsed;
            BtnIEExport2.Visibility = Visibility.Collapsed;
            BtnIEReExtract2.Visibility = Visibility.Collapsed;
            BtnIEContinueDialog2.Visibility = Visibility.Collapsed;

            LblModeStatus.Text = "🖼️ 视觉问答模式";

            await AppendLineAsync("[模式] 已切换到 LLaVA 视觉问答模式");
            await AppendLineAsync("[模式] 💡 上传图片后可以对图片内容进行提问");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[模式] 切换失败：{ex.Message}");
        }
    }
}
