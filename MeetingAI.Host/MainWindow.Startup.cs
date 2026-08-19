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

    public Task RefreshModelStatusAsync()
    {
        if (_worker is null || _worker.HasExited)
        {
            return Task.CompletedTask;
        }
        return SendManagedModelCommandAsync(
            new { type = "get_model_status" });
    }

    public string GetStreamingModelReadinessError(
        string translationMode,
        bool summaryEnabled)
    {
        var missing = new System.Collections.Generic.List<string>();
        if (!_isSherpaLoaded)
        {
            missing.Add("Sherpa-ONNX 实时转录");
        }
        if (!_isOpenVINOWhisperLoaded)
        {
            missing.Add("OpenVINO Whisper（会后最终稿）");
        }
        if (summaryEnabled && !_isGraniteLoaded)
        {
            missing.Add("Granite（会议摘要）");
        }
        if (translationMode is "auto" or "to_zh")
        {
            if (!_isTranslationEnZhLoaded)
            {
                missing.Add("OPUS-MT 英→中");
            }
        }
        if (translationMode is "auto" or "to_en")
        {
            if (!_isTranslationZhEnLoaded)
            {
                missing.Add("OPUS-MT 中→英");
            }
        }
        return missing.Count == 0
            ? ""
            : "请先到 Startup 加载以下模型：\n• " +
              string.Join("\n• ", missing);
    }

    public async void BtnLoadGranite_Click(
        object sender,
        RoutedEventArgs e)
    {
        var page = GetStartupPage();
        if (page == null) return;
        SetManagedModelBusy("granite", true);
        await SendManagedModelCommandAsync(
            _isGraniteLoaded
                ? new { type = "unload_granite", device = "" }
                : new
                {
                    type = "load_granite",
                    device = SelectedDevice(page.CmbGraniteDevice)
                });
    }

    public async void BtnLoadEmbedding_Click(
        object sender,
        RoutedEventArgs e)
    {
        var page = GetStartupPage();
        if (page == null) return;
        SetManagedModelBusy("embedding", true);
        await SendManagedModelCommandAsync(
            _isEmbeddingLoaded
                ? new { type = "unload_embedding", device = "" }
                : new
                {
                    type = "load_embedding",
                    device = SelectedDevice(page.CmbEmbeddingDevice)
                });
    }

    public async void BtnLoadSherpa_Click(
        object sender,
        RoutedEventArgs e)
    {
        SetManagedModelBusy("sherpa", true);
        await SendManagedModelCommandAsync(
            new
            {
                type = _isSherpaLoaded
                    ? "unload_sherpa"
                    : "load_sherpa"
            });
    }

    public async void BtnLoadPunctuator_Click(
        object sender,
        RoutedEventArgs e)
    {
        SetManagedModelBusy("punctuator", true);
        await SendManagedModelCommandAsync(
            new
            {
                type = _isPunctuatorLoaded
                    ? "unload_punctuator"
                    : "load_punctuator"
            });
    }

    public async void BtnLoadTranslationEnZh_Click(
        object sender,
        RoutedEventArgs e)
    {
        SetManagedModelBusy("translation_en_zh", true);
        await SendManagedModelCommandAsync(
            new
            {
                type = _isTranslationEnZhLoaded
                    ? "unload_translation"
                    : "load_translation",
                direction = "en_zh"
            });
    }

    public async void BtnLoadTranslationZhEn_Click(
        object sender,
        RoutedEventArgs e)
    {
        SetManagedModelBusy("translation_zh_en", true);
        await SendManagedModelCommandAsync(
            new
            {
                type = _isTranslationZhEnLoaded
                    ? "unload_translation"
                    : "load_translation",
                direction = "zh_en"
            });
    }

    private async Task SendManagedModelCommandAsync(object command)
    {
        try
        {
            await EnsurePipeAsync();
            await SendJsonAsync(
                JsonSerializer.Serialize(command) + "\n");
        }
        catch (Exception ex)
        {
            await AppendLineAsync(
                $"[Startup] 模型命令发送失败: {ex.Message}");
            SetManagedModelBusy("granite", false);
            SetManagedModelBusy("embedding", false);
            SetManagedModelBusy("sherpa", false);
            SetManagedModelBusy("punctuator", false);
            SetManagedModelBusy("translation_en_zh", false);
            SetManagedModelBusy("translation_zh_en", false);
        }
    }

    private static string SelectedDevice(
        Microsoft.UI.Xaml.Controls.ComboBox comboBox)
        => comboBox.SelectedIndex switch
        {
            1 => "GPU",
            2 => "NPU",
            _ => "CPU"
        };

    private void HandleModelStateMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var model = root.GetProperty("model").GetString() ?? "";
            bool loaded =
                root.TryGetProperty("loaded", out var loadedValue) &&
                loadedValue.ValueKind == JsonValueKind.True;
            string device =
                root.TryGetProperty("device", out var deviceValue)
                    ? deviceValue.GetString() ?? ""
                    : "";
            string message =
                root.TryGetProperty("message", out var messageValue)
                    ? messageValue.GetString() ?? ""
                    : "";
            DispatcherQueue.TryEnqueue(
                () => ApplyManagedModelState(
                    model,
                    loaded,
                    device,
                    message));
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync(
                $"[Startup] 无法解析模型状态: {ex.Message}");
        }
    }

    private void HandleModelStatusMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            bool Loaded(string name) =>
                root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.True;

            // DispatcherQueue 中的回调会在本方法返回后才执行；此时
            // JsonDocument 已经 Dispose。必须先把所有状态复制成普通
            // bool，不能让 UI 回调继续捕获 root/JsonElement。
            bool graniteLoaded = Loaded("granite");
            bool embeddingLoaded = Loaded("embedding");
            bool openVinoWhisperLoaded = Loaded("openvino_whisper");
            bool sherpaLoaded = Loaded("sherpa");
            bool punctuatorLoaded = Loaded("punctuator");
            bool translationEnZhLoaded = Loaded("translation_en_zh");
            bool translationZhEnLoaded = Loaded("translation_zh_en");
            bool llavaLoaded = Loaded("llava");
            bool stableDiffusionLoaded = Loaded("stable_diffusion");

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyManagedModelState(
                    "granite", graniteLoaded, "", "");
                ApplyManagedModelState(
                    "embedding", embeddingLoaded, "", "");
                ApplyManagedModelState(
                    "openvino_whisper",
                    openVinoWhisperLoaded, "", "");
                ApplyManagedModelState(
                    "sherpa", sherpaLoaded, "CPU", "");
                ApplyManagedModelState(
                    "punctuator", punctuatorLoaded, "CPU", "");
                ApplyManagedModelState(
                    "translation_en_zh",
                    translationEnZhLoaded, "CPU", "");
                ApplyManagedModelState(
                    "translation_zh_en",
                    translationZhEnLoaded, "CPU", "");
                ApplyManagedModelState(
                    "llava", llavaLoaded, "", "");
                ApplyManagedModelState(
                    "stable_diffusion",
                    stableDiffusionLoaded, "", "");
            });
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync(
                $"[Startup] 无法解析模型快照: {ex.Message}");
        }
    }

    private void ApplyManagedModelState(
        string model,
        bool loaded,
        string device,
        string message)
    {
        var page = GetStartupPage();
        if (page == null) return;
        string readyText = string.IsNullOrWhiteSpace(device)
            ? "Ready"
            : $"Ready · {device}";
        string stateText = loaded
            ? readyText
            : "Not loaded";
        if (!string.IsNullOrWhiteSpace(message))
        {
            stateText += $" · {message}";
        }

        switch (model)
        {
            case "granite":
                _isGraniteLoaded = loaded;
                page.BtnLoadGranite.Content =
                    loaded ? "Unload Granite" : "Load Granite";
                page.BtnLoadGranite.IsEnabled = true;
                page.CmbGraniteDevice.IsEnabled = !loaded;
                page.TxtGraniteState.Text = stateText;
                SetManagedModelBusy(model, false);
                break;
            case "embedding":
                _isEmbeddingLoaded = loaded;
                page.BtnLoadEmbedding.Content =
                    loaded ? "Unload Embedding" : "Load Embedding";
                page.BtnLoadEmbedding.IsEnabled = true;
                page.CmbEmbeddingDevice.IsEnabled = !loaded;
                page.TxtEmbeddingState.Text =
                    stateText +
                    " · OpenVINO GenAI TextEmbeddingPipeline";
                SetManagedModelBusy(model, false);
                break;
            case "openvino_whisper":
                _isOpenVINOWhisperLoaded = loaded;
                page.BtnLoadOpenVINOWhisper.Content =
                    loaded
                        ? "Unload OpenVINO Whisper"
                        : "Load OpenVINO Whisper";
                page.BtnLoadOpenVINOWhisper.IsEnabled = true;
                page.CmbOpenVINOWhisperDevice.IsEnabled = !loaded;
                var chatPage = GetChatPage();
                if (chatPage != null)
                {
                    chatPage.BtnVoiceInputChat.IsEnabled = loaded;
                }
                break;
            case "sherpa":
                _isSherpaLoaded = loaded;
                page.BtnLoadSherpa.Content =
                    loaded ? "Unload Sherpa" : "Load Sherpa";
                page.BtnLoadSherpa.IsEnabled = true;
                page.TxtSherpaState.Text = stateText;
                SetManagedModelBusy(model, false);
                break;
            case "punctuator":
                _isPunctuatorLoaded = loaded;
                page.BtnLoadPunctuator.Content =
                    loaded
                        ? "Unload Punctuation"
                        : "Load Punctuation";
                page.BtnLoadPunctuator.IsEnabled = true;
                page.TxtPunctuatorState.Text = stateText;
                SetManagedModelBusy(model, false);
                break;
            case "translation_en_zh":
                _isTranslationEnZhLoaded = loaded;
                page.BtnLoadTranslationEnZh.Content =
                    loaded ? "Unload EN → ZH" : "Load EN → ZH";
                page.BtnLoadTranslationEnZh.IsEnabled = true;
                page.TxtTranslationEnZhState.Text =
                    stateText + " · CTranslate2";
                SetManagedModelBusy(model, false);
                break;
            case "translation_zh_en":
                _isTranslationZhEnLoaded = loaded;
                page.BtnLoadTranslationZhEn.Content =
                    loaded ? "Unload ZH → EN" : "Load ZH → EN";
                page.BtnLoadTranslationZhEn.IsEnabled = true;
                page.TxtTranslationZhEnState.Text =
                    stateText + " · CTranslate2";
                SetManagedModelBusy(model, false);
                break;
            case "stable_diffusion":
                _isSDLoaded = loaded;
                page.BtnLoadSD.Content =
                    loaded ? "Unload Model" : "Load Model";
                page.BtnLoadSD.IsEnabled = true;
                page.CmbSDDevice.IsEnabled = !loaded;
                page.ProgressSD.IsActive = false;
                page.ProgressSD.Visibility = Visibility.Collapsed;
                break;
            case "llava":
                _isLLaVALoaded = loaded;
                page.BtnLoadLLaVA.Content =
                    loaded ? "Unload Model" : "Load Model";
                page.BtnLoadLLaVA.IsEnabled = true;
                page.CmbLLaVADevice.IsEnabled = !loaded;
                page.ProgressLLaVA.IsActive = false;
                page.ProgressLLaVA.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void SetManagedModelBusy(string model, bool busy)
    {
        var page = GetStartupPage();
        if (page == null) return;
        var visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
        switch (model)
        {
            case "granite":
                page.BtnLoadGranite.IsEnabled = !busy;
                page.ProgressGranite.IsActive = busy;
                page.ProgressGranite.Visibility = visibility;
                break;
            case "embedding":
                page.BtnLoadEmbedding.IsEnabled = !busy;
                page.ProgressEmbedding.IsActive = busy;
                page.ProgressEmbedding.Visibility = visibility;
                break;
            case "sherpa":
                page.BtnLoadSherpa.IsEnabled = !busy;
                page.ProgressSherpa.IsActive = busy;
                page.ProgressSherpa.Visibility = visibility;
                break;
            case "punctuator":
                page.BtnLoadPunctuator.IsEnabled = !busy;
                page.ProgressPunctuator.IsActive = busy;
                page.ProgressPunctuator.Visibility = visibility;
                break;
            case "translation_en_zh":
                page.BtnLoadTranslationEnZh.IsEnabled = !busy;
                page.ProgressTranslationEnZh.IsActive = busy;
                page.ProgressTranslationEnZh.Visibility = visibility;
                break;
            case "translation_zh_en":
                page.BtnLoadTranslationZhEn.IsEnabled = !busy;
                page.ProgressTranslationZhEn.IsActive = busy;
                page.ProgressTranslationZhEn.Visibility = visibility;
                break;
        }
    }
}
