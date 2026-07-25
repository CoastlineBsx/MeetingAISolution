using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetingAI.Host.Pages;

public sealed partial class ChatPage : Page
{
    private MainWindow? _mainWindow;

    public ChatPage()
    {
        InitializeComponent();

        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    // Event handlers that forward to MainWindow
    private void CmbConversationMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mainWindow?.CmbConversationMode_SelectionChanged(sender, e);
    }

    private void BtnGraniteClear_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnGraniteClear_Click(sender, e);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.CopyMessage_Click(sender, e);
    }

    private void TxtGraniteInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _mainWindow?.TxtGraniteInput_PreviewKeyDown(sender, e);
    }

    private void BtnGraniteSend_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnGraniteSend_Click(sender, e);
    }

    private void BtnVoiceInput_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnVoiceInput_Click(sender, e);
    }
}
