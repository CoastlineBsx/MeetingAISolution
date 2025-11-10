using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

/// <summary>
/// 快速问答模式逻辑
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MAX_TOKENS = 50000;  // 最大 50K tokens
    private const int MAX_TURNS = 10;      // 最多 10 轮对话

    /// <summary>
    /// 加载文档按钮点击事件
    /// </summary>
    private async void BtnQuickQALoad_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 创建文件选择器（仅支持 TXT, DOCX, PDF）
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".pdf");

            // 获取窗口句柄并初始化 picker
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            // 选择文件
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await AppendLineAsync($"[快速问答] 正在加载文档：{file.Name}");

            // 使用已有的 DocumentProcessor 解析文档
            if (_documentProcessor == null)
            {
                // 如果还没初始化（可能用户直接进入QuickQA模式），需要初始化
                var baseDir = AppContext.BaseDirectory;
                var tesseractDataPath = Path.Combine(baseDir, "tessdata");
                _documentProcessor = new RAG.Services.DocumentProcessor(tesseractDataPath);
            }

            // 提取文档内容
            var extracted = await _documentProcessor.ExtractAsync(file.Path);

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                await AppendLineAsync($"[快速问答] ⚠️ 文档内容为空");
                return;
            }

            await AppendLineAsync($"[快速问答] ✅ 提取完成，内容长度：{extracted.Content.Length} 字符");

            // 计算 Token 数
            await AppendLineAsync($"[快速问答] 🔢 正在计算 Token 数...");
            int tokenCount = await CountTokensAsync(extracted.Content);

            if (tokenCount <= 0)
            {
                await AppendLineAsync($"[快速问答] ⚠️ Token 计算失败，无法加载文档");
                return;
            }

            await AppendLineAsync($"[快速问答] Token 数：{tokenCount}");

            // 检查是否超过限制
            if (tokenCount > MAX_TOKENS)
            {
                await AppendLineAsync($"[快速问答] ⚠️ 文档过大（{tokenCount} tokens > {MAX_TOKENS} tokens）");
                await AppendLineAsync($"[快速问答] 💡 建议使用 RAG 模式或 IE 模式");
                return;
            }

            // 加载成功，保存文档信息
            _quickQADocumentContent = extracted.Content;
            _quickQADocumentName = extracted.FileName;
            _quickQADocumentSize = extracted.FileSize;
            _quickQATokenCount = tokenCount;

            // 清空对话历史
            _quickQAHistory.Clear();

            // 更新 UI
            UpdateQuickQAUI();

            await AppendLineAsync($"[快速问答] ✅ 文档加载成功：{_quickQADocumentName}");
            await AppendLineAsync($"[快速问答] 📊 文档大小：{FormatFileSize(_quickQADocumentSize)}，Token 数：{_quickQATokenCount}");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[快速问答] ❌ 加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清除文档按钮点击事件
    /// </summary>
    private async void BtnQuickQAClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _quickQADocumentContent = null;
            _quickQADocumentName = null;
            _quickQADocumentSize = 0;
            _quickQATokenCount = 0;
            _quickQAHistory.Clear();

            UpdateQuickQAUI();

            await AppendLineAsync("[快速问答] 文档已清除");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[快速问答] ❌ 清除失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新 QuickQA UI 状态
    /// </summary>
    private void UpdateQuickQAUI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(_quickQADocumentName))
            {
                // 未加载文档
                LblQuickQADoc.Text = "No document loaded";
                BtnQuickQAClear.IsEnabled = false;
                BtnQuickQASend.IsEnabled = false;
                BtnQuickQAClearHistory.IsEnabled = false;
            }
            else
            {
                // 已加载文档
                string sizeStr = FormatFileSize(_quickQADocumentSize);
                int currentTurn = _quickQAHistory.Count;
                LblQuickQADoc.Text = $"File: {_quickQADocumentName} ({sizeStr}, {_quickQATokenCount} tokens, {currentTurn}/{MAX_TURNS} turns)";
                BtnQuickQAClear.IsEnabled = true;
                BtnQuickQASend.IsEnabled = true;
                BtnQuickQAClearHistory.IsEnabled = true;
            }
        });
    }

    /// <summary>
    /// 构建快速问答的 Prompt
    /// </summary>
    public string BuildQuickQAPrompt(string userQuestion)
    {
        if (string.IsNullOrEmpty(_quickQADocumentContent))
        {
            throw new InvalidOperationException("No document loaded");
        }

        var sb = new StringBuilder();

        // 获取当前对话模式（从QuickQA页面的ComboBox）
        string mode = "single";
        if (CmbConversationModeQuickQA?.SelectedItem is ComboBoxItem selectedItem)
        {
            mode = selectedItem.Tag?.ToString() ?? "single";
        }

        // 判断是单轮模式还是多轮模式
        if (mode.ToLower() == "multi")
        {
            // ========== 多轮模式 ==========
            if (_quickQAHistory.Count == 0)
            {
                // 第1轮：发送文档 + 问题
                sb.AppendLine("You are a document Q&A assistant. Please carefully read the following document content, then answer the user's question.");
                sb.AppendLine();
                sb.AppendLine("=== Document Start ===");
                sb.AppendLine(_quickQADocumentContent);
                sb.AppendLine("=== Document End ===");
                sb.AppendLine();
                sb.AppendLine($"User question: {userQuestion}");
                sb.AppendLine();
                sb.AppendLine("Please answer based on the above document content. If the document doesn't contain relevant information, please clearly state that.");
            }
            else
            {
                // 第2轮及以后：只发送问题（Worker session已记住文档）
                sb.AppendLine(userQuestion);
            }
        }
        else
        {
            // ========== 单轮模式 ==========
            // 每次只发送文档 + 问题（不拼历史）
            sb.AppendLine("You are a document Q&A assistant. Please carefully read the following document content, then answer the user's question.");
            sb.AppendLine();
            sb.AppendLine("=== Document Start ===");
            sb.AppendLine(_quickQADocumentContent);
            sb.AppendLine("=== Document End ===");
            sb.AppendLine();
            sb.AppendLine($"User question: {userQuestion}");
            sb.AppendLine();
            sb.AppendLine("Please answer based on the above document content. If the document doesn't contain relevant information, please clearly state that.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 检查是否可以继续对话
    /// </summary>
    public bool CanContinueQuickQA(out string errorMessage)
    {
        // 检查是否加载了文档
        if (string.IsNullOrEmpty(_quickQADocumentContent))
        {
            errorMessage = "⚠️ 请先点击'📎 加载文件'";
            return false;
        }

        // 检查是否达到轮数限制
        if (_quickQAHistory.Count >= MAX_TURNS)
        {
            errorMessage = $"⚠️ 已达到 {MAX_TURNS} 轮上限，请点击'🗑️ 清空历史'或'❌ 清除文件'重新开始";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// 添加对话历史
    /// </summary>
    public void AddQuickQAHistory(string question, string answer)
    {
        _quickQAHistory.Add((question, answer));
        UpdateQuickQAUI();
    }

    /// <summary>
    /// 清空快速问答历史（由全局"清空历史"按钮调用）
    /// </summary>
    public void ClearQuickQAHistory()
    {
        _quickQAHistory.Clear();
        UpdateQuickQAUI();
        _ = AppendLineAsync("[快速问答] 对话历史已清空");
    }

    /// <summary>
    /// 清空快速问答聊天历史按钮点击事件
    /// </summary>
    private void BtnQuickQAClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _quickQAChatHistory.Clear();
        _quickQAHistory.Clear();
        UpdateQuickQAUI();
        _ = AppendLineAsync("[Document Assistant] Chat history cleared");
    }

    /// <summary>
    /// QuickQA输入框键盘事件（Ctrl+Enter发送）
    /// </summary>
    private void TxtQuickQAInput_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                // Ctrl+Enter: 发送消息
                e.Handled = true;
                BtnQuickQASend_Click(sender, new RoutedEventArgs());
            }
        }
    }

    /// <summary>
    /// QuickQA发送按钮点击事件
    /// </summary>
    private async void BtnQuickQASend_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var userInput = TxtQuickQAInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(userInput))
                return;

            // 检查是否可以继续对话
            if (!CanContinueQuickQA(out string errorMessage))
            {
                await AppendLineAsync($"[Document Assistant] {errorMessage}");
                return;
            }

            await EnsurePipeAsync();

            // 添加用户消息到历史
            var userMessage = new Models.ChatMessage
            {
                Role = "user",
                Content = userInput
            };
            _quickQAChatHistory.Add(userMessage);

            // 创建 AI 消息占位符（用于流式追加）
            var aiMessage = new Models.ChatMessage
            {
                Role = "assistant",
                Content = "",
                IsStreaming = true
            };
            _quickQAChatHistory.Add(aiMessage);

            // 设置 QuickQA 专用的流式消息
            _quickQAStreamingMessage = aiMessage;
            _quickQAScrollThrottle = 0;  // 重置滚动计数器

            // 滚动到底部
            if (_quickQAChatHistory.Count > 0)
            {
                ChatHistoryListQuickQA.ScrollIntoView(_quickQAChatHistory[_quickQAChatHistory.Count - 1]);
            }

            // 清空输入框
            TxtQuickQAInput.Text = "";

            // 构建QuickQA Prompt
            string promptToSend = BuildQuickQAPrompt(userInput);
            await AppendLineAsync($"[Document Assistant] Conversation turn: {_quickQAHistory.Count + 1}/{MAX_TURNS}");

            // 发送消息并处理流式响应
            await SendQuickQAMessageAsync(promptToSend, aiMessage, userInput);

        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Document Assistant] ❌ Error: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取QuickQA的系统提示词
    /// </summary>
    private string GetQuickQASystemPrompt()
    {
        string basePrompt = "You are a helpful document Q&A assistant. ";

        // 获取Answer Style设置
        if (CmbAnswerStyleQuickQA?.SelectedItem is ComboBoxItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();
            return basePrompt + (tag switch
            {
                "Simple" => "Use simple, easy-to-understand language. Avoid jargon. Explain like teaching a beginner.",
                "Professional" => "Use technical terminology and professional language. Assume expert-level knowledge. Be concise and precise.",
                _ => "Provide clear, accurate answers based on the document. Use appropriate technical terms with explanations when needed."
            });
        }

        return basePrompt + "Provide clear, accurate answers based on the document.";
    }

    /// <summary>
    /// 发送QuickQA消息到Worker并处理流式响应
    /// </summary>
    private async Task SendQuickQAMessageAsync(string prompt, Models.ChatMessage aiMessage, string userQuestion)
    {
        try
        {
            // 不再设置 _currentStreamingMessage，因为已经设置了 _quickQAStreamingMessage

            // 获取参数（从UI控件读取）
            float temperature = 0.7f;  // 默认值
            int maxTokens = 2048;      // 默认值

            // 读取Temperature设置
            if (CmbTemperatureQuickQA?.SelectedItem is ComboBoxItem tempItem)
            {
                if (float.TryParse(tempItem.Tag?.ToString(), out float temp))
                {
                    temperature = temp;
                }
            }

            // 读取MaxTokens设置
            if (CmbMaxTokensQuickQA?.SelectedItem is ComboBoxItem tokenItem)
            {
                if (int.TryParse(tokenItem.Tag?.ToString(), out int tokens))
                {
                    maxTokens = tokens;
                }
            }

            // 构建fullPrompt（单轮模式格式）
            string fullPrompt =
                $"<|start_of_role|>system<|end_of_role|>{GetQuickQASystemPrompt()}<|end_of_text|>" +
                $"<|start_of_role|>user<|end_of_role|>{prompt}<|end_of_text|>" +
                $"<|start_of_role|>assistant<|end_of_role|>";

            // 创建命令
            var cmd = new GraniteGenerateStreamCommand
            {
                prompt = fullPrompt,
                max_tokens = maxTokens,
                temperature = temperature
            };

            var json = System.Text.Json.JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";

            // 发送命令
            await SendJsonAsync(json);
            await AppendLineAsync($"[Document Assistant] Message sent (Temperature: {temperature}, Max tokens: {maxTokens})");

            // Note: 流式响应会通过管道读取循环自动处理，更新aiMessage.Content
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Document Assistant] ❌ Send error: {ex.Message}");
            if (_currentStreamingMessage != null)
            {
                _currentStreamingMessage.Content = $"❌ Error: {ex.Message}";
                _currentStreamingMessage.IsStreaming = false;
                _currentStreamingMessage = null;
            }
        }
    }

    /// <summary>
    /// 计算文本的 Token 数（通过 Worker 的 tokenizer）
    /// </summary>
    private async Task<int> CountTokensAsync(string text)
    {
        try
        {
            if (_pipe == null || !_pipe.IsConnected)
            {
                await AppendLineAsync("[快速问答] ⚠️ Worker 未连接");
                return -1;
            }

            // 创建 TaskCompletionSource
            var tcs = new TaskCompletionSource<int>();

            lock (_tokenCountLock)
            {
                _tokenCountTcs = tcs;
            }

            // 构建命令
            var command = new
            {
                type = "count_tokens",
                prompt = text
            };

            var json = JsonSerializer.Serialize(command) + "\n";
            var buffer = Encoding.UTF8.GetBytes(json);

            // 发送命令
            await _pipe.WriteAsync(buffer, 0, buffer.Length);
            await _pipe.FlushAsync();

            // 等待响应（最多 30 秒）
            var timeoutTask = Task.Delay(30000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                await AppendLineAsync("[快速问答] ⚠️ Token 计数超时");
                lock (_tokenCountLock)
                {
                    _tokenCountTcs = null;
                }
                return -1;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[快速问答] ❌ Token 计数失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 格式化文件大小
    /// </summary>
    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        else if (bytes < 1024 * 1024)
            return $"{bytes / 1024} KB";
        else
            return $"{bytes / (1024 * 1024)} MB";
    }
}
