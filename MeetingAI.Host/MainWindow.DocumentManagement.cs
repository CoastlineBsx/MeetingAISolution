using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;
using DocumentInfo = MeetingAI.Host.RAG.VectorStore.DocumentInfo;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 初始化文档管理组件（在 RAG 初始化完成后调用）
    /// </summary>
    private void InitializeDocumentManagement()
    {
        try
        {
            // 获取 Tesseract 语言包路径
            var baseDir = AppContext.BaseDirectory;
            var tesseractDataPath = Path.Combine(baseDir, "tessdata");

            if (!Directory.Exists(tesseractDataPath))
            {
                _ = AppendLineAsync($"[文档管理] ⚠️ 警告：Tesseract 语言包目录不存在：{tesseractDataPath}");
                _ = AppendLineAsync($"[文档管理] OCR 功能将不可用");
            }

            // 初始化文档处理器和分块器
            _documentProcessor = new DocumentProcessor(tesseractDataPath);
            _documentChunker = new DocumentChunker(targetChunkSize: 500, maxChunkSize: 750, overlapSize: 100);

            // 初始化文档列表
            _documentList = new ObservableCollection<DocumentInfo>();
            DocumentListView.ItemsSource = _documentList;

            // 启用文档管理按钮
            BtnUploadDocument.IsEnabled = true;
            BtnRefreshDocuments.IsEnabled = true;
            BtnDeleteAllDocuments.IsEnabled = true;

            // 加载现有文档
            _ = LoadDocumentsAsync();

            _ = AppendLineAsync("[文档管理] ✅ 初始化完成");
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync($"[文档管理] ❌ 初始化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 上传文档按钮点击事件
    /// </summary>
    private async void BtnUploadDocument_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vectorDb == null || _embeddingService == null || _documentProcessor == null || _documentChunker == null)
            {
                await AppendLineAsync("[文档管理] ❌ 请先初始化 RAG 系统");
                return;
            }

            // 创建文件选择器
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".pdf");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");

            // 获取窗口句柄并初始化 picker
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            // 选择文件
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await AppendLineAsync($"[文档管理] 开始处理文档：{file.Name}");

            // 处理文档
            await ProcessAndStoreDocumentAsync(file.Path);
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 上传失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 处理并存储文档
    /// </summary>
    private async Task ProcessAndStoreDocumentAsync(string filePath)
    {
        try
        {
            if (_vectorDb == null || _embeddingService == null || _documentProcessor == null || _documentChunker == null)
            {
                throw new InvalidOperationException("文档管理服务未初始化");
            }

            // 1. 提取文档内容
            await AppendLineAsync($"[文档管理] 📄 提取文档内容...");
            var extracted = await _documentProcessor.ExtractAsync(filePath);

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                await AppendLineAsync($"[文档管理] ⚠️ 文档内容为空，跳过");
                return;
            }

            await AppendLineAsync($"[文档管理] ✅ 提取完成，内容长度：{extracted.Content.Length} 字符");

            // 2. 分块
            await AppendLineAsync($"[文档管理] ✂️ 分块处理...");
            var chunks = _documentChunker.ChunkDocument(extracted.Content, extracted.FileName);

            if (chunks.Count == 0)
            {
                await AppendLineAsync($"[文档管理] ⚠️ 分块失败，跳过");
                return;
            }

            await AppendLineAsync($"[文档管理] ✅ 分块完成，共 {chunks.Count} 块");

            // 3. 添加文档到数据库
            var docId = await _vectorDb.AddDocumentAsync(
                filename: extracted.FileName,
                filepath: filePath,
                fileType: extracted.FileType,
                language: "zh-CN",
                fileSize: extracted.FileSize,
                hasOcr: extracted.UsedOcr
            );

            await AppendLineAsync($"[文档管理] 📝 文档已添加，ID: {docId}");

            // 4. 生成 Embedding 并存储每个块
            await AppendLineAsync($"[文档管理] 🔢 生成 Embedding...");

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                // 生成 Embedding
                var embedding = await _embeddingService.GetEmbeddingAsync(chunk.Text);

                if (embedding == null || embedding.Length == 0)
                {
                    await AppendLineAsync($"[文档管理] ⚠️ 块 {i + 1}/{chunks.Count} Embedding 生成失败，跳过");
                    continue;
                }

                // 存储到数据库
                await _vectorDb.AddChunkAsync(
                    docId: docId,
                    chunkIndex: chunk.ChunkIndex,
                    pageNumber: 0,
                    content: chunk.Text,
                    embedding: embedding
                );

                // 每 5 块输出一次进度
                if ((i + 1) % 5 == 0 || i == chunks.Count - 1)
                {
                    await AppendLineAsync($"[文档管理] 进度：{i + 1}/{chunks.Count}");
                }
            }

            // 5. 更新文档块数统计
            await _vectorDb.UpdateDocumentChunkCountAsync(docId, chunks.Count);

            await AppendLineAsync($"[文档管理] ✅ 文档 '{extracted.FileName}' 上传成功！");
            await AppendLineAsync($"[文档管理] 📊 共 {chunks.Count} 块，使用 OCR: {(extracted.UsedOcr ? "是" : "否")}");

            // 6. 刷新文档列表
            await LoadDocumentsAsync();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 处理失败：{ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 刷新文档列表按钮点击事件
    /// </summary>
    private async void BtnRefreshDocuments_Click(object sender, RoutedEventArgs e)
    {
        await LoadDocumentsAsync();
    }

    /// <summary>
    /// 加载文档列表
    /// </summary>
    private async Task LoadDocumentsAsync()
    {
        try
        {
            if (_vectorDb == null || _documentList == null)
            {
                return;
            }

            await AppendLineAsync("[文档管理] 🔄 刷新文档列表...");

            // 从数据库加载文档列表
            var documents = await _vectorDb.GetAllDocumentsAsync();

            // 更新 UI
            DispatcherQueue.TryEnqueue(() =>
            {
                _documentList.Clear();
                foreach (var doc in documents)
                {
                    _documentList.Add(doc);
                }
            });

            // 更新统计信息
            await UpdateDocumentStatsAsync();

            await AppendLineAsync($"[文档管理] ✅ 文档列表已刷新，共 {documents.Count} 个文档");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 刷新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新文档统计信息
    /// </summary>
    private async Task UpdateDocumentStatsAsync()
    {
        try
        {
            if (_vectorDb == null)
            {
                return;
            }

            var stats = await _vectorDb.GetDocumentStatsAsync();

            DispatcherQueue.TryEnqueue(() =>
            {
                LblDocumentStats.Text = stats.DisplayText;
            });
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 统计更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除全部文档按钮点击事件
    /// </summary>
    private async void BtnDeleteAllDocuments_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vectorDb == null)
            {
                return;
            }

            // 创建确认对话框
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = "确定要删除所有文档吗？此操作无法撤销！",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await AppendLineAsync("[文档管理] 🗑️ 删除所有文档...");

                await _vectorDb.DeleteAllDocumentsAsync();

                await AppendLineAsync("[文档管理] ✅ 所有文档已删除");

                // 刷新列表
                await LoadDocumentsAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 删除失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除单个文档按钮点击事件
    /// </summary>
    private async void BtnDeleteDocument_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vectorDb == null)
            {
                return;
            }

            if (sender is not Button button || button.Tag is not long docId)
            {
                return;
            }

            // 找到对应的文档信息
            var doc = _documentList?.FirstOrDefault(d => d.DocId == docId);
            if (doc == null)
            {
                return;
            }

            // 创建确认对话框
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = $"确定要删除文档 '{doc.Filename}' 吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await AppendLineAsync($"[文档管理] 🗑️ 删除文档：{doc.Filename}");

                await _vectorDb.DeleteDocumentAsync(docId);

                await AppendLineAsync($"[文档管理] ✅ 文档已删除");

                // 刷新列表
                await LoadDocumentsAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[文档管理] ❌ 删除失败：{ex.Message}");
        }
    }
}
