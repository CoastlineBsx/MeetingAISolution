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
    // Helper method to get SettingsPage
    private Pages.SettingsPage? GetSettingsPage()
    {
        return SettingsFrame?.Content as Pages.SettingsPage;
    }

    // ========== Microphone Test ==========
    public async void BtnMicrophoneTest_Click(object sender, RoutedEventArgs e)
    {
        var page = GetSettingsPage();
        if (page == null) return;

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
            page.MicrophoneVolumeText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task StartMicrophoneTestAsync()
    {
        var page = GetSettingsPage();
        if (page == null) return;

        _microphoneTestCapture = new WasapiCapture();
        _microphoneTestCancellation = new CancellationTokenSource();

        // Show volume panel
        page.MicrophoneVolumePanel.Visibility = Visibility.Visible;
        page.MicrophoneVolumeBar.Value = 0;
        page.MicrophoneVolumeText.Text = "0%";

        // Update button
        page.BtnMicrophoneTest.Content = "Stop test";
        _isMicrophoneTestRunning = true;

        // Data available event - calculate and display volume
        _microphoneTestCapture.DataAvailable += (_, args) =>
        {
            if (_microphoneTestCancellation?.Token.IsCancellationRequested == true)
                return;

            // Calculate peak volume (more intuitive for users)
            float peak = CalculatePeak(args.Buffer, args.BytesRecorded, _microphoneTestCapture.WaveFormat);

            // Apply 5x gain for display (makes normal speech show 30-60%)
            float displayValue = Math.Min(peak * 5.0f, 1.0f);

            // Update UI on UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                var p = GetSettingsPage();
                if (p != null && _isMicrophoneTestRunning)
                {
                    int percentage = (int)(displayValue * 100);
                    p.MicrophoneVolumeBar.Value = percentage;
                    p.MicrophoneVolumeText.Text = $"{percentage}%";
                }
            });
        };

        _microphoneTestCapture.StartRecording();

        await Task.CompletedTask;
    }

    private async Task StopMicrophoneTestAsync()
    {
        var page = GetSettingsPage();
        if (page == null) return;

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
        page.BtnMicrophoneTest.Content = "Start test";

        // Keep volume panel visible for a moment
        await Task.Delay(500);
        if (!_isMicrophoneTestRunning)
        {
            page.MicrophoneVolumePanel.Visibility = Visibility.Collapsed;
        }
    }

    // ========== Speaker Test ==========
    public async void BtnSpeakerTest_Click(object sender, RoutedEventArgs e)
    {
        var page = GetSettingsPage();
        if (page == null) return;

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
            page.BtnSpeakerTest.Content = $"Error";
            await Task.Delay(2000);
            page.BtnSpeakerTest.Content = "Play test sound";
        }
    }

    private async Task StartSpeakerTestAsync()
    {
        var page = GetSettingsPage();
        if (page == null) return;

        _isSpeakerTestRunning = true;
        page.BtnSpeakerTest.Content = "Stop";

        // Create pink noise generator (softer, more pleasant than sine wave)
        _speakerTestGenerator = new SignalGenerator()
        {
            Gain = 0.15,  // 15% volume - comfortable level for pink noise
            Type = SignalGeneratorType.Pink
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
        var page = GetSettingsPage();
        if (page == null) return;

        _isSpeakerTestRunning = false;

        if (_speakerTestOutput != null)
        {
            _speakerTestOutput.Stop();
            _speakerTestOutput.Dispose();
            _speakerTestOutput = null;
        }

        _speakerTestGenerator = null;

        page.BtnSpeakerTest.Content = "Play test sound";
    }

    // ========== Helper Functions ==========
    private float CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        float maxPeak = 0f;
        int sampleCount = bytesRecorded / format.BlockAlign;  // Fixed: use BlockAlign for channels

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            // 32-bit float samples
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = Math.Abs(BitConverter.ToSingle(buffer, i * 4));
                if (sample > maxPeak) maxPeak = sample;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            // 16-bit PCM samples
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                float normalized = Math.Abs(sample / 32768f);
                if (normalized > maxPeak) maxPeak = normalized;
            }
        }

        return maxPeak;
    }

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
