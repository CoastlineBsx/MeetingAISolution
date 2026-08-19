using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeetingAI.Host.Models;

/// <summary>
/// 图片附件数据模型
/// </summary>
public class ImageAttachment : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private BitmapImage? _preview;
    private BitmapImage? _fullImage;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public BitmapImage? Preview
    {
        get => _preview;
        set
        {
            if (_preview != value)
            {
                _preview = value;
                OnPropertyChanged();
            }
        }
    }

    public BitmapImage? FullImage
    {
        get => _fullImage;
        set
        {
            if (_fullImage != value)
            {
                _fullImage = value;
                OnPropertyChanged();
            }
        }
    }

    // INotifyPropertyChanged 实现
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 聊天消息数据模型
/// </summary>
public class ChatMessage : INotifyPropertyChanged
{
    private string _role = "";
    private string _content = "";
    private bool _isStreaming = false;
    private List<Citation> _citations = new();
    private ImageAttachment? _image;
    private bool _isGenerating = false;
    private int _generationProgress = 0;
    private string? _jsonContent = null;
    private Visibility _jsonVisible = Visibility.Collapsed;

    public string Role
    {
        get => _role;
        set
        {
            if (_role != value)
            {
                _role = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayRole));
                OnPropertyChanged(nameof(IsUserVisible));
                OnPropertyChanged(nameof(IsAssistantVisible));
            }
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming != value)
            {
                _isStreaming = value;
                OnPropertyChanged();
            }
        }
    }

    // SD 生成进度
    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            if (_isGenerating != value)
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressVisible));
            }
        }
    }

    public int GenerationProgress
    {
        get => _generationProgress;
        set
        {
            if (_generationProgress != value)
            {
                _generationProgress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => IsGenerating ? $"Generating... {GenerationProgress}%" : "";
    public Visibility ProgressVisible => IsGenerating ? Visibility.Visible : Visibility.Collapsed;

    // SD 元数据
    public string? GenerationInfo { get; set; }  // "✅ Generated in 18.3s"
    public Dictionary<string, object>? Metadata { get; set; }  // Prompt, Seed, CFG等

    // UI 显示用
    public string DisplayRole => Role == "user" ? "你" : "AI";
    public string DisplayTime => Timestamp.ToString("HH:mm:ss");

    // UI 绑定用（控制气泡显示）
    public Visibility IsUserVisible => Role == "user" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsAssistantVisible => Role == "assistant" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsSystemVisible => Role == "system" ? Visibility.Visible : Visibility.Collapsed;

    // Streaming state visibility
    public Visibility StreamingVisible => IsStreaming ? Visibility.Visible : Visibility.Collapsed;

    // Content visibility (hide when generating)
    public Visibility ContentVisible => IsGenerating ? Visibility.Collapsed : Visibility.Visible;

    // RAG 引用
    public List<Citation> Citations
    {
        get => _citations;
        set
        {
            if (_citations != value)
            {
                _citations = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCitations));
                OnPropertyChanged(nameof(CitationsVisible));
                OnPropertyChanged(nameof(CitationCount));
            }
        }
    }

    public bool HasCitations => Citations != null && Citations.Count > 0;
    public int CitationCount => Citations?.Count ?? 0;
    public Visibility CitationsVisible => HasCitations ? Visibility.Visible : Visibility.Collapsed;

    // Image support
    public ImageAttachment? Image
    {
        get => _image;
        set
        {
            if (_image != value)
            {
                _image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(ImageVisible));
            }
        }
    }

    public bool HasImage => Image != null;
    public Visibility ImageVisible => HasImage ? Visibility.Visible : Visibility.Collapsed;

    // IE Chat JSON support
    public string? JsonContent
    {
        get => _jsonContent;
        set
        {
            if (_jsonContent != value)
            {
                _jsonContent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasJsonContent));
            }
        }
    }

    public Visibility JsonVisible
    {
        get => _jsonVisible;
        set
        {
            if (_jsonVisible != value)
            {
                _jsonVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasJsonContent => !string.IsNullOrEmpty(JsonContent);

    // INotifyPropertyChanged 实现
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
