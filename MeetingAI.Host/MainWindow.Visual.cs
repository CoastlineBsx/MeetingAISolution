using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MeetingAI.Host.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace MeetingAI.Host;

public partial class MainWindow : Window
{
    // Helper method to get LLaVAPage
    private Pages.LLaVAPage? GetLLaVAPage() => LLaVAFrame?.Content as Pages.LLaVAPage;

    // Visual Understanding chat history
    public ObservableCollection<ChatMessage> VisualChatHistory { get; set; } = new();

    // Current image attachment for Visual Understanding
    private ImageAttachment? _currentVisualImage = null;

    // Streaming message for Visual Understanding mode
    private ChatMessage? _visualStreamingMessage = null;
    private int _visualScrollThrottle = 0;

    // Clear Visual Understanding chat
    public void BtnClearVisualChat_Click(object sender, RoutedEventArgs e)
    {
        VisualChatHistory.Clear();
        _visualStreamingMessage = null;
        _currentVisualImage = null;
        UpdateVisualImageUI();
    }

    // Upload image button click
    public async void BtnUploadImageVisual_Click(object sender, RoutedEventArgs e)
    {
        await PickAndLoadImageVisual();
    }

    // Send message in Visual Understanding
    public async void BtnSendVisual_Click(object sender, RoutedEventArgs e)
    {
        await SendVisualMessage();
    }

