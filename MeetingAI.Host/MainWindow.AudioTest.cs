using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Window
{
    // ========== Microphone Test ==========
    private async void BtnMicrophoneTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isMicrophoneTestRunning)
            {
                await StartMicrophoneTestAsync();
            }
            else
            {
                await StopMicrophoneTestAsync();
            }
        }
        catch (Exception ex)
        {
            // Show error in a simple way
            MicrophoneVolumeText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task StartMicrophoneTestAsync()
    {
        _microphoneTestCapture = new WasapiCapture();
        _microphoneTestCancellation = new CancellationTokenSource();

        // Show volume panel
        MicrophoneVolumePanel.Visibility = Visibility.Visible;
        MicrophoneVolumeBar.Value = 0;
        MicrophoneVolumeText.Text = "0%";

        // Update button
        BtnMicrophoneTest.Content = "Stop test";
        _isMicrophoneTestRunning = true;

        // Data available event - calculate and display volume
        _microphoneTestCapture.DataAvailable += (_, args) =>
        {
            if (_microphoneTestCancellation?.Token.IsCancellationRequested == true)
                return;

            // Calculate RMS volume
            float rms = CalculateRMS(args.Buffer, args.BytesRecorded, _microphoneTestCapture.WaveFormat);

            // Update UI on UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isMicrophoneTestRunning)
                {
                    int percentage = (int)(rms * 100);
                    MicrophoneVolumeBar.Value = Math.Min(percentage, 100);
                    MicrophoneVolumeText.Text = $"{percentage}%";
                }
            });
        };

        _microphoneTestCapture.StartRecording();

        await Task.CompletedTask;
    }

    private async Task StopMicrophoneTestAsync()
    {
        _isMicrophoneTestRunning = false;

        _microphoneTestCancellation?.Cancel();

        if (_microphoneTestCapture != null)
        {
            _microphoneTestCapture.StopRecording();
            _microphoneTestCapture.Dispose();
            _microphoneTestCapture = null;
        }

        _microphoneTestCancellation?.Dispose();
        _microphoneTestCancellation = null;

        // Update UI
        BtnMicrophoneTest.Content = "Start test";

        // Keep volume panel visible for a moment
        await Task.Delay(500);
        if (!_isMicrophoneTestRunning)
        {
            MicrophoneVolumePanel.Visibility = Visibility.Collapsed;
        }
    }

    // ========== Speaker Test ==========
    private async void BtnSpeakerTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isSpeakerTestRunning)
            {
                await StartSpeakerTestAsync();
            }
            else
            {
                StopSpeakerTest();
            }
        }
        catch (Exception ex)
        {
            // Show error
            BtnSpeakerTest.Content = $"Error";
            await Task.Delay(2000);
            BtnSpeakerTest.Content = "Play test sound";
        }
    }

    private async Task StartSpeakerTestAsync()
    {
        _isSpeakerTestRunning = true;
        BtnSpeakerTest.Content = "Stop";

        // Create 1kHz sine wave generator
        _speakerTestGenerator = new SignalGenerator()
        {
            Gain = 0.2,  // 20% volume - comfortable level
            Frequency = 1000,  // 1kHz tone
            Type = SignalGeneratorType.Sin
        };

        // Take 3 seconds of audio
        var testTone = _speakerTestGenerator.Take(TimeSpan.FromSeconds(3));

        _speakerTestOutput = new WaveOutEvent();
        _speakerTestOutput.Init(testTone);

        // Handle playback stopped
        _speakerTestOutput.PlaybackStopped += (_, __) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StopSpeakerTest();
            });
        };

        _speakerTestOutput.Play();

        await Task.CompletedTask;
    }

    private void StopSpeakerTest()
    {
        _isSpeakerTestRunning = false;

        if (_speakerTestOutput != null)
        {
            _speakerTestOutput.Stop();
            _speakerTestOutput.Dispose();
            _speakerTestOutput = null;
        }

        _speakerTestGenerator = null;

        BtnSpeakerTest.Content = "Play test sound";
    }

    // ========== Helper Functions ==========
    private float CalculateRMS(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        // Convert bytes to samples
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;

        float sum = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            // 32-bit float samples
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(buffer, i * 4);
                sum += sample * sample;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            // 16-bit PCM samples
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                float normalized = sample / 32768f;
                sum += normalized * normalized;
            }
        }

        if (sampleCount == 0)
            return 0f;

        return (float)Math.Sqrt(sum / sampleCount);
    }
}
