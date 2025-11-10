using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using MeetingAI.Host.Models;
using Windows.System;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // 四个模式各自独立的对话历史
    private ObservableCollection<ChatMessage> _normalChatHistory = new();
    private ObservableCollection<ChatMessage> _quickQAChatHistory = new();
    private ObservableCollection<ChatMessage> _ieChatHistory = new();
    private ObservableCollection<ChatMessage> _ragChatHistory = new();

    // 当前激活的对话历史（指向上面4个之一）
    private ObservableCollection<ChatMessage> _chatHistory = new();

    // 当前模式：single 或 multi
    private string _graniteMode = "single";

    // 当前流式输出的消息
    private ChatMessage? _currentStreamingMessage = null;

    // 滚动节流计数器（每N个token滚动一次）
    private int _scrollThrottleCounter = 0;

    // 缓存的 ScrollViewer（用于直接滚动到底部）
    private ScrollViewer? _chatScrollViewer = null;

    private void InitializeGranite()
    {
        // 默认使用普通模式的历史
        _chatHistory = _normalChatHistory;
        ChatHistoryList.ItemsSource = _chatHistory;

        // 等待 ListView 加载完成后获取内部 ScrollViewer
        ChatHistoryList.Loaded += (s, e) =>
        {
            _chatScrollViewer = FindScrollViewer(ChatHistoryList);
        };
    }

    // 递归查找 ScrollViewer
    private ScrollViewer? FindScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv)
                return sv;

            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }

    // ========== 获取系统提示词 ==========
    private string GetSystemPrompt()
    {
        // IE模式：使用极简system prompt
        if (_currentDialogMode == "ie")
        {
            return "基于提供的文档信息回答问题。";
        }

        // 检查是否在ChatPage，如果是则从ChatPage的ComboBox读取
        bool isInChatPage = ChatPage.Visibility == Visibility.Visible;

        if (isInChatPage)
        {
            // ChatPage使用ComboBox
            if (CmbAnswerStyleChat?.SelectedItem is ComboBoxItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString();
                return tag switch
                {
                    "Simple" => "Use simple, easy-to-understand language. Avoid jargon. Explain like teaching a beginner.",
                    "Professional" => "Use technical terminology and professional language. Assume expert-level knowledge. Be concise and precise.",
                    _ => "Provide clear, accurate answers. Use appropriate technical terms with explanations when needed."
                };
            }
        }
        else
        {
            // HomePage使用RadioButtons
            if (RbSimple.IsChecked == true)
            {
                return "Use simple, easy-to-understand language. Avoid jargon. Explain like teaching a beginner.";
            }
            else if (RbProfessional.IsChecked == true)
            {
                return "Use technical terminology and professional language. Assume expert-level knowledge. Be concise and precise.";
            }
        }

        // Default to Normal mode
        return "Provide clear, accurate answers. Use appropriate technical terms with explanations when needed.";
    }

    // ========== 模式切换 ==========
    private async void CmbConversationMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem selectedItem)
            return;

        var tag = selectedItem.Tag?.ToString();
        if (string.IsNullOrEmpty(tag))
            return;

        try
        {
            await EnsurePipeAsync();

            if (tag == "Single")
            {
                // 如果当前是多轮模式，先结束会话
                if (_graniteMode == "multi")
                {
                    var finishCmd = JsonSerializer.Serialize(
                        new GraniteFinishChatCommand(),
                        AppJsonContext.Utf8.GraniteFinishChatCommand
                    ) + "\n";
                    await SendJsonAsync(finishCmd);
                }

                _graniteMode = "single";
                LblGraniteMode.Text = "[单轮模式] 每次独立回答";
                await AppendLineAsync("[Granite] 已切换到单轮模式");
            }
            else if (tag == "Multi")
            {
                // 启动多轮会话
                var startCmd = JsonSerializer.Serialize(
                    new GraniteStartChatCommand
                    {
                        system_message = GetSystemPrompt()
                    },
                    AppJsonContext.Utf8.GraniteStartChatCommand
                ) + "\n";

                await SendJsonAsync(startCmd);

                _graniteMode = "multi";
                LblGraniteMode.Text = "[多轮模式] 保留上下文";
                await AppendLineAsync("[Granite] 已切换到多轮模式，会话已开始");
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Granite] 切换模式失败：{ex.Message}");
        }
    }

    private async void BtnGraniteSingle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            // 如果当前是多轮模式，先结束会话
            if (_graniteMode == "multi")
            {
                var finishCmd = JsonSerializer.Serialize(
                    new GraniteFinishChatCommand(),
                    AppJsonContext.Utf8.GraniteFinishChatCommand
                ) + "\n";
                await SendJsonAsync(finishCmd);
            }

            _graniteMode = "single";
            LblGraniteMode.Text = "[单轮模式] 每次独立回答";
            BtnGraniteSingle.IsEnabled = false;
            BtnGraniteMulti.IsEnabled = true;

            await AppendLineAsync("[Granite] 已切换到单轮模式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Granite] 切换模式失败：{ex.Message}");
        }
    }

    private async void BtnGraniteMulti_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            // 启动多轮会话
            var startCmd = JsonSerializer.Serialize(
                new GraniteStartChatCommand
                {
                    system_message = GetSystemPrompt()
                },
                AppJsonContext.Utf8.GraniteStartChatCommand
            ) + "\n";

            await SendJsonAsync(startCmd);

            _graniteMode = "multi";
            LblGraniteMode.Text = "[多轮模式] 保留上下文";
            BtnGraniteSingle.IsEnabled = true;
            BtnGraniteMulti.IsEnabled = false;

            await AppendLineAsync("[Granite] 已切换到多轮模式，会话已开始");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Granite] 切换模式失败：{ex.Message}");
        }
    }

    // ========== 清空历史 ==========
    private async void BtnGraniteClear_Click(object sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();

        // 如果是快速问答模式，清空对话历史（但不清除文档）
        if (_currentDialogMode == "quickqa")
        {
            ClearQuickQAHistory();
        }

        // 如果是IE模式，清空对话历史（但不清除文档和提取结果）
        if (_currentDialogMode == "ie")
        {
            ClearIEDialogHistory();
        }

        // 如果是多轮模式，需要重启会话
        if (_graniteMode == "multi")
        {
            try
            {
                var finishCmd = JsonSerializer.Serialize(
                    new GraniteFinishChatCommand(),
                    AppJsonContext.Utf8.GraniteFinishChatCommand
                ) + "\n";
                await SendJsonAsync(finishCmd);

                var startCmd = JsonSerializer.Serialize(
                    new GraniteStartChatCommand
                    {
                        system_message = GetSystemPrompt()
                    },
                    AppJsonContext.Utf8.GraniteStartChatCommand
                ) + "\n";
                await SendJsonAsync(startCmd);

                await AppendLineAsync("[Granite] 多轮会话已重置");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Granite] 重置会话失败：{ex.Message}");
            }
        }
    }

    // ========== 发送消息 ==========
    private async void BtnGraniteSend_Click(object sender, RoutedEventArgs e)
    {
        await SendGraniteMessageAsync();
    }

    private void TxtGraniteInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+Enter 发送
        if (e.Key == VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);

            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                _ = SendGraniteMessageAsync();
            }
        }
    }

    private async Task SendGraniteMessageAsync()
    {
        try
        {
            // 检查是否在ChatPage，如果是则从ChatPage读取
            bool isInChatPage = ChatPage.Visibility == Visibility.Visible;

            var userInput = isInChatPage ? TxtGraniteInputChat.Text.Trim() : TxtGraniteInput.Text.Trim();
            if (string.IsNullOrEmpty(userInput)) return;

            await EnsurePipeAsync();

            // 添加用户消息到历史
            var userMessage = new ChatMessage
            {
                Role = "user",
                Content = userInput
            };
            _chatHistory.Add(userMessage);

            // 创建 AI 消息占位符（用于流式追加）
            _currentStreamingMessage = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                IsStreaming = true
            };
            _chatHistory.Add(_currentStreamingMessage);

            // 重置滚动计数器
            _scrollThrottleCounter = 0;

            // 自动滚动到底部
            var chatListView = isInChatPage ? ChatHistoryListChat : ChatHistoryList;
            if (chatListView.Items.Count > 0)
            {
                chatListView.ScrollIntoView(chatListView.Items[^1]);
            }

            // 清空输入框
            if (isInChatPage)
            {
                TxtGraniteInputChat.Text = "";
            }
            else
            {
                TxtGraniteInput.Text = "";
            }

            // ========== IE 对话模式处理 ==========
            string promptToSend = userInput;
            if (_currentDialogMode == "ie")
            {
                // 检查是否可以继续对话
                if (!CanContinueIEDialog(out string errorMessage))
                {
                    await AppendLineAsync($"[IE] {errorMessage}");

                    // 移除刚添加的用户消息和AI占位符
                    _chatHistory.Remove(userMessage);
                    _chatHistory.Remove(_currentStreamingMessage);
                    _currentStreamingMessage = null;
                    return;
                }

                // 构建IE Dialog Prompt
                promptToSend = BuildIEDialogPrompt(userInput);

                // 保存用户问题（AI回答完成后会添加到历史）
                await AppendLineAsync($"[IE] 对话轮数：{_ieDialogHistory.Count + 1}/{MAX_IE_TURNS}");
            }
            // ========== 快速问答模式处理 ==========
            else if (_currentDialogMode == "quickqa")
            {
                // 检查是否可以继续对话
                if (!CanContinueQuickQA(out string errorMessage))
                {
                    await AppendLineAsync($"[快速问答] {errorMessage}");

                    // 移除刚添加的用户消息和AI占位符
                    _chatHistory.Remove(userMessage);
                    _chatHistory.Remove(_currentStreamingMessage);
                    _currentStreamingMessage = null;
                    return;
                }

                // 构建QuickQA Prompt
                promptToSend = BuildQuickQAPrompt(userInput);

                // 保存用户问题（AI回答完成后会添加到历史）
                await AppendLineAsync($"[快速问答] 对话轮数：{_quickQAHistory.Count + 1}/{MAX_TURNS}");
            }
            // ========== RAG 检索集成 ==========
            else if (_isRAGMode && _ragService != null && _isRAGInitialized)
            {
                try
                {
                    await AppendLineAsync("[RAG] 检索相关文档中...");

                    var ragContext = await _ragService.RetrieveContextAsync(userInput);

                    if (ragContext.Citations.Count > 0)
                    {
                        // 保存引用到当前消息
                        _currentStreamingMessage.Citations = ragContext.Citations;

                        // 构建包含上下文的 Prompt
                        promptToSend = BuildRAGPrompt(userInput, ragContext.ContextText);

                        await AppendLineAsync($"[RAG] 找到 {ragContext.Citations.Count} 条相关文档");
                    }
                    else
                    {
                        await AppendLineAsync("[RAG] 未找到相关文档，使用普通模式回答");
                    }
                }
                catch (Exception ex)
                {
                    await AppendLineAsync($"[RAG] 检索失败: {ex.Message}，使用普通模式");
                }
            }

            // 获取参数 - 根据所在页面读取
            var maxTokensCombo = isInChatPage ? CmbMaxTokensChat : CmbMaxTokens;
            var temperatureCombo = isInChatPage ? CmbTemperatureChat : CmbTemperature;

            int maxTokens;
            float temperature;

            if (isInChatPage)
            {
                // ChatPage: 从Tag读取
                string maxTokensTag = ((ComboBoxItem)maxTokensCombo.SelectedItem).Tag?.ToString() ?? "1024";
                string temperatureTag = ((ComboBoxItem)temperatureCombo.SelectedItem).Tag?.ToString() ?? "0.7";
                maxTokens = int.Parse(maxTokensTag);
                temperature = float.Parse(temperatureTag);
            }
            else
            {
                // HomePage: 从Content读取并提取数字
                string maxTokensStr = ((ComboBoxItem)maxTokensCombo.SelectedItem).Content.ToString()!;
                string temperatureStr = ((ComboBoxItem)temperatureCombo.SelectedItem).Content.ToString()!;
                maxTokens = int.Parse(maxTokensStr.Split(' ')[0]);
                temperature = float.Parse(temperatureStr.Split(' ')[0]);
            }

            // 根据模式发送命令
            string json;
            if (_graniteMode == "single")
            {
                // 单轮模式：使用Granite的chat template格式
                string fullPrompt =
                    $"<|start_of_role|>system<|end_of_role|>{GetSystemPrompt()}<|end_of_text|>" +
                    $"<|start_of_role|>user<|end_of_role|>{promptToSend}<|end_of_text|>" +
                    $"<|start_of_role|>assistant<|end_of_role|>";

                // 调试：输出最终发送的prompt
                if (_currentDialogMode == "ie")
                {
                    await AppendLineAsync($"[GRANITE DEBUG] 最终fullPrompt长度：{fullPrompt.Length} 字符");
                    await AppendLineAsync($"[GRANITE DEBUG] fullPrompt前300字符：{(fullPrompt.Length > 300 ? fullPrompt.Substring(0, 300) : fullPrompt)}...");
                }

                var cmd = new GraniteGenerateStreamCommand
                {
                    prompt = fullPrompt,
                    max_tokens = maxTokens,
                    temperature = temperature
                };
                json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";
            }
            else
            {
                var cmd = new GraniteChatStreamCommand
                {
                    prompt = promptToSend,
                    max_tokens = maxTokens,
                    temperature = temperature
                };
                json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteChatStreamCommand) + "\n";
            }

            await SendJsonAsync(json);
            await AppendLineAsync($"[Granite] 已发送 ({_graniteMode}{(_isRAGMode ? " + RAG" : "")})：{userInput}");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Granite] 发送失败：{ex.Message}");
            if (_currentStreamingMessage != null)
            {
                _currentStreamingMessage.Content = $"❌ 错误：{ex.Message}";
                _currentStreamingMessage.IsStreaming = false;
                _currentStreamingMessage = null;
            }
        }
    }

    // ========== 处理流式响应 ==========
    private Task HandleGraniteStreamToken(string token)
    {
        // ========== IE文档类型识别 ==========
        if (_isIEDetecting && _ieDetectionBuffer != null)
        {
            _ieDetectionBuffer.Append(token);
            return Task.CompletedTask;
        }

        // ========== IE信息提取 ==========
        if (_isIEExtracting && _ieExtractionBuffer != null)
        {
            _ieExtractionBuffer.Append(token);
            return Task.CompletedTask;
        }

        // ========== 正常聊天模式 ==========
        if (_currentStreamingMessage == null)
            return Task.CompletedTask;

        // 投递到 UI 线程
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            // 直接修改 Content 属性，INotifyPropertyChanged 会自动通知 UI 更新
            _currentStreamingMessage.Content += token;

            // 滚动节流：每5个token滚动一次，减少性能消耗
            _scrollThrottleCounter++;
            if (_scrollThrottleCounter >= 5)
            {
                _scrollThrottleCounter = 0;
                ScrollToBottom();
            }
        });

        return Task.CompletedTask;
    }

    private void HandleGraniteStreamDone()
    {
        // ========== IE文档类型识别完成 ==========
        if (_isIEDetecting && _ieDetectionBuffer != null)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                string typeId = _ieDetectionBuffer.ToString().Trim();
                _ieDetectionBuffer = null;
                _isIEDetecting = false;

                await HandleIEDetectionResult(typeId);
            });
            return;
        }

        // ========== IE信息提取完成 ==========
        if (_isIEExtracting && _ieExtractionBuffer != null)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                string fullJson = _ieExtractionBuffer.ToString();
                _ieExtractionBuffer = null;
                _isIEExtracting = false;

                await HandleIEExtractionResult(fullJson);
            });
            return;
        }

        // ========== 正常聊天模式 ==========
        // 必须在 UI 线程执行，因为会触发 PropertyChanged
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentStreamingMessage != null)
            {
                _currentStreamingMessage.IsStreaming = false;

                // 如果是快速问答模式，保存对话历史
                if (_currentDialogMode == "quickqa" && _chatHistory.Count >= 2)
                {
                    // 获取最后一对问答
                    var userMsg = _chatHistory[^2];  // 倒数第二个是用户消息
                    var aiMsg = _chatHistory[^1];    // 最后一个是AI回答

                    if (userMsg.Role == "user" && aiMsg.Role == "assistant")
                    {
                        AddQuickQAHistory(userMsg.Content, aiMsg.Content);
                    }
                }

                // 如果是IE对话模式，保存对话历史
                if (_currentDialogMode == "ie" && _chatHistory.Count >= 2)
                {
                    // 获取最后一对问答
                    var userMsg = _chatHistory[^2];  // 倒数第二个是用户消息
                    var aiMsg = _chatHistory[^1];    // 最后一个是AI回答

                    if (userMsg.Role == "user" && aiMsg.Role == "assistant")
                    {
                        AddIEDialogHistory(userMsg.Content, aiMsg.Content);
                    }
                }

                _currentStreamingMessage = null;
            }

            // 重置滚动计数器，并确保最后一次滚动到底部
            _scrollThrottleCounter = 0;
            ScrollToBottom();
        });
    }

    // ========== 滚动到底部 ==========
    private void ScrollToBottom()
    {
        if (_chatScrollViewer != null)
        {
            // 使用 ChangeView 直接滚动到最底部
            // 参数：horizontal offset, vertical offset, zoom factor
            // null = 保持当前值，double.MaxValue = 滚动到最大值（底部）
            _chatScrollViewer.ChangeView(null, _chatScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
    }

    // ========== 复制消息内容 ==========
    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.DataContext is ChatMessage message)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(message.Content);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
    }
}
