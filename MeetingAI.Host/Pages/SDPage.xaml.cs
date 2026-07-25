using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetingAI.Host.Pages;

public sealed partial class SDPage : Page
{
    private MainWindow? _mainWindow;

    public SDPage()
    {
        InitializeComponent();

        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    // Event handlers that forward to MainWindow
    private void BtnSDClear_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSDClear_Click(sender, e);
    }

    private void BtnSDSingle_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSDSingle_Click(sender, e);
    }

    private void BtnSDMulti_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSDMulti_Click(sender, e);
    }

    private void TxtSDInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _mainWindow?.TxtSDInput_PreviewKeyDown(sender, e);
    }

    private void BtnSaveImage_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSaveImage_Click(sender, e);
    }

    private void BtnCopyImage_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnCopyImage_Click(sender, e);
    }

    private void BtnRegenerate_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnRegenerate_Click(sender, e);
    }

    private void BtnSDGenerate_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSDGenerate_Click(sender, e);
    }
}
