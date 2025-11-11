using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MeetingAI.Host.Models;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;

namespace MeetingAI.Host;

/// <summary>
/// RAG Chat 模式 V2 - 简化版实现
/// 参考 QuickQA 的实现方式，完全独立运行
/// </summary>
public sealed partial class MainWindow : Window
{
    // ========== RAG Chat 2 私有字段 ==========
    private ObservableCollection<ChatMessage> _ragChat2History = new();
    private ChatMessage? _ragChat2StreamingMessage = null;
    private RAGService? _ragChat2Service = null;
    private SqliteVectorDatabase? _ragChat2VectorDb = null;
    private EmbeddingNPUService? _ragChat2EmbeddingService = null;
    private DocumentProcessor? _ragChat2DocumentProcessor = null;
    private bool _isRAGChat2Initialized = false;

    /// <summary>
    /// 初始化 RAG Chat 2 页面
    /// </summary>
    private void InitializeRAGChat2Page()
    {
        // 绑定聊天历史
        ChatHistoryRAGChat2.ItemsSource = _ragChat2History;

        // 更新 UI
        UpdateRAGChat2UI();
    }

    /// <summary>
    /// 上传文档按钮
    /// </summary>
    private async void BtnRAGChat2Upload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 确保 RAG 已初始化
            if (!_isRAGChat2Initialized)
            {
                await InitializeRAGChat2Async();
            }