    // Handle Enter key in input box
    public async void InputBoxVisual_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            await SendVisualMessage();
        }
    }

    // Drag-drop event handlers
    public void OnDragEnter_Visual(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop image here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    public void OnDragLeave_Visual(object sender, DragEventArgs e)
    {
        // Visual feedback when drag leaves
    }

    public void OnDragOver_Visual(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    public async void OnDrop_Visual(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0 && items[0] is StorageFile file)
            {
                // Check if it's an image file
                var extension = file.FileType.ToLower();
                if (IsImageFile(extension))
                {
                    await LoadImageFileVisual(file);
                }
                else
                {
                    await ShowErrorDialog("Invalid File", "Please drop an image file (PNG, JPG, GIF, BMP, WebP)");
                }
            }
        }
    }

    // Image tap handler for preview
    public void OnImageTapped_Visual(object sender, TappedRoutedEventArgs e)
    {
        var page = GetLLaVAPage();
        if (page != null && sender is Image image && image.Tag is ImageAttachment attachment && attachment.FullImage != null)
        {
            page.PreviewImageVisual.Source = attachment.FullImage;
            page.ImagePreviewOverlayVisual.Visibility = Visibility.Visible;
        }
    }

    // Close image preview
    public void CloseImagePreview_Visual(object sender, TappedRoutedEventArgs e)
    {
        var page = GetLLaVAPage();
        if (page != null)
        {
            page.ImagePreviewOverlayVisual.Visibility = Visibility.Collapsed;
        }
    }

    // Remove current image
    public void RemoveImage_Visual(object sender, RoutedEventArgs e)
    {
        _currentVisualImage = null;
        UpdateVisualImageUI();
    }

    // Helper: Pick and load image
    private async Task PickAndLoadImageVisual()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await LoadImageFileVisual(file);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Error Loading Image", ex.Message);
        }
    }

    // Helper: Load image file
    private async Task LoadImageFileVisual(StorageFile file)
    {
        try
        {
            // Create image attachment
            _currentVisualImage = new ImageAttachment
            {
                FilePath = file.Path,
                FileName = file.Name,
                FileSize = (long)(await file.GetBasicPropertiesAsync()).Size
            };

            // Load image as BitmapImage
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                // Create thumbnail (60x60)
                var thumbnail = new BitmapImage();
                thumbnail.DecodePixelWidth = 60;
                thumbnail.DecodePixelHeight = 60;
                await thumbnail.SetSourceAsync(stream);
                _currentVisualImage.Thumbnail = thumbnail;

                // Reset stream position
                stream.Seek(0);

                // Create preview (400px max)
                var preview = new BitmapImage();
                preview.DecodePixelWidth = 400;
                await preview.SetSourceAsync(stream);
                _currentVisualImage.Preview = preview;

                // Reset stream position
                stream.Seek(0);

                // Load full image
                var fullImage = new BitmapImage();
                await fullImage.SetSourceAsync(stream);
                _currentVisualImage.FullImage = fullImage;
                _currentVisualImage.Width = fullImage.PixelWidth;
                _currentVisualImage.Height = fullImage.PixelHeight;
            }

            UpdateVisualImageUI();
            await AppendLineAsync($"[Visual] Image prepared: {_currentVisualImage.FileName} ({_currentVisualImage.Width}x{_currentVisualImage.Height})");
            await AppendLineAsync($"[Visual] Image path: {_currentVisualImage.FilePath}");
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Error Loading Image", ex.Message);
            await AppendLineAsync($"[Visual] Failed to load image: {ex.Message}");
            _currentVisualImage = null;
            UpdateVisualImageUI();
        }
    }

    // Helper: Update UI for current image
    private void UpdateVisualImageUI()
    {
        var page = GetLLaVAPage();
        if (page != null)
        {
            if (_currentVisualImage != null)
            {
                page.CurrentImageBarVisual.Visibility = Visibility.Visible;
                page.CurrentImageThumbnailVisual.Source = _currentVisualImage.Thumbnail;
                page.CurrentImageNameVisual.Text = _currentVisualImage.FileName;
            }
            else
            {
                page.CurrentImageBarVisual.Visibility = Visibility.Collapsed;
            }
        }
    }

    // Helper: Check if file is an image
    private bool IsImageFile(string extension)
    {
        var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        return imageExtensions.Contains(extension.ToLower());
    }

    // Helper: Send Visual Understanding message
    private async Task SendVisualMessage()
    {
        var page = GetLLaVAPage();
        if (page == null) return;

        var input = page.InputBoxVisual.Text.Trim();

        // Check if we have input or image
        if (string.IsNullOrEmpty(input) && _currentVisualImage == null)
        {
            await ShowErrorDialog("Input Required", "Please enter a message or upload an image");
            return;
        }

        // Check if we have an image when there's input
        if (_currentVisualImage == null)
        {
            await ShowErrorDialog("No Image", "Please upload an image first");
            await AppendLineAsync("[Visual] Please upload an image first");
            return;
        }

        // Ensure pipe connection
        try
        {
            await EnsurePipeAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Connection Error", $"Failed to connect to Worker: {ex.Message}");
            return;
        }

        // Output debug info
        await AppendLineAsync($"[Visual] Image loaded: {_currentVisualImage.FileName}");
        await AppendLineAsync($"[Visual] Image path: {_currentVisualImage.FilePath}");

        // Create user message
        var userMessage = new ChatMessage
        {
            Role = "user",
            Content = string.IsNullOrEmpty(input) ? "What's in this image?" : input,
            Image = _currentVisualImage
        };

        VisualChatHistory.Add(userMessage);
        page.InputBoxVisual.Text = "";

        await AppendLineAsync($"[Visual] Sending: {userMessage.Content}");

        // Clear current image after adding to message
        _currentVisualImage = null;
        UpdateVisualImageUI();

        // Prepare assistant response
        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "",
            IsStreaming = true
        };
        VisualChatHistory.Add(assistantMessage);
        _visualStreamingMessage = assistantMessage;

        // Get parameters from UI
        float temperature = 0.7f;
        int maxTokens = 2048;
        string systemPrompt = GetVisualSystemPrompt();

        if (page.CmbCreativityVisual?.SelectedItem is ComboBoxItem tempItem &&
            tempItem.Tag is string tempStr &&
            float.TryParse(tempStr, out float temp))
        {
            temperature = temp;
        }

        if (page.CmbMaxLengthVisual?.SelectedItem is ComboBoxItem tokenItem &&
            tokenItem.Tag is string tokenStr &&
            int.TryParse(tokenStr, out int tokens))
        {
            maxTokens = tokens;
        }

        // Build request
        var request = new
        {
            type = "llava_generate",
            image_path = userMessage.Image?.FilePath ?? "",
            prompt = userMessage.Content,
            max_tokens = maxTokens,
            temperature = temperature
        };

        var jsonRequest = JsonSerializer.Serialize(request) + "\n";

        try
        {
            await SendJsonAsync(jsonRequest);
            await AppendLineAsync("[Visual] Request sent successfully");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[Visual] Error: {ex.Message}");
            assistantMessage.Content = $"Error: {ex.Message}";
            assistantMessage.IsStreaming = false;
            _visualStreamingMessage = null;
        }
    }

    // Helper: Get system prompt based on answer style
    private string GetVisualSystemPrompt()
    {
        var page = GetLLaVAPage();
        string basePrompt = "You are a helpful visual AI assistant. ";

        if (page?.CmbAnswerStyleVisual?.SelectedItem is ComboBoxItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();
            return basePrompt + tag switch
            {
                "Concise" => "Provide brief, direct answers focusing on the key visual elements.",
                "Conversational" => "Engage in a friendly, natural conversation about what you see.",
                "Detailed" => "Provide comprehensive analysis of all visual elements, colors, composition, and context.",
                "Educational" => "Explain what you see in an educational manner, teaching about the visual elements and their significance.",
                "Technical" => "Provide technical analysis including composition, lighting, color theory, and artistic techniques.",
                _ => "Analyze the image and provide helpful, accurate information."
            };
        }

        return basePrompt + "Analyze the image and provide helpful, accurate information.";
    }

    // Helper: Scroll to bottom of Visual Understanding chat
    private void ScrollToBottomVisual()
    {
        var page = GetLLaVAPage();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (page?.ScrollViewerVisual != null)
            {
                page.ScrollViewerVisual.ScrollToVerticalOffset(page.ScrollViewerVisual.ScrollableHeight);
            }
        });
    }
}