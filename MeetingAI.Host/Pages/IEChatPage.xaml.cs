using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetingAI.Host.Pages;

public sealed partial class IEChatPage : Page
{
    private MainWindow? _mainWindow;

    public IEChatPage()
    {
        InitializeComponent();

        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    // Event handlers that forward to MainWindow
    private void CmbIEChatTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mainWindow?.CmbIEChatTemplate_SelectionChanged(sender, e);
    }

    private void BtnIEChatUpload_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnIEChatUpload_Click(sender, e);
    }

    private void BtnIEChatExtract_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnIEChatExtract_Click(sender, e);
    }

    private void BtnIEChatClear_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnIEChatClear_Click(sender, e);
    }

    private void CopyIEChatMessage_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.CopyIEChatMessage_Click(sender, e);
    }

    private void BtnCopyIEJson_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnCopyIEJson_Click(sender, e);
    }

    private void BtnExportIEJson_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnExportIEJson_Click(sender, e);
    }

    private void TxtIEChatInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _mainWindow?.TxtIEChatInput_PreviewKeyDown(sender, e);
    }

    private void BtnIEChatSend_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnIEChatSend_Click(sender, e);
    }
}
