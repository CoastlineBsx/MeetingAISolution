using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetingAI.Host.Pages;

public sealed partial class LLaVAPage : Page
{
    private MainWindow? _mainWindow;

    public LLaVAPage()
    {
        InitializeComponent();

        this.Loaded += (s, e) =>
        {
            _mainWindow = App.MainWindow as MainWindow;
        };
    }

    // Event handlers that forward to MainWindow
    private void BtnClearVisualChat_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnClearVisualChat_Click(sender, e);
    }

    private void OnImageTapped_Visual(object sender, TappedRoutedEventArgs e)
    {
        _mainWindow?.OnImageTapped_Visual(sender, e);
    }

    private void OnDragEnter_Visual(object sender, DragEventArgs e)
    {
        _mainWindow?.OnDragEnter_Visual(sender, e);
    }

    private void OnDragLeave_Visual(object sender, DragEventArgs e)
    {
        _mainWindow?.OnDragLeave_Visual(sender, e);
    }

    private void OnDragOver_Visual(object sender, DragEventArgs e)
    {
        _mainWindow?.OnDragOver_Visual(sender, e);
    }

    private void OnDrop_Visual(object sender, DragEventArgs e)
    {
        _mainWindow?.OnDrop_Visual(sender, e);
    }

    private void RemoveImage_Visual(object sender, RoutedEventArgs e)
    {
        _mainWindow?.RemoveImage_Visual(sender, e);
    }

    private void InputBoxVisual_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        _mainWindow?.InputBoxVisual_KeyDown(sender, e);
    }

    private void BtnUploadImageVisual_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnUploadImageVisual_Click(sender, e);
    }

    private void BtnSendVisual_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.BtnSendVisual_Click(sender, e);
    }

    private void CloseImagePreview_Visual(object sender, TappedRoutedEventArgs e)
    {
        _mainWindow?.CloseImagePreview_Visual(sender, e);
    }
}
