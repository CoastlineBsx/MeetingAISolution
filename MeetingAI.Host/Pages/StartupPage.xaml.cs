using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host.Pages;

public sealed partial class StartupPage : Page
{
    private MainWindow? _mainWindow;

    public StartupPage()
    {
        InitializeComponent();

        // Get MainWindow reference when page loads
        this.Loaded += async (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
            if (_mainWindow != null)
            {
                await _mainWindow.RefreshModelStatusAsync();
            }
        };
    }

    private void BtnLoadGranite_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadGranite_Click(sender, e);
    }

    private void BtnLoadEmbedding_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadEmbedding_Click(sender, e);
    }

    private void BtnLoadWhisper_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadWhisper_Click(sender, e);
    }

    private void BtnLoadOpenVINOWhisper_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadOpenVINOWhisper_Click(sender, e);
    }

    private void BtnLoadSherpa_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadSherpa_Click(sender, e);
    }

    private void BtnLoadPunctuator_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadPunctuator_Click(sender, e);
    }

    private void BtnLoadTranslationEnZh_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadTranslationEnZh_Click(sender, e);
    }

    private void BtnLoadTranslationZhEn_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadTranslationZhEn_Click(sender, e);
    }

    private void BtnLoadLLaVA_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadLLaVA_Click(sender, e);
    }

    private void BtnLoadSD_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadSD_Click(sender, e);
    }
}
