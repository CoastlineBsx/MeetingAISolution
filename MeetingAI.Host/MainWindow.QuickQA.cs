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
                LblQuickQADoc.Text = "未加载文档";
                BtnQuickQAClear.IsEnabled = false;
            }
            else
            {
                // 已加载文档
                string sizeStr = FormatFileSize(_quickQADocumentSize);
                int currentTurn = _quickQAHistory.Count;
                LblQuickQADoc.Text = $"文件: {_quickQADocumentName} ({sizeStr}, {_quickQATokenCount} tokens, {currentTurn}/{MAX_TURNS}轮)";
                BtnQuickQAClear.IsEnabled = true;
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
            throw new InvalidOperationException("未加载文档");
        }

        var sb = new StringBuilder();

        // 单轮或多轮的 Prompt 格式
        if (_quickQAHistory.Count == 0)
        {
            // 单轮模式（第一轮）
            sb.AppendLine("你是一个文档问答助手。请仔细阅读以下文档内容，然后回答用户的问题。");
            sb.AppendLine();
            sb.AppendLine("=== 文档开始 ===");
            sb.AppendLine(_quickQADocumentContent);
            sb.AppendLine("=== 文档结束 ===");
            sb.AppendLine();
            sb.AppendLine($"用户问题：{userQuestion}");
            sb.AppendLine();
            sb.AppendLine("请基于上述文档内容回答，如果文档中没有相关信息，请明确告知。");
        }
        else
        {
            // 多轮模式
            sb.AppendLine("你是一个文档问答助手。请仔细阅读以下文档内容，然后回答用户的问题。");
            sb.AppendLine();
            sb.AppendLine("=== 文档开始 ===");
            sb.AppendLine(_quickQADocumentContent);
            sb.AppendLine("=== 文档结束 ===");
            sb.AppendLine();
            sb.AppendLine("=== 对话历史 ===");
            foreach (var (q, a) in _quickQAHistory)
            {
                sb.AppendLine($"用户：{q}");
                sb.AppendLine($"助手：{a}");
            }
            sb.AppendLine("=== 历史结束 ===");
            sb.AppendLine();
            sb.AppendLine($"用户问题：{userQuestion}");
            sb.AppendLine();
            sb.AppendLine("请基于上述文档内容和对话历史回答，如果文档中没有相关信息，请明确告知。");
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
