using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private void InitializeStartup()
    {
        // 初始化（如果需要）
    }

    // ========== 加载/卸载 Whisper 模型 ==========
    private async void BtnLoadWhisper_Click(object sender, RoutedEventArgs e)
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
        try
        {
            await AppendLineAsync("[Startup] Loading Whisper model...");

            // 显示加载中状态
            BtnLoadWhisper.IsEnabled = false;
            ProgressWhisper.IsActive = true;
            ProgressWhisper.Visibility = Visibility.Visible;
            CmbWhisperDevice.IsEnabled = false;

            await EnsurePipeAsync();

            // 读取用户选择的设备
            string whisperDevice = CmbWhisperDevice.SelectedIndex switch
            {
                0 => "CPU",
                2 => "NPU",
                _ => "GPU"
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
            ProgressWhisper.IsActive = false;
            ProgressWhisper.Visibility = Visibility.Collapsed;
            BtnLoadWhisper.IsEnabled = true;
            CmbWhisperDevice.IsEnabled = true;
        }
    }

    private async Task UnloadWhisperModel()
    {
        try
        {
            await AppendLineAsync("[Startup] Unloading Whisper model...");

            // 显示卸载中状态
            BtnLoadWhisper.IsEnabled = false;
            ProgressWhisper.IsActive = true;
            ProgressWhisper.Visibility = Visibility.Visible;

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
            ProgressWhisper.IsActive = false;
            ProgressWhisper.Visibility = Visibility.Collapsed;
            BtnLoadWhisper.IsEnabled = true;
        }
    }

    // ========== 处理 Whisper 响应 ==========
    private void HandleWhisperLoadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressWhisper.IsActive = false;
            ProgressWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isWhisperLoaded = true;
                BtnLoadWhisper.Content = "Unload Model";
                BtnLoadWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ Whisper model loaded: {message}");
            }
            else
            {
                BtnLoadWhisper.IsEnabled = true;
                CmbWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ Whisper load failed: {message}");
            }
        });
    }

    private void HandleWhisperUnloadResponse(bool success, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressWhisper.IsActive = false;
            ProgressWhisper.Visibility = Visibility.Collapsed;

            if (success)
            {
                _isWhisperLoaded = false;
                BtnLoadWhisper.Content = "Load Model";
                BtnLoadWhisper.IsEnabled = true;
                CmbWhisperDevice.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✓ Whisper model unloaded");
            }
            else
            {
                BtnLoadWhisper.IsEnabled = true;
                _ = AppendLineAsync($"[Startup] ✗ Whisper unload failed: {message}");
            }
        });
    }
}
