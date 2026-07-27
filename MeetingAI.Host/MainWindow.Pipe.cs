using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // 与 Worker 端 main.cpp 的退出码约定保持一致
    private const int ExitCodeWorkerAlreadyRunning = 2;

    // 管道是单条字节流，多个并发 WriteAsync 会把 JSON 交错写坏。
    // 流式转录每秒会发多条音频包，必须串行化。
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);
    private readonly SemaphoreSlim _pipeConnectLock = new(1, 1);

    private void StopWorkerOnExit()
    {
        try
        {
            try { if (_loopback is not null) _loopback.StopRecording(); } catch { }
            _isLoopback = false;
            _loopbackWriter = null;
            _loopback = null;

            try { if (_microphone is not null) _microphone.StopRecording(); } catch { }
            _isMicrophone = false;
            _microphoneWriter = null;
            _microphone = null;

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            if (_worker is { HasExited: false })
            {
                _worker.Kill();
            }
            _worker = null;

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch
        {
        }
    }

    private string? FindWorkerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "MeetingAI.Worker.exe"),
        };

        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(baseDir, "..", ".."))
        };
        foreach (var r in roots)
        {
            try
            {
                if (Directory.Exists(r))
                {
                    var hit = Directory.EnumerateFiles(r, "MeetingAI.Worker.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit)) candidates.Add(hit);
                }
            }
            catch { }
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            _ = AppendLineAsync("[Host] 未找到 MeetingAI.Worker.exe。\n候选路径（依次尝试）：\n"
                                + string.Join("\n", candidates.Distinct()));
        }
        else
        {
            _ = AppendLineAsync($"[Host] Worker 位置：{found}");
        }
        return found;
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_worker is { HasExited: false })
            {
                await AppendLineAsync("[Host] Worker 已在运行，忽略重复启动。");
                return;
            }

            var workerPath = FindWorkerExe();
            if (string.IsNullOrEmpty(workerPath))
            {
                await AppendLineAsync("[Host] 请确认已生成 Worker，并把 MeetingAI.Worker.exe 复制到 Host 的输出目录，或按提示的候选路径检查。");
                return;
            }

            // 读取设备选择 (0=CPU, 1=GPU, 2=NPU)
            var startupPage = GetStartupPage();
            string graniteDevice = startupPage.CmbGraniteDevice.SelectedIndex switch
            {
                0 => "CPU",
                1 => "GPU",
                2 => "NPU",
                _ => "GPU"
            };
            string embeddingDevice = startupPage.CmbEmbeddingDevice.SelectedIndex switch
            {
                0 => "CPU",
                1 => "GPU",
                2 => "NPU",
                _ => "GPU"
            };

            await AppendLineAsync($"[Host] 设备配置: Granite={graniteDevice}, Embedding={embeddingDevice}");

            _worker = Process.Start(new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"--ppid {Environment.ProcessId} --granite-device {graniteDevice} --embedding-device {embeddingDevice}"
            });

            if (_worker is null)
            {
                await AppendLineAsync("[Host] Worker 启动失败：Process.Start 返回 null。");
                return;
            }

            // Worker 没有可见窗口，不转发的话它的启动失败原因会静默丢失
            _worker.OutputDataReceived += (_, ev) => { if (ev.Data is not null) _ = AppendLineAsync($"[Worker] {ev.Data}"); };
            _worker.ErrorDataReceived += (_, ev) => { if (ev.Data is not null) _ = AppendLineAsync($"[Worker] {ev.Data}"); };
            _worker.BeginOutputReadLine();
            _worker.BeginErrorReadLine();

            await Task.Delay(700);

            // Worker 起来就死了的话，后面连管道只会白等 30 秒超时
            if (_worker.HasExited)
            {
                var code = _worker.ExitCode;
                await AppendLineAsync($"[Host] Worker 启动后立即退出（exit code {code}）。");
                if (code == ExitCodeWorkerAlreadyRunning)
                {
                    await AppendLineAsync("[Host] 原因：已有 Worker 实例在运行。请结束残留的 MeetingAI.Worker.exe 后重试。");
                }
                _worker = null;
                return;
            }

            startupPage.BtnLoadGranite.IsEnabled = true;
            startupPage.BtnLoadEmbedding.IsEnabled = true;
            startupPage.BtnLoadSherpa.IsEnabled = true;
            startupPage.BtnLoadPunctuator.IsEnabled = true;
            startupPage.BtnLoadTranslationEnZh.IsEnabled = true;
            startupPage.BtnLoadTranslationZhEn.IsEnabled = true;
            BtnPing.IsEnabled = true;
            BtnTranscribe.IsEnabled = true;
            BtnLoopback.IsEnabled = true;
            BtnMicrophone.IsEnabled = true;
            BtnMeeting.IsEnabled = true;
            BtnMeetingBeta.IsEnabled = true;
            BtnMeetingBeta2.IsEnabled = true;
            BtnStop.IsEnabled = true;
            BtnTestEmbedding.IsEnabled = true;
            BtnTestSimilarity.IsEnabled = true;
            BtnStart.IsEnabled = false;

            // 启用 Granite 对话功能
            BtnGraniteSingle.IsEnabled = false; // 默认单轮模式，单轮按钮禁用
            BtnGraniteMulti.IsEnabled = true;   // 多轮按钮启用，可切换
            BtnGraniteClear.IsEnabled = true;
            BtnGraniteSend.IsEnabled = true;

            // 默认选中普通对话模式
            _currentDialogMode = "normal";
            _isRAGMode = false;

            // 启用模式选择按钮
            BtnQuickQA.IsEnabled = true;
            BtnIEMode.IsEnabled = true;
            BtnRAGMode.IsEnabled = true;
            BtnLLaVAMode.IsEnabled = true;

            // 启用Startup页面的模型加载按钮
            startupPage.BtnLoadWhisper.IsEnabled = true;
            startupPage.BtnLoadOpenVINOWhisper.IsEnabled = true;
            startupPage.BtnLoadLLaVA.IsEnabled = true;
            startupPage.BtnLoadSD.IsEnabled = true;

            // 启用Document Assistant页面的按钮
            var quickQAPage = GetQuickQAPage();
            quickQAPage.BtnQuickQALoad.IsEnabled = true;

            // 启用ChatPage的按钮
            var chatPage = GetChatPage();
            chatPage.BtnGraniteClearChat.IsEnabled = true;
            chatPage.BtnGraniteSendChat.IsEnabled = true;
            // ChatPage的对话模式现在由ComboBox控制，默认选中Single-turn（索引0）

            LblModeStatus.Text = "";

            LblStatus.Text = "Worker 已启动";
            await AppendLineAsync("[Host] Worker 启动完成");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 启动失败：{ex.Message}");
        }
    }

    public async Task EnsurePipeAsync()
    {
        await _pipeConnectLock.WaitAsync();
        try
        {
            // 多个页面可能同时检查模型状态。连接锁内必须再次检查，
            // 否则后到的调用会销毁先到调用刚建立的读取循环。
            if (_pipe is { IsConnected: true } &&
                _reader != null &&
                _readLoopTask is { IsCompleted: false })
            {
                return;
            }

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;
            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            _pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(30_000);

            _reader = new StreamReader(
                _pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var testCmd = new PingMessage { payload = "init-check" };
            var testJson = JsonSerializer.Serialize(
                testCmd,
                AppJsonContext.Default.PingMessage) + "\n";

            var testBuf = Encoding.UTF8.GetBytes(testJson);
            await _pipe.WriteAsync(testBuf, 0, testBuf.Length);
            await _pipe.FlushAsync();

            var ack = await _reader.ReadLineAsync();
            await AppendLineAsync($"[Worker ACK] {ack}");

            _pipeCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(
                () => PipeReadLoopAsync(_pipeCts.Token));
        }
        finally
        {
            _pipeConnectLock.Release();
        }
    }

    private async Task PipeReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe is { IsConnected: true } && _reader != null)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null)
                {
                    await AppendLineAsync("[Host] Worker 连接已关闭");
                    break;
                }
                if (line.Length == 0) continue;

                // partial 每秒约 10 条，而 AppendLineAsync 是 OutputBox.Text += 的 O(n²) 拼接，
                // 全量写进去会让日志框越来越卡。定稿结果保留，便于排查。
                if (!line.Contains("\"type\":\"streaming_partial\"") &&
                    !line.Contains("\"type\":\"streaming_translation_partial\"") &&
                    !line.Contains("\"type\":\"streaming_summary_partial\""))
                {
                    // 日志 UI 绝不能阻塞命名管道。否则 OutputBox 更新稍慢，
                    // Worker 的同步 WriteFile 就会把整个会后 Whisper 线程堵死。
                    _ = AppendLineAsync($"[Pipe] {line}");
                }

                // Split concatenated JSON messages (e.g., "}{"type":"embedding_ready")
                var jsonMessages = SplitJsonMessages(line);
                foreach (var jsonMsg in jsonMessages)
                {
                    try
                    {
                        await ProcessJsonMessage(jsonMsg);
                    }
                    catch (Exception ex)
                    {
                        // 单条业务消息失败不能杀掉整根管道的读取循环。
                        _ = AppendLineAsync(
                            $"[Pipe] Message handling error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync($"[Pipe] Read loop error: {ex.Message}");
        }
    }

    private List<string> SplitJsonMessages(string line)
    {
        var messages = new List<string>();

        // Handle concatenated JSON objects like: {...}{...}
        if (line.Contains("}{"))
        {
            // Split by }{ and reconstruct valid JSON objects
            var parts = line.Split(new[] { "}{" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (i > 0) part = "{" + part;  // Add opening brace
                if (i < parts.Length - 1) part = part + "}";  // Add closing brace
                messages.Add(part);
            }
        }
        else
        {
            messages.Add(line);
        }

        return messages;
    }

    private async Task ProcessJsonMessage(string jsonMsg)
    {
        // ========== 实时流式转录消息优先转发 ==========
        // streaming_started / streaming_stopped 早先不在任何分支里，会一路落到函数末尾被丢弃，
        // 导致页面的启动握手永远等不到回应（界面卡在 Loading model）。这里按前缀统一转发。
        if (StreamingMessageHandler is { } streamingHandler)
        {
            if (jsonMsg.Contains("\"type\":\"streaming_"))
            {
                streamingHandler.Invoke(jsonMsg);
                return;
            }
            // info 仍按原逻辑写日志，这里只是抄送一份给页面显示模型加载进度
            if (jsonMsg.Contains("\"type\":\"info\""))
            {
                streamingHandler.Invoke(jsonMsg);
            }
        }

        if (jsonMsg.Contains("\"type\":\"model_state\""))
        {
            HandleModelStateMessage(jsonMsg);
            return;
        }

        if (jsonMsg.Contains("\"type\":\"model_status\""))
        {
            HandleModelStatusMessage(jsonMsg);
            return;
        }

        // ========== Info 消息处理（设备枚举等） ==========
        if (jsonMsg.Contains("\"type\":\"info\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(msg))
                {
                    await AppendLineAsync(msg);
                }
            }
            catch { }
            return;
        }

        // ========== Granite 消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"token\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string token = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                await HandleGraniteStreamToken(token);
            }
            catch { }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"done\""))
        {
            try
            {
                HandleGraniteStreamDone();
                await AppendLineAsync("[Granite] 生成完成");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Granite] 处理 done 消息异常：{ex.Message}");
            }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"preload_started\""))
        {
            await AppendLineAsync("[Worker] 后台加载已启动");
            return;
        }

        if (jsonMsg.Contains("\"type\":\"granite_chat_status\""))
        {
            // 处理 Granite 聊天状态消息
            return;
        }

        if (jsonMsg.Contains("\"type\":\"granite_ready\""))
        {
            await AppendLineAsync("[DEBUG] *** granite_ready 处理代码被执行 ***");
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string device = root.TryGetProperty("device", out var d) ? (d.GetString() ?? "unknown") : "unknown";
                await AppendLineAsync($"[Granite] ✅ Model ready (device={device})");
                await AppendLineAsync("[DEBUG] *** 现在会停止转圈了 ***");
                _isGraniteLoaded = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    LblStatus.Text = "Granite 已就绪";
                    // Fix: Also stop the spinner for Granite
                    var startupPage = GetStartupPage();
                    if (startupPage != null)
                    {
                        startupPage.ProgressGranite.IsActive = false;
                        startupPage.ProgressGranite.Visibility = Visibility.Collapsed;
                        startupPage.BtnLoadGranite.Content = "Unload Granite";
                        startupPage.BtnLoadGranite.IsEnabled = true;
                        startupPage.CmbGraniteDevice.IsEnabled = false;
                        startupPage.TxtGraniteState.Text =
                            $"Ready · {device}";
                    }

                    // Update IE Chat UI to enable upload button
                    UpdateIEChatUI();
                });
            }
            catch { }
            return;
        }

        // ========== Embedding 消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"embedding_result\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;

                if (root.TryGetProperty("embedding", out var embeddingArray))
                {
                    int dim = embeddingArray.GetArrayLength();

                    // 解析向量
                    var embedding = new float[dim];
                    for (int i = 0; i < dim; i++)
                    {
                        embedding[i] = embeddingArray[i].GetSingle();
                    }

                    // 设置结果到等待的任务
                    SetEmbeddingResult(embedding);

                    await AppendLineAsync($"[Embedding] ✅ 收到向量 (dim={dim})");
                }
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Embedding] 解析结果异常: {ex.Message}");
                SetEmbeddingError(ex);
            }
            return;
        }

        // ========== Token 计数结果处理 ==========
        if (jsonMsg.Contains("\"type\":\"token_count_result\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;

                if (root.TryGetProperty("count", out var countProp))
                {
                    int tokenCount = countProp.GetInt32();

                    // 设置结果到等待的任务
                    lock (_tokenCountLock)
                    {
                        _tokenCountTcs?.TrySetResult(tokenCount);
                        _tokenCountTcs = null;
                    }
                }
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[TokenCount] 解析结果异常: {ex.Message}");
                lock (_tokenCountLock)
                {
                    _tokenCountTcs?.TrySetException(ex);
                    _tokenCountTcs = null;
                }
            }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"embedding_ready\""))
        {
            await AppendLineAsync("[DEBUG] *** embedding_ready 处理代码被执行 ***");
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string device = root.TryGetProperty("device", out var d) ? (d.GetString() ?? "unknown") : "unknown";
                int dim = root.TryGetProperty("dim", out var dimProp) ? dimProp.GetInt32() : 0;
                await AppendLineAsync($"[Embedding] ✅ Model ready (device={device}, dim={dim})");
                await AppendLineAsync("[DEBUG] *** 准备更新UI ***");
                DispatcherQueue.TryEnqueue(() =>
                {
                    LblStatus.Text = "模型加载完成 ✅";

                    // 更新Startup页面状态
                    var startupPage = GetStartupPage();
                    if (startupPage != null)
                    {
                        startupPage.ProgressEmbedding.IsActive = false;
                        startupPage.ProgressEmbedding.Visibility = Visibility.Collapsed;
                        startupPage.BtnLoadEmbedding.Content = "Unload Embedding";
                        startupPage.BtnLoadEmbedding.IsEnabled = true;
                        startupPage.CmbEmbeddingDevice.IsEnabled = false;
                        startupPage.TxtEmbeddingState.Text =
                            $"Ready · {device} · {dim} dimensions · OpenVINO GenAI";
                    }
                    _isEmbeddingLoaded = true;

                    _ = AppendLineAsync("[DEBUG] *** UI更新已完成 ***");
                });
                await AppendLineAsync("[DEBUG] *** UI更新已提交到队列 ***");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[DEBUG] *** embedding_ready 处理异常: {ex.Message} ***");
            }
            return;
        }

        // ========== Granite/Embedding 卸载响应 ==========
        if (jsonMsg.Contains("\"type\":\"granite_embedding_unloaded\""))
        {
            await AppendLineAsync("[Startup] ✓ Granite & Embedding models unloaded");
            DispatcherQueue.TryEnqueue(() =>
            {
                var startupPage = GetStartupPage();
                if (startupPage != null)
                {
                    startupPage.ProgressGranite.IsActive = false;
                    startupPage.ProgressGranite.Visibility = Visibility.Collapsed;
                    startupPage.ProgressEmbedding.IsActive = false;
                    startupPage.ProgressEmbedding.Visibility = Visibility.Collapsed;
                    startupPage.BtnLoadGranite.Content = "Load Granite";
                    startupPage.BtnLoadGranite.IsEnabled = true;
                    startupPage.BtnLoadEmbedding.Content = "Load Embedding";
                    startupPage.BtnLoadEmbedding.IsEnabled = true;
                    startupPage.CmbGraniteDevice.IsEnabled = true;
                    startupPage.CmbEmbeddingDevice.IsEnabled = true;
                    startupPage.TxtGraniteState.Text = "Not loaded";
                    startupPage.TxtEmbeddingState.Text =
                        "Not loaded · OpenVINO GenAI TextEmbeddingPipeline";
                }
                _isGraniteLoaded = false;
                _isEmbeddingLoaded = false;
            });
            return;
        }

        // ========== LLaVA 消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"llava_ready\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string device = root.TryGetProperty("device", out var d) ? (d.GetString() ?? "unknown") : "unknown";
                await AppendLineAsync($"[LLaVA] ✅ Model ready (device={device})");
                DispatcherQueue.TryEnqueue(() =>
                {
                    LblStatus.Text = "LLaVA 已就绪";

                    // 更新Startup页面状态
                    var startupPage = GetStartupPage();
                    if (startupPage != null)
                    {
                        startupPage.ProgressLLaVA.IsActive = false;
                        startupPage.ProgressLLaVA.Visibility = Visibility.Collapsed;
                        startupPage.BtnLoadLLaVA.Content = "Unload Model";
                        startupPage.BtnLoadLLaVA.IsEnabled = true;
                    }

                    // 启用所有 LLaVA 功能按钮
                    BtnUploadImage.IsEnabled = true;
                    BtnLLaVASingle.IsEnabled = false;  // 默认单轮模式
                    BtnLLaVAMulti.IsEnabled = true;
                    BtnLLaVAClear.IsEnabled = true;
                    BtnLLaVASend.IsEnabled = true;

                    _isLLaVALoaded = true;
                });
            }
            catch { }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"llava_unloaded\""))
        {
            await AppendLineAsync("[DEBUG] *** llava_unloaded 处理代码被执行 ***");
            await AppendLineAsync("[Startup] ✓ LLaVA model unloaded");
            DispatcherQueue.TryEnqueue(() =>
            {
                var startupPage = GetStartupPage();
                if (startupPage != null)
                {
                    startupPage.ProgressLLaVA.IsActive = false;
                    startupPage.ProgressLLaVA.Visibility = Visibility.Collapsed;
                    startupPage.BtnLoadLLaVA.Content = "Load Model";
                    startupPage.BtnLoadLLaVA.IsEnabled = true;
                    startupPage.CmbLLaVADevice.IsEnabled = true;
                }

                // 禁用 LLaVA 功能按钮
                BtnUploadImage.IsEnabled = false;
                BtnLLaVASingle.IsEnabled = false;
                BtnLLaVAMulti.IsEnabled = false;
                BtnLLaVAClear.IsEnabled = false;
                BtnLLaVASend.IsEnabled = false;

                _isLLaVALoaded = false;
            });
            return;
        }

        if (jsonMsg.Contains("\"type\":\"llava_token\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string token = root.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : "";
                await HandleLLaVAStreamToken(token);
            }
            catch { }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"llava_complete\""))
        {
            try
            {
                HandleLLaVAStreamDone();
                await AppendLineAsync("[LLaVA] 生成完成");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[LLaVA] 处理 complete 消息异常：{ex.Message}");
            }
            return;
        }

        // ========== Stable Diffusion 消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"sd_ready\"") ||
            jsonMsg.Contains("\"type\":\"sd_progress\"") ||
            jsonMsg.Contains("\"type\":\"sd_complete\""))
        {
            try
            {
                await AppendLineAsync($"[SD Handler] 开始处理消息: {jsonMsg.Substring(0, Math.Min(100, jsonMsg.Length))}...");
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";
                await AppendLineAsync($"[SD Handler] 消息类型: {type}, 调用 HandleSDMessage");
                HandleSDMessage(type, root);
                await AppendLineAsync($"[SD Handler] HandleSDMessage 调用完成");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[SD] 处理消息异常: {ex.Message}");
            }
            return;
        }

        // ========== Whisper 消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"whisper_ready\""))
        {
            await AppendLineAsync("[DEBUG] *** whisper_ready 处理代码被执行 ***");
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string device = root.TryGetProperty("device", out var d) ? (d.GetString() ?? "unknown") : "unknown";
                await AppendLineAsync($"[Whisper] ✅ Model ready (device={device})");
                await AppendLineAsync("[DEBUG] *** 调用 HandleWhisperLoadResponse ***");
                HandleWhisperLoadResponse(true, $"Model loaded on {device}");
            }
            catch { }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"whisper_error\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "Unknown error") : "Unknown error";
                await AppendLineAsync($"[Whisper] ✗ Error: {message}");
                HandleWhisperLoadResponse(false, message);
            }
            catch { }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"whisper_unloaded\""))
        {
            await AppendLineAsync("[DEBUG] *** whisper_unloaded 处理代码被执行 ***");
            await AppendLineAsync("[Startup] ✓ Whisper model unloaded");
            await AppendLineAsync("[DEBUG] *** 调用 HandleWhisperUnloadResponse ***");
            HandleWhisperUnloadResponse(true, "");
            return;
        }

        // ========== OpenVINO Whisper 响应 ==========
        if (jsonMsg.Contains("\"type\":\"whisper_openvino_ready\""))
        {
            await AppendLineAsync("[DEBUG] *** whisper_openvino_ready 处理代码被执行 ***");
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string modelPath = root.TryGetProperty("model_path", out var mp) ? (mp.GetString() ?? "unknown") : "unknown";
                await AppendLineAsync($"[OpenVINO Whisper] ✅ Model ready (path={modelPath})");
                await AppendLineAsync("[DEBUG] *** 调用 HandleOpenVINOWhisperLoadResponse ***");
                HandleOpenVINOWhisperLoadResponse(true, $"Model loaded from {modelPath}");
            }
            catch (Exception ex)
            {
                await AppendLineAsync(
                    $"[OpenVINO Whisper] Invalid ready response: {ex.Message}");
                HandleOpenVINOWhisperLoadResponse(
                    false,
                    "Worker 返回的模型加载结果格式无效");
            }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"whisper_openvino_error\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;
                string message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "Unknown error") : "Unknown error";
                await AppendLineAsync($"[OpenVINO Whisper] ✗ Error: {message}");
                HandleOpenVINOWhisperLoadResponse(false, message);
            }
            catch (Exception ex)
            {
                await AppendLineAsync(
                    $"[OpenVINO Whisper] Invalid error response: {ex.Message}");
                HandleOpenVINOWhisperLoadResponse(
                    false,
                    "Worker 返回的模型错误信息格式无效");
            }
            return;
        }

        if (jsonMsg.Contains("\"type\":\"whisper_openvino_unloaded\""))
        {
            await AppendLineAsync("[DEBUG] *** whisper_openvino_unloaded 处理代码被执行 ***");
            await AppendLineAsync("[Startup] ✓ OpenVINO Whisper model unloaded");
            await AppendLineAsync("[DEBUG] *** 调用 HandleOpenVINOWhisperUnloadResponse ***");
            HandleOpenVINOWhisperUnloadResponse(true, "");
            return;
        }

        // ========== 相似度诊断测试结果 ==========
        if (jsonMsg.Contains("\"type\":\"similarity_test_result\""))
        {
            try
            {
                using var jd = JsonDocument.Parse(jsonMsg);
                var root = jd.RootElement;

                await AppendLineAsync("\n========== 相似度诊断结果 ==========");

                if (root.TryGetProperty("pairs", out var pairsArray))
                {
                    for (int i = 0; i < pairsArray.GetArrayLength(); i++)
                    {
                        var pair = pairsArray[i];
                        string text1 = pair.TryGetProperty("text1", out var t1) ? (t1.GetString() ?? "") : "";
                        string text2 = pair.TryGetProperty("text2", out var t2) ? (t2.GetString() ?? "") : "";
                        float similarity = pair.TryGetProperty("similarity", out var sim) ? sim.GetSingle() : 0f;

                        await AppendLineAsync($"\n[对比 {i + 1}]");
                        await AppendLineAsync($"  文本1: {text1}");
                        await AppendLineAsync($"  文本2: {text2}");
                        await AppendLineAsync($"  相似度: {similarity:F4} ({similarity * 100:F2}%)");
                    }
                }

                await AppendLineAsync("\n====================================\n");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[诊断] 解析结果异常: {ex.Message}");
            }
            return;
        }

        // ========== Whisper 转录消息处理 ==========
        if (jsonMsg.Contains("\"type\":\"asr_segment\""))
        {
            // 转发给 OpenVINO Whisper 页面（如果有处理器）
            OpenVINOWhisperMessageHandler?.Invoke(jsonMsg);
            return;
        }
        if (jsonMsg.Contains("\"type\":\"transcribe_complete\"") ||
            jsonMsg.Contains("\"type\":\"error\""))
        {
            // 转发给 OpenVINO Whisper 页面（如果有处理器）
            OpenVINOWhisperMessageHandler?.Invoke(jsonMsg);

            _transcribeTcs?.TrySetResult(true);
            _transcribeTcs = null;
            return;
        }

        // ========== 进度消息处理（OpenVINO Whisper）==========
        if (jsonMsg.Contains("\"type\":\"progress\""))
        {
            // 转发给 OpenVINO Whisper 页面（如果有处理器）
            OpenVINOWhisperMessageHandler?.Invoke(jsonMsg);
            return;
        }

        // 流式转录消息已在本方法开头统一转发
    }

    private async void BtnPing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsurePipeAsync();

            var cmd = new PingMessage { payload = "hello from host" };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.PingMessage) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync("[Host] 已发送测试 ping");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 发送失败：{ex.Message}");
            _pipeCts?.Cancel(); _pipeCts = null; _readLoopTask = null;
            _reader = null; _pipe?.Dispose(); _pipe = null;
        }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            try { if (_loopback is not null) _loopback.StopRecording(); } catch { }
            _isLoopback = false;
            BtnLoopback.Content = "扬声器转录";

            if (_pipe is { IsConnected: true })
            {
                var cmd = new QuitMessage();
                var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.QuitMessage) + "\n";

                var buf = Encoding.UTF8.GetBytes(json);
                await _pipe.WriteAsync(buf, 0, buf.Length);
                await _pipe.FlushAsync();
            }

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            if (_worker != null && !_worker.HasExited)
            {
                if (!_worker.WaitForExit(2000))
                {
                    _worker.Kill();
                }
            }
            _worker = null;

            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnLoopback.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnTestEmbedding.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止";
            await AppendLineAsync("[Host] Worker 已停止");

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 停止时异常：{ex.Message}");

            _pipeCts?.Cancel();
            _pipeCts = null;
            _readLoopTask = null;

            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            try
            {
                if (_worker is { HasExited: false }) _worker.Kill();
            }
            catch { }
            _worker = null;

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = null;

            BtnPing.IsEnabled = false;
            BtnTranscribe.IsEnabled = false;
            BtnLoopback.IsEnabled = false;
            BtnStop.IsEnabled = false;
            BtnStart.IsEnabled = true;
            LblStatus.Text = "已停止(异常)";
        }
    }

    public async Task SendJsonAsync(string json)
    {
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe == null || !_pipe.IsConnected)
            {
                throw new InvalidOperationException("Worker 未连接。请先启动 Worker。");
            }
            await _pipe.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
            await _pipe.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _pipeWriteLock.Release();
        }
    }
}
