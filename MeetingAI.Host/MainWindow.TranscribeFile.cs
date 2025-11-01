using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using FFMpegCore;
using NAudio.Wave;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnTranscribe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                await AppendLineAsync("[Host] 未选择文件");
                return;
            }

            await AppendLineAsync($"[Host] 选择的音频文件: {file.Path}");

            string audioPath = file.Path;
            if (file.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                audioPath = await ConvertToWavAsync(file.Path);
                if (string.IsNullOrEmpty(audioPath))
                {
                    await AppendLineAsync("[Host] 音频格式转换失败");
                    return;
                }
            }

            await EnsurePipeAsync();

            string mode = CmbTranscribeMode.SelectedIndex switch
            {
                0 => "speech",
                1 => "music",
                2 => "mixed",
                _ => "auto"
            };

            string language = CmbTranscribeLanguage.SelectedIndex switch
            {
                0 => "auto",
                1 => "zh",
                2 => "en",
                3 => "ja",
                4 => "ko",
                5 => "es",
                6 => "fr",
                7 => "de",
                _ => "auto"
            };

            var cmd = new TranscribeFileCommand { path = audioPath, mode = mode, language = language };
            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";

            var buf = Encoding.UTF8.GetBytes(json);
            await _pipe!.WriteAsync(buf, 0, buf.Length);
            await _pipe.FlushAsync();

            await AppendLineAsync($"[Host] 转录命令已发送（模式: {mode}，语言: {language}），等待结果...");

            _transcribeTcs?.TrySetCanceled();
            _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsLocal = _transcribeTcs;

            var overallTimeoutMs = 600_000;
            var completed = await Task.WhenAny(tcsLocal!.Task, Task.Delay(overallTimeoutMs));
            if (completed != tcsLocal.Task)
            {
                await AppendLineAsync("[Host] 总等待时长到达上限，结束等待（后续消息仍会在输出框显示）");
            }
            else
            {
                await AppendLineAsync("[Host] 本次转录完成");
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 转录测试失败：{ex.Message}");
            _pipeCts?.Cancel(); _pipeCts = null; _readLoopTask = null;
            _reader = null; _pipe?.Dispose(); _pipe = null;
        }
    }

    private async Task<string?> ConvertToWavAsync(string sourcePath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var tempWav = Path.Combine(Path.GetTempPath(), $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                await EnsureFFmpegAsync();
                _ = AppendLineAsync("[Host] 使用FFmpeg处理音频（工业级）...");

                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(tempWav, true, options => options
                        .WithAudioCodec("pcm_s16le")
                        .WithAudioSamplingRate(16000)
                        .WithCustomArgument("-ac 1")
                        .WithCustomArgument("-af \"highpass=f=200,lowpass=f=3000,loudnorm=I=-16:TP=-1.5:LRA=11\"")
                    )
                    .ProcessAsynchronously();

                if (File.Exists(tempWav))
                {
                    _ = AppendLineAsync($"[Host] FFmpeg转换完成: {tempWav}");
                    return tempWav;
                }
                else
                {
                    _ = AppendLineAsync("[Host] FFmpeg转换失败，使用NAudio备选");
                    return ConvertWithNAudio(sourcePath, tempWav);
                }
            }
            catch (Exception ex)
            {
                _ = AppendLineAsync($"[Host] FFmpeg失败: {ex.Message}，使用NAudio备选");
                var tempWav = Path.Combine(Path.GetTempPath(), $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                return ConvertWithNAudio(sourcePath, tempWav);
            }
        });
    }

    private async Task EnsureFFmpegAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                GlobalFFOptions.Configure(options =>
                {
                });
                _ = AppendLineAsync("[Host] FFmpeg已就绪");
            }
            catch (Exception ex)
            {
                _ = AppendLineAsync($"[Host] FFmpeg配置警告: {ex.Message}");
            }
        });
    }

    private string? ConvertWithNAudio(string sourcePath, string outputPath)
    {
        try
        {
            using var reader = new AudioFileReader(sourcePath);
            var outFormat = new WaveFormat(16000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, outFormat);
            WaveFileWriter.CreateWaveFile(outputPath, resampler);
            _ = AppendLineAsync($"[Host] NAudio转换完成: {outputPath}");
            return outputPath;
        }
        catch
        {
            return null;
        }
    }
}
