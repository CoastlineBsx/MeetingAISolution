using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;

namespace MeetingAI.Host;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    private void EnumerateMicrophoneDevices()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            while (CmbMicrophoneDevice.Items.Count > 1)
            {
                CmbMicrophoneDevice.Items.RemoveAt(1);
            }

            var separator = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMicrophoneDevice.Items.Add(separator);

            foreach (var device in devices)
            {
                var item = new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID };
                CmbMicrophoneDevice.Items.Add(item);
            }

            var enumerator2 = new MMDeviceEnumerator();
            var renders = enumerator2.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var rd in renders)
            {
                CmbLoopbackDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingSpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingBetaSpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
                CmbMeetingBeta2SpeakerDevice.Items.Add(new ComboBoxItem { Content = rd.FriendlyName, Tag = rd.ID });
            }

            while (CmbMeetingDevice.Items.Count > 1)
            {
                CmbMeetingDevice.Items.RemoveAt(1);
            }
            var separator2 = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMeetingDevice.Items.Add(separator2);
            foreach (var device in devices)
            {
                var item = new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID };
                CmbMeetingDevice.Items.Add(item);
            }

            while (CmbMeetingBetaDevice.Items.Count > 1)
            {
                CmbMeetingBetaDevice.Items.RemoveAt(1);
            }
            var separator3 = new ComboBoxItem { Content = "─────────────", IsEnabled = false };
            CmbMeetingBetaDevice.Items.Add(separator3);
            foreach (var device in devices)
            {
                CmbMeetingBetaDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
                CmbMeetingBeta2Device.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
            }

            _ = AppendLineAsync($"[Host] 已枚举 {devices.Count} 个麦克风设备");
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync($"[Host] 枚举麦克风设备失败: {ex.Message}");
        }
    }

    private void CmbMicrophoneDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMicrophoneDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId) _selectedMicrophoneId = deviceId; else _selectedMicrophoneId = null;
    }
    private void CmbLoopbackDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLoopbackDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedLoopbackDeviceId = id; else _selectedLoopbackDeviceId = null;
    }
    private void CmbMeetingSpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingSpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingSpeakerId = id; else _selectedMeetingSpeakerId = null;
    }
    private void CmbMeetingBetaSpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBetaSpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingBetaSpeakerId = id; else _selectedMeetingBetaSpeakerId = null;
    }
    private void CmbMeetingBeta2SpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta2SpeakerDevice.SelectedItem is ComboBoxItem item && item.Tag is string id) _selectedMeetingBeta2SpeakerId = id; else _selectedMeetingBeta2SpeakerId = null;
    }

    private void CmbMeetingDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
            _selectedMeetingMicrophoneId = deviceId;
        else
            _selectedMeetingMicrophoneId = null;
    }

    private void CmbMeetingBetaDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBetaDevice.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
            _selectedMeetingBetaMicrophoneId = deviceId;
        else
            _selectedMeetingBetaMicrophoneId = null;
    }

    private void CmbMeetingBeta2Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbMeetingBeta2Device.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
            _selectedMeetingBeta2MicrophoneId = deviceId;
        else
            _selectedMeetingBeta2MicrophoneId = null;
    }
}

