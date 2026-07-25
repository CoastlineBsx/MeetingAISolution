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
        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    private void BtnPreloadModels_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnPreloadModels_Click(sender, e);
    }

    private void BtnLoadWhisper_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadWhisper_Click(sender, e);
    }

    private void BtnLoadOpenVINOWhisper_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnLoadOpenVINOWhisper_Click(sender, e);
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
