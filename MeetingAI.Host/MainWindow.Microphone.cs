using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async void BtnMicrophone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMicrophone)
            {
                await StartMicrophoneAsync();
            }
            else
            {
                await StopMicrophoneAndTranscribeAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Host] 麦克风录音异常：{ex.Message}");
        }
    }

    private async Task StartMicrophoneAsync()
    {
        _microphoneTempFile = Path.Combine(Path.GetTempPath(), $"microphone_{DateTime.Now:yyyyMMdd_HHmmss}_raw.wav");

        if (_selectedMicrophoneId == null || _selectedMicrophoneId == "default")
        {
            _microphone = new WasapiCapture();
            await AppendLineAsync("[Host] 使用默认麦克风");
        }
        else
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_selectedMicrophoneId);
            _microphone = new WasapiCapture(device);
            await AppendLineAsync($"[Host] 使用麦克风: {device.FriendlyName}");
        }

        _microphoneWriter = new WaveFileWriter(_microphoneTempFile, _microphone.WaveFormat);

        await AppendLineAsync($"[Host] 录制格式: {_microphone.WaveFormat.SampleRate}Hz, " +
            $"{_microphone.WaveFormat.BitsPerSample}bit, {_microphone.WaveFormat.Channels}声道, " +
            $"{_microphone.WaveFormat.Encoding}");

        _microphone.DataAvailable += (_, args) =>
        {
            _microphoneWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        };

        _microphone.RecordingStopped += async (_, __) =>
        {
            try { _microphoneWriter?.Dispose(); } catch { }
            _microphoneWriter = null;
            try { _microphone?.Dispose(); } catch { }
            _microphone = null;
            await AppendLineAsync($"[Host] 麦克风录音已停止，原始文件：{_microphoneTempFile}");
        };

        _microphone.StartRecording();
        _isMicrophone = true;
        BtnMicrophone.Content = "🛑 停止麦克风转录";
        await AppendLineAsync("[Host] 开始录制麦克风音频...");
    }

    private async Task StopMicrophoneAndTranscribeAsync()
    {
        if (_microphone != null)
        {
            _microphone.StopRecording();
        }
        _isMicrophone = false;
        BtnMicrophone.Content = "🎤 麦克风转录";

        var rawPath = _microphoneTempFile;
        if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath))
        {
            await AppendLineAsync("[Host] 未找到录制的文件，取消转录。");
            return;
        }

        await Task.Delay(500);

        string mode = CmbMicrophoneMode.SelectedIndex switch
        {
            0 => "speech",
            1 => "music",
            2 => "mixed",
            _ => "speech"
        };

        string language = CmbMicrophoneLanguage.SelectedIndex switch
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

        bool needDenoise = false;
        string processedPath = rawPath;

        if (needDenoise)
        {
            processedPath = Path.Combine(Path.GetTempPath(), $"microphone_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            await AppendLineAsync($"[Host] 正在应用 FFmpeg 降噪处理...");

            try
            {
                await FFMpegCore.FFMpegArguments
                    .FromFileInput(rawPath)
                    .OutputToFile(processedPath, true, options => options
                        .WithAudioCodec("pcm_s16le")
                        .WithAudioSamplingRate(16000)
                        .WithCustomArgument("-ac 1")
                        .WithCustomArgument("-af \"" +
                            "highpass=f=80," +
                            "lowpass=f=8000," +
                            "afftdn=nr=20:nf=-40:tn=1," +
                            "anlmdn=s=0.00001:p=0.002:r=0.002," +
                            "equalizer=f=2000:t=q:w=1:g=3," +
                            "compand=attacks=0.1:decays=0.3:points=-60/-60|-30/-20|-20/-10|0/-5," +
                            "loudnorm=I=-16:TP=-1.5:LRA=11" +
                        "\"")
                    )
                    .ProcessAsynchronously();

                await AppendLineAsync($"[Host] 降噪处理完成：{processedPath}");
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Host] FFmpeg 降噪失败，使用原始文件: {ex.Message}");
                processedPath = rawPath;
            }
        }
        else
        {
            await AppendLineAsync($"[Host] 跳过降噪，使用原始录音");
        }

        await EnsurePipeAsync();

        var cmd = new TranscribeFileCommand { path = processedPath, mode = mode, language = language };
        var json = JsonSerializer.Serialize(cmd, AppJsonContext.Default.TranscribeFileCommand) + "\n";
        var buf = Encoding.UTF8.GetBytes(json);
        await _pipe!.WriteAsync(buf, 0, buf.Length);
        await _pipe.FlushAsync();
        await AppendLineAsync($"[Host] 已发送麦克风录音转录命令（模式: {mode}，语言: {language}，降噪: {needDenoise}）：{processedPath}");

        _transcribeTcs?.TrySetCanceled();
        _transcribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
