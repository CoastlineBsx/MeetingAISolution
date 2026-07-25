using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using MeetingAI.Host.Models;

namespace MeetingAI.Host;

public partial class MainWindow
{
    // Helper method to get SDPage
    private Pages.SDPage? GetSDPage() => SDFrame?.Content as Pages.SDPage;

    // SD 相关字段
    private ObservableCollection<ChatMessage> _sdChatHistory = new();
    private bool _isSDMultiTurnMode = false;
    private string? _lastSDImagePath = null;
    private string? _lastSDPrompt = null;
    private int _lastSDSeed = -1;
    private ChatMessage? _currentSDGeneratingMessage = null;

    // SD 初始化
    private void InitializeSDPage()
    {
        // ItemsSource will be set when page is loaded
    }

    // 加载 SD 模型
    public async void BtnLoadSD_Click(object sender, RoutedEventArgs e)
    {
        var page = GetStartupPage();
        if (page == null) return;

        page.BtnLoadSD.IsEnabled = false;
        page.ProgressSD.IsActive = true;
        page.ProgressSD.Visibility = Visibility.Visible;

        try
        {
            // 确保管道连接
            await EnsurePipeAsync();

            // 获取选择的设备
            string device = page.CmbSDDevice.SelectedIndex switch
            {
                1 => "GPU",
                2 => "NPU",
                _ => "CPU"  // 默认 CPU
            };

            var command = new
            {
                type = "load_sd",
                device = device
            };

            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n"));
            await _pipe.FlushAsync();
            Debug.WriteLine($"[SD] 已发送加载命令: {device}");

            // 等待加载完成（通过监听 sd_ready 消息）
            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SD] 加载失败: {ex.Message}");
            await ShowErrorDialog("加载失败", ex.Message);
        }
        finally
        {
            page.ProgressSD.IsActive = false;
            page.ProgressSD.Visibility = Visibility.Collapsed;
            page.BtnLoadSD.IsEnabled = true;
        }
    }

    // 单轮/多轮模式切换
    public void BtnSDSingle_Click(object sender, RoutedEventArgs e)
    {
        var page = GetSDPage();
        if (page == null) return;

        _isSDMultiTurnMode = false;
        page.LblSDMode.Text = "[单轮模式]";
        page.BtnSDSingle.IsEnabled = false;
        page.BtnSDMulti.IsEnabled = true;
    }

    public void BtnSDMulti_Click(object sender, RoutedEventArgs e)
    {
        var page = GetSDPage();
        if (page == null) return;

        _isSDMultiTurnMode = true;
        page.LblSDMode.Text = "[多轮模式 - 迭代修改]";
        page.BtnSDSingle.IsEnabled = true;
        page.BtnSDMulti.IsEnabled = false;
    }

    // 清空历史
    public void BtnSDClear_Click(object sender, RoutedEventArgs e)
    {
        _sdChatHistory.Clear();
        _lastSDImagePath = null;
        _lastSDPrompt = null;
        _lastSDSeed = -1;
    }

    // 发送生成请求
    public async void BtnSDGenerate_Click(object sender, RoutedEventArgs e)
    {
        await GenerateSD();
    }

    // Ctrl+Enter 快捷键
    public void TxtSDInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if ((ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            {
                e.Handled = true;
                _ = GenerateSD();
            }
        }
    }

    private async Task GenerateSD()
    {
        var page = GetSDPage();
        if (page == null) return;

        string prompt = page.TxtSDInput.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return;
        }

        try
        {
            // 确保管道连接
            await EnsurePipeAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("错误", $"无法连接到 Worker: {ex.Message}");
            return;
        }

        // 添加用户消息
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTime.Now
        };
        _sdChatHistory.Add(userMsg);
        page.TxtSDInput.Text = "";

        // 创建 AI 生成中消息
        var aiMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "",
            IsGenerating = true,
            GenerationProgress = 0,
            Timestamp = DateTime.Now
        };
        _sdChatHistory.Add(aiMsg);
        _currentSDGeneratingMessage = aiMsg;

        // 滚动到底部
        if (page.SDChatList.Items.Count > 0)
        {
            page.SDChatList.ScrollIntoView(page.SDChatList.Items[^1]);
        }

        try
        {
            // 构建生成参数
            string mode = (_isSDMultiTurnMode && !string.IsNullOrEmpty(_lastSDImagePath)) ? "img2img" : "text2img";

            // 获取参数
            int width = 512, height = 512;
            if (page.CmbSDSize.SelectedIndex == 1) { width = 768; height = 768; }
            else if (page.CmbSDSize.SelectedIndex == 2) { width = 1024; height = 1024; }
            else if (page.CmbSDSize.SelectedIndex == 3) { width = 512; height = 768; }
            else if (page.CmbSDSize.SelectedIndex == 4) { width = 768; height = 512; }

            int steps = 20;
            if (page.CmbSDQuality.SelectedIndex == 0) steps = 15;
            else if (page.CmbSDQuality.SelectedIndex == 1) steps = 20;
            else if (page.CmbSDQuality.SelectedIndex == 2) steps = 30;

            float cfgScale = 7.5f;
            int seed = -1; // 随机

            string negativePrompt = page.TxtNegativePrompt.Text.Trim();
            if (string.IsNullOrEmpty(negativePrompt))
            {
                negativePrompt = "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, worst quality, low quality, normal quality, jpeg artifacts, signature, watermark, username, blurry";
            }

            // 应用风格预设
            string fullPrompt = ApplyStylePreset(prompt);

            var command = new
            {
                type = "sd_generate",
                mode = mode,
                prompt = fullPrompt,
                negative_prompt = negativePrompt,
                width = width,
                height = height,
                steps = steps,
                cfg_scale = cfgScale,
                seed = seed,
                input_image = mode == "img2img" ? _lastSDImagePath : null,
                strength = 0.75f
            };

            var startTime = DateTime.Now;
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n"));
            await _pipe.FlushAsync();
            
            Debug.WriteLine($"[SD] 生成请求已发送: {mode}, {width}x{height}, {steps} steps");

            // 等待完成将在 OnWorkerMessage 中处理
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SD] 生成失败: {ex.Message}");
            aiMsg.IsGenerating = false;
            aiMsg.Content = $"生成失败: {ex.Message}";
        }
    }

    // 应用风格预设
    private string ApplyStylePreset(string basePrompt)
    {
        var page = GetSDPage();
        if (page == null) return basePrompt;

        if (page.CmbSDStyle.SelectedIndex == 0)
        {
            // 默认
            return $"{basePrompt}, high quality, detailed";
        }
        else if (page.CmbSDStyle.SelectedIndex == 1)
        {
            // 写实
            return $"{basePrompt}, photorealistic, 8k, ultra detailed, professional photography";
        }
        else if (page.CmbSDStyle.SelectedIndex == 2)
        {
            // 卡通
            return $"{basePrompt}, cartoon style, cute, colorful, animated";
        }
        else if (page.CmbSDStyle.SelectedIndex == 3)
        {
            // 油画
            return $"{basePrompt}, oil painting style, artistic, brush strokes";
        }
        else if (page.CmbSDStyle.SelectedIndex == 4)
        {
            // 水彩
            return $"{basePrompt}, watercolor style, soft colors, artistic";
        }

        return basePrompt;
    }

    // 处理 Worker 的 SD 消息
    private void HandleSDMessage(string type, JsonElement root)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var page = GetSDPage();
                if (page == null) return;

                Debug.WriteLine($"[SD] 收到消息类型: {type}");
                switch (type)
                {
                    case "sd_ready":
                        // SD 模型加载完成
                        page.BtnSDGenerate.IsEnabled = true;
                        page.BtnSDSingle.IsEnabled = true;
                        page.BtnSDMulti.IsEnabled = true;
                        page.BtnSDClear.IsEnabled = true;
                        Debug.WriteLine("[SD] 模型加载完成");
                        break;

                    case "sd_progress":
                        // 进度更新
                        if (_currentSDGeneratingMessage != null)
                        {
                            int current = root.GetProperty("current").GetInt32();
                            int total = root.GetProperty("total").GetInt32();
                            int progress = (int)((float)current / total * 100);
                            
                            _currentSDGeneratingMessage.GenerationProgress = progress;

                            // 如果有预览图
                            if (root.TryGetProperty("preview", out var previewProp))
                            {
                                string previewPath = previewProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
                                {
                                    await LoadImageAsync(_currentSDGeneratingMessage, previewPath);
                                }
                            }
                        }
                        break;

                    case "sd_complete":
                        // 生成完成
                        Debug.WriteLine($"[SD] sd_complete 消息处理开始, _currentSDGeneratingMessage != null: {_currentSDGeneratingMessage != null}");
                        if (_currentSDGeneratingMessage != null)
                        {
                            string imagePath = root.GetProperty("image_path").GetString() ?? "";
                            Debug.WriteLine($"[SD] 图片路径: {imagePath}");

                            _currentSDGeneratingMessage.IsGenerating = false;
                            _currentSDGeneratingMessage.GenerationProgress = 100;

                            if (File.Exists(imagePath))
                            {
                                Debug.WriteLine($"[SD] 图片文件存在，开始加载");
                                await LoadImageAsync(_currentSDGeneratingMessage, imagePath);
                                _currentSDGeneratingMessage.GenerationInfo = "✅ Generated successfully";

                                // 保存上下文（用于多轮模式）
                                _lastSDImagePath = imagePath;
                                _lastSDPrompt = _currentSDGeneratingMessage.Content;

                                // 启用图片操作按钮
                                page.BtnSDSaveImage.IsEnabled = true;
                                page.BtnSDCopyImage.IsEnabled = true;
                                page.BtnSDRegenerate.IsEnabled = true;
                                Debug.WriteLine($"[SD] 已启用保存/复制/重新生成按钮");
                            }
                            else
                            {
                                Debug.WriteLine($"[SD] 图片文件不存在");
                                _currentSDGeneratingMessage.Content = "❌ 图片文件未找到";
                            }

                            _currentSDGeneratingMessage = null;

                            // 滚动到底部
                            if (page.SDChatList.Items.Count > 0)
                            {
                                page.SDChatList.ScrollIntoView(page.SDChatList.Items[^1]);
                            }
                            Debug.WriteLine($"[SD] sd_complete 消息处理完成");
                        }
                        else
                        {
                            Debug.WriteLine($"[SD] 警告：收到 sd_complete 但 _currentSDGeneratingMessage 为 null");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SD] 处理消息失败: {ex.Message}");
            }
        });
    }

    // 加载图片到消息
    private async Task LoadImageAsync(ChatMessage message, string imagePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            var attachment = new ImageAttachment
            {
                FilePath = imagePath,
                FileName = Path.GetFileName(imagePath),
                FileSize = (long)(await file.GetBasicPropertiesAsync()).Size,
                Preview = bitmap
            };

            message.Image = attachment;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SD] 加载图片失败: {ex.Message}");
        }
    }

    // 保存图片
    public async void BtnSaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSDImagePath) || !File.Exists(_lastSDImagePath))
        {
            Debug.WriteLine("[SD] 没有可保存的图片");
            return;
        }

        try
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
            picker.SuggestedFileName = $"sd_{DateTime.Now:yyyyMMdd_HHmmss}.png";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                File.Copy(_lastSDImagePath, file.Path, true);
                Debug.WriteLine($"[SD] 图片已保存: {file.Path}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SD] 保存图片失败: {ex.Message}");
            await ShowErrorDialog("保存失败", ex.Message);
        }
    }

    // 复制图片
    public async void BtnCopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSDImagePath) || !File.Exists(_lastSDImagePath))
        {
            Debug.WriteLine("[SD] 没有可复制的图片");
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_lastSDImagePath);
            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(dataPackage);

            Debug.WriteLine("[SD] 图片已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SD] 复制图片失败: {ex.Message}");
            await ShowErrorDialog("复制失败", ex.Message);
        }
    }

    // 重新生成
    public async void BtnRegenerate_Click(object sender, RoutedEventArgs e)
    {
        var page = GetSDPage();
        if (page == null) return;

        if (string.IsNullOrEmpty(_lastSDPrompt))
        {
            Debug.WriteLine("[SD] 没有可重新生成的提示词");
            return;
        }

        // 将上次的提示词填回输入框并重新生成
        page.TxtSDInput.Text = _lastSDPrompt;
        await GenerateSD();
    }

    // 显示错误对话框的辅助方法
    private async Task ShowErrorDialog(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