            // 创建文件选择器
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".pdf");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            AddRAGChat2Message("system", $"Uploading: {file.Name}...");

            // 提取文档内容
            var extracted = await _ragChat2DocumentProcessor!.ExtractAsync(file.Path);
            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                AddRAGChat2Message("system", "Warning: Document is empty");
                return;
            }

            // 添加到 RAG 数据库
            var chunks = new List<(string Content, int PageNumber)>
            {
                (extracted.Content, 1)
            };

            await _ragChat2Service!.AddDocumentAsync(
                filename: extracted.FileName,
                filepath: file.Path,
                fileType: extracted.FileType,
                language: "zh",
                chunks: chunks
            );

            AddRAGChat2Message("system", $"Uploaded: {file.Name}");
            await UpdateRAGChat2DocCountAsync();
            UpdateRAGChat2UI();
        }
        catch (Exception ex)
        {
            AddRAGChat2Message("system", $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// 管理文档按钮
    /// </summary>
    private async void BtnRAGChat2Manage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isRAGChat2Initialized) return;

            var documents = await _ragChat2Service!.GetAllDocumentsAsync();
            if (documents.Count == 0)
            {
                await ShowSimpleDialog("No documents in knowledge base");
                return;
            }

            // 显示文档列表对话框
            var dialog = new ContentDialog
            {
                Title = "Document Management",
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot
            };

            var listView = new ListView
            {
                ItemsSource = documents,
                Height = 300,
                SelectionMode = ListViewSelectionMode.Single
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Total: {documents.Count} documents", Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(listView);

            var deleteBtn = new Button { Content = "Delete Selected", Margin = new Thickness(0, 12, 0, 0) };
            deleteBtn.Click += async (s, args) =>
            {
                if (listView.SelectedItem is DocumentInfo doc)
                {
                    await _ragChat2Service!.DeleteDocumentAsync(doc.DocId);
                    documents.Remove(doc);
                    AddRAGChat2Message("system", $"Deleted: {doc.Filename}");
                    await UpdateRAGChat2DocCountAsync();
                    if (documents.Count == 0) dialog.Hide();
                }
            };
            panel.Children.Add(deleteBtn);

            dialog.Content = panel;
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AddRAGChat2Message("system", $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// 清除聊天按钮
    /// </summary>
    private void BtnRAGChat2Clear_Click(object sender, RoutedEventArgs e)
    {
        _ragChat2History.Clear();
        UpdateRAGChat2UI();
    }

    /// <summary>
    /// 发送消息按钮
    /// </summary>
    private async void BtnRAGChat2Send_Click(object sender, RoutedEventArgs e)
    {
        await SendRAGChat2MessageAsync();
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    private async Task SendRAGChat2MessageAsync()
    {
        try
        {
            string userInput = TxtRAGChat2Input.Text.Trim();
            if (string.IsNullOrEmpty(userInput)) return;

            if (!_isRAGChat2Initialized)
            {
                AddRAGChat2Message("system", "Initializing RAG...");
                await InitializeRAGChat2Async();
            }

            TxtRAGChat2Input.Text = "";
            AddRAGChat2Message("user", userInput);

            // 添加 AI 消息（流式）
            var aiMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                Timestamp = DateTime.Now
            };
            _ragChat2History.Add(aiMsg);
            _ragChat2StreamingMessage = aiMsg;

            // 执行 RAG 搜索
            var ragContext = await _ragChat2Service!.RetrieveContextAsync(userInput);

            // 构建提示词
            string ragPrompt = BuildRAGChat2Prompt(userInput, ragContext.ContextText);

            // 调用 Granite 生成
            await EnsurePipeAsync();
            var cmd = new Contracts.Messages.GraniteGenerateStreamCommand
            {
                prompt = ragPrompt,
                max_tokens = 1024,
                temperature = 0.7f
            };

            var json = System.Text.Json.JsonSerializer.Serialize(cmd, Contracts.AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";
            await SendJsonAsync(json);

            UpdateRAGChat2UI();
        }
        catch (Exception ex)
        {
            AddRAGChat2Message("system", $"Error: {ex.Message}");
            if (_ragChat2StreamingMessage != null)
            {
                _ragChat2StreamingMessage = null;
            }
        }
    }

    /// <summary>
    /// 初始化 RAG 服务
    /// </summary>
    private async Task InitializeRAGChat2Async()
    {
        try
        {
            // 初始化向量数据库
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingAI");

            if (!Directory.Exists(appDataPath))
                Directory.CreateDirectory(appDataPath);

            var dbPath = Path.Combine(appDataPath, "meeting_rag.db");
            _ragChat2VectorDb = new SqliteVectorDatabase(dbPath);
            await _ragChat2VectorDb.InitializeAsync();

            // 初始化 Embedding 服务
            _ragChat2EmbeddingService = new EmbeddingNPUService(
                async (text, ct) => await GetEmbeddingViaPipeAsync(text, ct)
            );

            // 初始化 RAG 服务
            _ragChat2Service = new RAGService(
                _ragChat2VectorDb,
                _ragChat2EmbeddingService,
                topK: 2
            );

            // 初始化文档处理器
            var baseDir = AppContext.BaseDirectory;
            var tesseractDataPath = Path.Combine(baseDir, "tessdata");
            _ragChat2DocumentProcessor = new DocumentProcessor(tesseractDataPath);

            _isRAGChat2Initialized = true;
            AddRAGChat2Message("system", "RAG initialized successfully");
            await UpdateRAGChat2DocCountAsync();
            UpdateRAGChat2UI();
        }
        catch (Exception ex)
        {
            AddRAGChat2Message("system", $"Initialization failed: {ex.Message}");
            _isRAGChat2Initialized = false;
        }
    }

    /// <summary>
    /// 构建 RAG 提示词
    /// </summary>
    private string BuildRAGChat2Prompt(string userQuery, string contextText)
    {
        if (string.IsNullOrEmpty(contextText))
        {
            return $"<|start_of_role|>system<|end_of_role|>You are a helpful assistant.<|end_of_text|><|start_of_role|>user<|end_of_role|>{userQuery}<|end_of_text|><|start_of_role|>assistant<|end_of_role|>";
        }

        return $"<|start_of_role|>system<|end_of_role|>Answer based on the following context:\n{contextText}<|end_of_text|><|start_of_role|>user<|end_of_role|>{userQuery}<|end_of_text|><|start_of_role|>assistant<|end_of_role|>";
    }

    /// <summary>
    /// 添加消息到聊天历史
    /// </summary>
    private void AddRAGChat2Message(string role, string content)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ragChat2History.Add(new ChatMessage
            {
                Role = role,
                Content = content,
                Timestamp = DateTime.Now
            });
            ScrollRAGChat2ToBottom();
        });
    }

    /// <summary>
    /// 滚动到底部
    /// </summary>
    private void ScrollRAGChat2ToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ChatHistoryRAGChat2.Items.Count > 0)
            {
                ChatHistoryRAGChat2.ScrollIntoView(ChatHistoryRAGChat2.Items[^1]);
            }
        });
    }

    /// <summary>
    /// 更新文档计数
    /// </summary>
    private async Task UpdateRAGChat2DocCountAsync()
    {
        try
        {
            if (_ragChat2Service != null)
            {
                var documents = await _ragChat2Service.GetAllDocumentsAsync();
                var totalChunks = documents.Sum(d => d.TotalChunks);

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (documents.Count == 0)
                    {
                        LblRAGChat2Status.Text = "No documents - upload to start";
                    }
                    else
                    {
                        LblRAGChat2Status.Text = $"{documents.Count} document(s), {totalChunks} chunk(s)";
                    }
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// 更新 UI 状态
    /// </summary>
    private void UpdateRAGChat2UI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 上传按钮：Embedding 加载后可用
            BtnRAGChat2Upload.IsEnabled = _isGraniteEmbeddingLoaded;

            // 管理按钮：RAG 初始化后可用
            BtnRAGChat2Manage.IsEnabled = _isRAGChat2Initialized;

            // 发送按钮：RAG 初始化且 Granite 加载后可用
            BtnRAGChat2Send.IsEnabled = _isRAGChat2Initialized && _isGraniteLoaded;

            // 清除按钮：有聊天历史时可用
            BtnRAGChat2Clear.IsEnabled = _ragChat2History.Count > 0;
        });
    }

    /// <summary>
    /// 显示简单对话框
    /// </summary>
    private async Task ShowSimpleDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Info",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
