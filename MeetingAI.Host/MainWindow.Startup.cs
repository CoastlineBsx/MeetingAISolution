using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // 辅助方法：获取 StartupPage 实例
    private Pages.StartupPage? GetStartupPage()
    {
        return StartupFrame?.Content as Pages.StartupPage;
    }

    private void InitializeStartup()
    {
        // 初始化（如果需要）
    }

    // ========== 加载/卸载 Whisper 模型 ==========
    public async void BtnLoadWhisper_Click(object sender, RoutedEventArgs e)
    {
        if (_isWhisperLoaded)
        {
            // 卸载模型
            await UnloadWhisperModel();
        }
        else
        {
            // 加载模型
            await LoadWhisperModel();
        }
    }

    private async Task LoadWhisperModel()
    {
        var page = GetStartupPage();
        if (page == null) return;

        try
        {
            await AppendLineAsync("[Startup] Loading Whisper model...");

            // 显示加载中状态
            page.BtnLoadWhisper.IsEnabled = false;
            page.ProgressWhisper.IsActive = true;
            page.ProgressWhisper.Visibility = Visibility.Visible;
            page.CmbWhisperDevice.IsEnabled = false;

            await EnsurePipeAsync();

            // 读取用户选择的设备
            string whisperDevice = page.CmbWhisperDevice.SelectedIndex switch
            {
                1 => "GPU",
                2 => "NPU",
                _ => "CPU"  // 默认 CPU
            };

            await AppendLineAsync($"[Startup] Whisper device: {whisperDevice}");

            // 发送加载 Whisper 命令
            var loadCmd = new
            {
                type = "load_whisper",
                device = whisperDevice
            };
            var json = JsonSerializer.Serialize(loadCmd) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[Startup] Whisper load command sent, waiting for Worker response...");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Startup] Whisper load failed: {ex.Message}");
            page.ProgressWhisper.IsActive = false;
            page.ProgressWhisper.Visibility = Visibility.Collapsed;
            page.BtnLoadWhisper.IsEnabled = true;
            page.CmbWhisperDevice.IsEnabled = true;
        }
    }

    private async Task UnloadWhisperModel()
    {
        var page = GetStartupPage();
        if (page == null) return;

        try
        {
            await AppendLineAsync("[Startup] Unloading Whisper model...");

            // 显示卸载中状态
            page.BtnLoadWhisper.IsEnabled = false;
            page.ProgressWhisper.IsActive = true;
            page.ProgressWhisper.Visibility = Visibility.Visible;

            await EnsurePipeAsync();

            // 发送卸载 Whisper 命令
            var unloadCmd = new
            {
                type = "unload_whisper"
            };
            var json = JsonSerializer.Serialize(unloadCmd) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[Startup] Whisper unload command sent");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Startup] Whisper unload failed: {ex.Message}");
            page.ProgressWhisper.IsActive = false;
            page.ProgressWhisper.Visibility = Visibility.Collapsed;
            page.BtnLoadWhisper.IsEnabled = true;
        }
    }

    // ========== 处理 Whisper 响应 ==========
    private void HandleWhisperLoadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var page = GetStartupPage();
            if (page == null) return;

            page.ProgressWhisper.IsActive = false;
            page.ProgressWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isWhisperLoaded = true;
                page.BtnLoadWhisper.Content = "Unload Model";
                page.BtnLoadWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ Whisper model loaded: {message}");
            }
            else
            {
                page.BtnLoadWhisper.IsEnabled = true;
                page.CmbWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ Whisper load failed: {message}");
            }
        });
    }

    private void HandleWhisperUnloadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var page = GetStartupPage();
            if (page == null) return;

            page.ProgressWhisper.IsActive = false;
            page.ProgressWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isWhisperLoaded = false;
                page.BtnLoadWhisper.Content = "Load Model";
                page.BtnLoadWhisper.IsEnabled = true;
                page.CmbWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ Whisper model unloaded");
            }
            else
            {
                page.BtnLoadWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ Whisper unload failed: {message}");
            }
        });
    }

    // ========== 加载/卸载 OpenVINO Whisper 模型 ==========
    public async void BtnLoadOpenVINOWhisper_Click(object sender, RoutedEventArgs e)
    {
        if (_isOpenVINOWhisperLoaded)
        {
            // 卸载模型
            await UnloadOpenVINOWhisperModel();
        }
        else
        {
            // 加载模型
            await LoadOpenVINOWhisperModel();
        }
    }

    private async Task LoadOpenVINOWhisperModel()
    {
        var page = GetStartupPage();
        if (page == null) return;

        try
        {
            await AppendLineAsync("[Startup] Loading OpenVINO Whisper model...");

            // 显示加载中状态
            page.BtnLoadOpenVINOWhisper.IsEnabled = false;
            page.ProgressOpenVINOWhisper.IsActive = true;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Visible;
            page.CmbOpenVINOWhisperDevice.IsEnabled = false;

            await EnsurePipeAsync();

            // 读取设备选择
            string device = page.CmbOpenVINOWhisperDevice.SelectedIndex switch
            {
                1 => "GPU",
                2 => "NPU",
                _ => "CPU"
            };

            await AppendLineAsync($"[Startup] OpenVINO Whisper device: {device}");

            // 发送加载 OpenVINO Whisper 命令
            // 不传 model_path：路径由 Worker 按自己的 models 目录解析。
            // Host 发相对路径的话会按 Worker 的 CWD（继承自 Host 的输出目录）去找，必然找不到。
            var loadCmd = new
            {
                type = "load_whisper_openvino",
                device = device
            };
            var json = JsonSerializer.Serialize(loadCmd) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[Startup] OpenVINO Whisper load command sent, waiting for Worker response...");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Startup] OpenVINO Whisper load failed: {ex.Message}");
            page.ProgressOpenVINOWhisper.IsActive = false;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Collapsed;
            page.BtnLoadOpenVINOWhisper.IsEnabled = true;
            page.CmbOpenVINOWhisperDevice.IsEnabled = true;
        }
    }

    private async Task UnloadOpenVINOWhisperModel()
    {
        var page = GetStartupPage();
        if (page == null) return;

        try
        {
            await AppendLineAsync("[Startup] Unloading OpenVINO Whisper model...");

            // 显示卸载中状态
            page.BtnLoadOpenVINOWhisper.IsEnabled = false;
            page.ProgressOpenVINOWhisper.IsActive = true;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Visible;

            await EnsurePipeAsync();

            // 发送卸载 OpenVINO Whisper 命令
            var unloadCmd = new
            {
                type = "unload_whisper_openvino"
            };
            var json = JsonSerializer.Serialize(unloadCmd) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[Startup] OpenVINO Whisper unload command sent");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Startup] OpenVINO Whisper unload failed: {ex.Message}");
            page.ProgressOpenVINOWhisper.IsActive = false;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Collapsed;
            page.BtnLoadOpenVINOWhisper.IsEnabled = true;
        }
    }

    // ========== 处理 OpenVINO Whisper 响应 ==========
    private void HandleOpenVINOWhisperLoadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var page = GetStartupPage();
            if (page == null) return;

            page.ProgressOpenVINOWhisper.IsActive = false;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isOpenVINOWhisperLoaded = true;
                page.BtnLoadOpenVINOWhisper.Content = "Unload OpenVINO Whisper";
                page.BtnLoadOpenVINOWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ OpenVINO Whisper model loaded: {message}");

                // 启用 ChatPage 的语音输入按钮
                var chatPage = GetChatPage();
                if (chatPage != null)
                {
                    chatPage.BtnVoiceInputChat.IsEnabled = true;
                }
            }
            else
            {
                page.BtnLoadOpenVINOWhisper.IsEnabled = true;
                page.CmbOpenVINOWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ OpenVINO Whisper load failed: {message}");
            }
        });
    }

    private void HandleOpenVINOWhisperUnloadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var page = GetStartupPage();
            if (page == null) return;

            page.ProgressOpenVINOWhisper.IsActive = false;
            page.ProgressOpenVINOWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isOpenVINOWhisperLoaded = false;
                page.BtnLoadOpenVINOWhisper.Content = "Load OpenVINO Whisper";
                page.BtnLoadOpenVINOWhisper.IsEnabled = true;
                page.CmbOpenVINOWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ OpenVINO Whisper model unloaded");

                // 禁用 ChatPage 的语音输入按钮
                var chatPage = GetChatPage();
                if (chatPage != null)
                {
                    chatPage.BtnVoiceInputChat.IsEnabled = false;
                }
            }
            else
            {
                page.BtnLoadOpenVINOWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ OpenVINO Whisper unload failed: {message}");
            }
        });
    }
}
