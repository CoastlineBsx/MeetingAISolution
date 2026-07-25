using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetingAI.Host.Pages;

public sealed partial class QuickQAPage : Page
{
    private MainWindow? _mainWindow;

    public QuickQAPage()
    {
        InitializeComponent();

        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    // Event handlers that forward to MainWindow
    private void BtnQuickQAClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnQuickQAClearHistory_Click(sender, e);
    }

    private void BtnQuickQALoad_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnQuickQALoad_Click(sender, e);
    }

    private void BtnQuickQAClear_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnQuickQAClear_Click(sender, e);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.CopyMessage_Click(sender, e);
    }

    private void TxtQuickQAInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _mainWindow?.TxtQuickQAInput_PreviewKeyDown(sender, e);
    }

    private void BtnQuickQASend_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnQuickQASend_Click(sender, e);
    }
}
