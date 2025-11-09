using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // LLaVA 独立的对话历史
    private ObservableCollection<ChatMessage> _llavaChatHistory = new();

    // LLaVA 模式：single 或 multi
    private string _llavaMode = "single";

    // 当前上传的图片路径
    private string? _currentImagePath = null;

    // 支持的图片格式
    private readonly string[] _supportedImageFormats = { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif" };

    // 图片大小限制：15MB
    private const long MAX_IMAGE_SIZE = 15 * 1024 * 1024;

    // LLaVA 模型是否已加载
    private bool _isLLaVALoaded = false;

    private void InitializeLLaVA()
    {
        // 初始化（如果需要）
    }

    // ========== 加载 LLaVA 模型 ==========
    private async void BtnLoadLLaVA_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppendLineAsync("[LLaVA] 开始加载 LLaVA 模型...");

            // 禁用加载按钮，防止重复点击
            BtnLoadLLaVA.IsEnabled = false;
            LblLLaVAStatus.Text = "⏳ 正在加载模型...";

            await EnsurePipeAsync();

            // 读取用户选择的设备
            string llaVADevice = CmbLLaVADevice.SelectedIndex switch
            {
                0 => "CPU",
                2 => "NPU",
                _ => "GPU"
            };

            // 发送加载 LLaVA 命令
            var loadCmd = new
            {
                type = "load_llava",
                device = llaVADevice
            };
            var json = JsonSerializer.Serialize(loadCmd) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[LLaVA] 已发送加载命令，等待 Worker 响应...");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 加载失败：{ex.Message}");
            LblLLaVAStatus.Text = "❌ 加载失败";
            BtnLoadLLaVA.IsEnabled = true;
        }
    }

    // ========== 图片上传处理 ==========
    private async void BtnUploadImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };

            // 添加支持的图片格式
            foreach (var format in _supportedImageFormats)
            {
                picker.FileTypeFilter.Add(format);
            }

            // 获取窗口句柄（WinUI3 必需）
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await LoadImageAsync(file);
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 上传图片失败：{ex.Message}");
        }
    }

    private async Task LoadImageAsync(StorageFile file)
    {
        try
        {
            // 检查文件大小
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > MAX_IMAGE_SIZE)
            {
                await AppendLineAsync($"[LLaVA] 图片过大（{properties.Size / 1024 / 1024:F2} MB），最大支持 15 MB");
                return;
            }

            // 检查文件格式
            string ext = Path.GetExtension(file.Path).ToLower();
            if (!_supportedImageFormats.Contains(ext))
            {
                await AppendLineAsync($"[LLaVA] 不支持的图片格式：{ext}");
                return;
            }

            _currentImagePath = file.Path;

            // 更新UI预览
            DispatcherQueue.TryEnqueue(() =>
            {
                ImgPreview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path));
                ImgPlaceholder.Visibility = Visibility.Collapsed;
                LblImageStatus.Text = $"✅ {file.Name} ({properties.Size / 1024:F0} KB)";
                LblImageStatus.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green);

                // 启用删除按钮
                BtnDeleteImage.IsEnabled = true;
            });

            await AppendLineAsync($"[LLaVA] 图片已加载: {file.Name}");

            // 如果是多轮模式，重新编码图片
            if (_llavaMode == "multi")
            {
                await StartLLaVAChatAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 加载图片失败：{ex.Message}");
        }
    }

    // ========== 删除图片 ==========
    private async void BtnDeleteImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 如果在多轮模式，先结束会话
            if (_llavaMode == "multi")
            {
                await EnsurePipeAsync();
                var finishCmd = new { type = "llava_finish_chat" };
                var json = JsonSerializer.Serialize(finishCmd) + "\n";
                await SendJsonAsync(json);

                // 切回单轮模式
                _llavaMode = "single";
                LblLLaVAMode.Text = "[单轮模式] 每次重新编码图片";
                BtnLLaVASingle.IsEnabled = false;
                BtnLLaVAMulti.IsEnabled = true;
            }

            // 清空图片相关数据
            _currentImagePath = null;

            // 更新UI
            DispatcherQueue.TryEnqueue(() =>
            {
                ImgPreview.Source = null;
                ImgPlaceholder.Visibility = Visibility.Visible;
                LblImageStatus.Text = "未加载图片";
                LblImageStatus.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);

                // 禁用删除按钮和模式切换按钮
                BtnDeleteImage.IsEnabled = false;
                BtnLLaVASingle.IsEnabled = false;
                BtnLLaVAMulti.IsEnabled = false;
            });

            await AppendLineAsync("[LLaVA] ✅ 图片已删除");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 删除图片失败：{ex.Message}");
        }
    }

    // ========== 拖拽上传 ==========
    private void ImgPreview_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void ImgPreview_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                {
                    await LoadImageAsync(file);
                }
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 拖拽上传失败：{ex.Message}");
        }
    }

    // ========== 模式切换 ==========
    private async void BtnLLaVASingle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            // 如果当前是多轮模式，先结束会话
            if (_llavaMode == "multi")
            {
                var finishCmd = new { type = "llava_finish_chat" };
                var json = JsonSerializer.Serialize(finishCmd) + "\n";
                await SendJsonAsync(json);
            }

            _llavaMode = "single";
            LblLLaVAMode.Text = "[单轮模式] 每次重新编码图片";
            BtnLLaVASingle.IsEnabled = false;
            BtnLLaVAMulti.IsEnabled = true;

            await AppendLineAsync("[LLaVA] 已切换到单轮模式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 切换模式失败：{ex.Message}");
        }
    }

    private async void BtnLLaVAMulti_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            if (string.IsNullOrEmpty(_currentImagePath))
            {
                await AppendLineAsync("[LLaVA] 请先上传图片");
                return;
            }

            await StartLLaVAChatAsync();

            _llavaMode = "multi";
            LblLLaVAMode.Text = "[多轮模式] 复用缓存图片特征";
            BtnLLaVASingle.IsEnabled = true;
            BtnLLaVAMulti.IsEnabled = false;

            await AppendLineAsync("[LLaVA] 已切换到多轮模式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 切换模式失败：{ex.Message}");
        }
    }

    private async Task StartLLaVAChatAsync()
    {
        var startCmd = new
        {
            type = "llava_start_chat",
            image_path = _currentImagePath
        };
        var json = JsonSerializer.Serialize(startCmd) + "\n";
        await SendJsonAsync(json);
        await AppendLineAsync("[LLaVA] 图片编码中...");
    }

    // ========== 清空历史 ==========
    private async void BtnLLaVAClear_Click(object sender, RoutedEventArgs e)
    {
        _llavaChatHistory.Clear();

        // 如果是多轮模式，需要重启会话
        if (_llavaMode == "multi" && !string.IsNullOrEmpty(_currentImagePath))
        {
            try
            {
                var finishCmd = new { type = "llava_finish_chat" };
                var json1 = JsonSerializer.Serialize(finishCmd) + "\n";
                await SendJsonAsync(json1);

                await StartLLaVAChatAsync();

                await AppendLineAsync("[LLaVA] 多轮会话已重置");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[LLaVA] 重置会话失败：{ex.Message}");
            }
        }
    }

    // ========== 发送消息 ==========
    private async void BtnLLaVASend_Click(object sender, RoutedEventArgs e)
    {
        await SendLLaVAMessageAsync();
    }

    private async Task SendLLaVAMessageAsync()
    {
        try
        {
            var userInput = TxtLLaVAInput.Text.Trim();
            if (string.IsNullOrEmpty(userInput)) return;

            if (string.IsNullOrEmpty(_currentImagePath))
            {
                await AppendLineAsync("[LLaVA] 请先上传图片");
                return;
            }

            await EnsurePipeAsync();

            // 添加用户消息到历史
            var userMessage = new ChatMessage
            {
                Role = "user",
                Content = userInput
            };
            _llavaChatHistory.Add(userMessage);

            // 创建 AI 消息占位符（用于流式追加）
            _currentStreamingMessage = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                IsStreaming = true
            };
            _llavaChatHistory.Add(_currentStreamingMessage);

            // 重置滚动计数器
            _scrollThrottleCounter = 0;

            // 自动滚动到底部
            if (LLaVAChatList.Items.Count > 0)
            {
                LLaVAChatList.ScrollIntoView(LLaVAChatList.Items[^1]);
            }

            // 清空输入框
            TxtLLaVAInput.Text = "";

            // 获取参数
            string maxTokensStr = ((ComboBoxItem)CmbLLaVAMaxTokens.SelectedItem).Content.ToString()!;
            string temperatureStr = ((ComboBoxItem)CmbLLaVATemperature.SelectedItem).Content.ToString()!;

            int maxTokens = int.Parse(maxTokensStr.Split(' ')[0]);
            float temperature = float.Parse(temperatureStr.Split(' ')[0]);

            // 根据模式发送命令
            string json;
            if (_llavaMode == "single")
            {
                // 单轮模式
                var cmd = new
                {
                    type = "llava_generate",
                    image_path = _currentImagePath,
                    prompt = userInput,
                    max_tokens = maxTokens,
                    temperature = temperature
                };
                json = JsonSerializer.Serialize(cmd) + "\n";
            }
            else
            {
                // 多轮模式
                var cmd = new
                {
                    type = "llava_chat_stream",
                    prompt = userInput,
                    max_tokens = maxTokens,
                    temperature = temperature
                };
                json = JsonSerializer.Serialize(cmd) + "\n";
            }

            await SendJsonAsync(json);
            await AppendLineAsync($"[LLaVA] 已发送 ({_llavaMode})：{userInput}");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[LLaVA] 发送失败：{ex.Message}");
            if (_currentStreamingMessage != null)
            {
                _currentStreamingMessage.Content = $"❌ 错误：{ex.Message}";
                _currentStreamingMessage.IsStreaming = false;
                _currentStreamingMessage = null;
            }
        }
    }

    // ========== 处理流式响应 ==========
    private Task HandleLLaVAStreamToken(string token)
    {
        if (_currentStreamingMessage == null)
            return Task.CompletedTask;

        // 投递到 UI 线程
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _currentStreamingMessage.Content += token;

            // 滚动节流：每5个token滚动一次
            _scrollThrottleCounter++;
            if (_scrollThrottleCounter >= 5)
            {
                _scrollThrottleCounter = 0;
                ScrollToBottomLLaVA();
            }
        });

        return Task.CompletedTask;
    }

    private void HandleLLaVAStreamDone()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentStreamingMessage != null)
            {
                _currentStreamingMessage.IsStreaming = false;
                _currentStreamingMessage = null;
            }

            // 确保最后一次滚动到底部
            _scrollThrottleCounter = 0;
            ScrollToBottomLLaVA();
        });
    }

    // ========== 滚动到底部 ==========
    private ScrollViewer? _llavaScrollViewer = null;

    private void ScrollToBottomLLaVA()
    {
        if (_llavaScrollViewer == null && LLaVAChatList != null)
        {
            _llavaScrollViewer = FindScrollViewer(LLaVAChatList);
        }

        if (_llavaScrollViewer != null)
        {
            _llavaScrollViewer.ChangeView(null, _llavaScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
    }
}
