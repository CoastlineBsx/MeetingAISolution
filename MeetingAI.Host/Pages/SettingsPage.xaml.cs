using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host.Pages;

public sealed partial class SettingsPage : Page
{
    private MainWindow? _mainWindow;

    public SettingsPage()
    {
        InitializeComponent();

        // Get MainWindow reference when page loads
        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox?.SelectedItem is ComboBoxItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();
            ElementTheme theme = tag switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            // Apply to entire window content
            if (_mainWindow?.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme;
            }

            // Optional: save setting
            // Windows.Storage.ApplicationData.Current.LocalSettings.Values["AppTheme"] = tag;
        }
    }

    private void BtnMicrophoneTest_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement microphone test
        // Show volume panel and start capturing audio levels
    }

    private void BtnSpeakerTest_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement speaker test
        // Play a test tone
    }
}
