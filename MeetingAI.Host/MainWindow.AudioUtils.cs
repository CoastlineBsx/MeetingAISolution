using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    private async Task<string?> MixAudioFilesAsync(string micFile, string speakerFile)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var mixedFile = Path.Combine(Path.GetTempPath(), $"meeting_mixed_{timestamp}.wav");

                await AppendLineAsync("[Host] 使用 NAudio 进行音频混音（工业级流程）...");

                using var micReader = new AudioFileReader(micFile);
                using var speakerReader = new AudioFileReader(speakerFile);

                var micDuration = micReader.TotalTime;
                var speakerDuration = speakerReader.TotalTime;

                await AppendLineAsync($"[Host] 麦克风: {micReader.WaveFormat.SampleRate}Hz, {micReader.WaveFormat.Channels}声道, 时长 {micDuration.TotalSeconds:F2}秒");
                await AppendLineAsync($"[Host] 扬声器: {speakerReader.WaveFormat.SampleRate}Hz, {speakerReader.WaveFormat.Channels}声道, 时长 {speakerDuration.TotalSeconds:F2}秒");

                double durationDiff = Math.Abs(micDuration.TotalSeconds - speakerDuration.TotalSeconds);
                if (durationDiff > 0.1)
                {
                    await AppendLineAsync($"[Host] 警告：两个文件时长差异 {durationDiff:F2}秒，可能导致对齐问题");
                }
                else
                {
                    await AppendLineAsync($"[Host] ✓ 两个文件时长一致（差异 {durationDiff:F3}秒）");
                }

                var commonFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

                var micResampler = new MediaFoundationResampler(micReader, commonFormat);
                var micProvider = micResampler.ToSampleProvider();

                var speakerResampler = new MediaFoundationResampler(speakerReader, commonFormat);
                var speakerProvider = speakerResampler.ToSampleProvider();

                var micVolume = new VolumeSampleProvider(micProvider) { Volume = 0.7f };
                var speakerVolume = new VolumeSampleProvider(speakerProvider) { Volume = 0.5f };

                await AppendLineAsync("[Host] 混音权重: 麦克风 70%, 扬声器 50%");

                await AppendLineAsync("[Host] [DEBUG] 正在检查扬声器音频内容...");
                float[] spkTestSamples = new float[4800];
                int spkTestRead = speakerVolume.Read(spkTestSamples, 0, 4800);
                float spkTestMax = 0f;
                for (int i = 0; i < spkTestRead; i++)
                {
                    if (Math.Abs(spkTestSamples[i]) > spkTestMax)
                        spkTestMax = Math.Abs(spkTestSamples[i]);
                }
                await AppendLineAsync($"[Host] [DEBUG] 扬声器前0.1秒最大振幅: {spkTestMax:F4}");
                if (spkTestMax < 0.001f)
                {
                    await AppendLineAsync("[Host] [DEBUG] ⚠️ 警告：扬声器音频振幅过小，可能是静音或没录到声音！");
                }

                speakerReader.Position = 0;
                speakerResampler.Dispose();
                var speakerResampler2 = new MediaFoundationResampler(speakerReader, commonFormat);
                var speakerProvider2 = speakerResampler2.ToSampleProvider();
                var speakerVolume2 = new VolumeSampleProvider(speakerProvider2) { Volume = 0.5f };

                var mixer = new MixingSampleProvider(new[] { micVolume, speakerVolume2 });

                var outFormat = new WaveFormat(16000, 16, 1);
                using var finalResampler = new MediaFoundationResampler(mixer.ToWaveProvider(), outFormat);

                WaveFileWriter.CreateWaveFile16(mixedFile, finalResampler.ToSampleProvider());

                micResampler.Dispose();
                speakerResampler.Dispose();

                using var mixedReader = new AudioFileReader(mixedFile);
                var mixedDuration = mixedReader.TotalTime;
                await AppendLineAsync($"[Host] 混音完成: {outFormat.SampleRate}Hz, {outFormat.BitsPerSample}bit, {outFormat.Channels}声道, 时长 {mixedDuration.TotalSeconds:F2}秒");

                return mixedFile;
            }
            catch (Exception ex)
            {
                await AppendLineAsync($"[Host] 混音失败: {ex.Message}");
                await AppendLineAsync($"[Host] 详细错误: {ex.StackTrace}");
                return null;
            }
        });
    }

    private async Task PrependSilenceToWavFileAsync(string wavFilePath, double silenceDuration)
    {
        await Task.Run(() =>
        {
            if (silenceDuration < 0.001) return;

            var tempFile = Path.Combine(Path.GetTempPath(), $"temp_prepend_{Guid.NewGuid()}.wav");

            using (var reader = new WaveFileReader(wavFilePath))
            {
                var format = reader.WaveFormat;
                int silenceBytes = (int)(silenceDuration * format.SampleRate * format.BlockAlign);

                using (var writer = new WaveFileWriter(tempFile, format))
                {
                    byte[] silenceBuffer = new byte[silenceBytes];
                    Array.Fill<byte>(silenceBuffer, 0);
                    writer.Write(silenceBuffer, 0, silenceBuffer.Length);

                    byte[] buffer = new byte[format.SampleRate * format.BlockAlign];
                    int bytesRead;
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(100);

            File.Delete(wavFilePath);
            File.Move(tempFile, wavFilePath);
        });
    }
}
