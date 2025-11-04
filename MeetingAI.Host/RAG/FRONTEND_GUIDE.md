# 前端使用指南 - RAG 功能集成

## 🎯 三种使用方式

### 方式 1: 在现有 MainWindow 中使用

#### 步骤 1: 添加成员变量

```csharp
// MainWindow.xaml.cs
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;

public sealed partial class MainWindow : Window
{
    // 现有字段...
    
    // RAG 相关字段
    private WorkerPipeClient? _workerClient;
    private SqliteVectorDatabase? _vectorDb;
    private RAGService? _ragService;
    
    // ...
}
```

#### 步骤 2: 在窗口初始化时启动 RAG

```csharp
public MainWindow()
{
    this.InitializeComponent();
    
    // 现有初始化...
    
    // 初始化 RAG（异步）
    _ = InitializeRAGAsync();
}

private async Task InitializeRAGAsync()
{
    try
    {
        // 1. Worker 路径
        var workerPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "MeetingAI.Worker.exe");
        
        // 2. 启动 Worker
        _workerClient = new WorkerPipeClient(workerPath);
        bool started = await _workerClient.StartAsync();
        
        if (!started)
        {
            Debug.WriteLine("[RAG] Worker 启动失败");
            return;
        }
        
        Debug.WriteLine("[RAG] ✅ Worker 已启动");
        
        // 3. 初始化向量数据库
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingAI",
            "rag.db");
        
        _vectorDb = new SqliteVectorDatabase(dbPath);
        await _vectorDb.InitializeAsync();
        
        Debug.WriteLine("[RAG] ✅ 数据库已初始化");
        
        // 4. 创建服务
        var embeddingService = new EmbeddingNPUService(_workerClient);
        var graniteService = new GraniteNPUService(_workerClient);
        
        _ragService = new RAGService(
            _vectorDb,
            embeddingService,
            graniteService,
            topK: 3);
        
        Debug.WriteLine("[RAG] ✅ RAG 服务已就绪");
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[RAG] 初始化失败: {ex.Message}");
    }
}
```

#### 步骤 3: 添加 UI 控件（在 MainWindow.xaml 中）

```xml
<StackPanel Orientation="Vertical" Margin="10">
    <TextBlock Text="RAG 问答" FontSize="20" FontWeight="Bold" Margin="0,0,0,10"/>
    
    <!-- 问题输入框 -->
    <TextBox x:Name="QuestionTextBox" 
             PlaceholderText="输入你的问题..."
             Height="40"
             Margin="0,0,0,10"/>
    
    <!-- 提问按钮 -->
    <Button x:Name="AskButton" 
            Content="提问" 
            Click="AskButton_Click"
            Width="100"
            HorizontalAlignment="Left"
            Margin="0,0,0,10"/>
    
    <!-- 回答显示区 -->
    <TextBlock Text="回答:" FontWeight="SemiBold" Margin="0,10,0,5"/>
    <TextBox x:Name="AnswerTextBox"
             IsReadOnly="True"
             AcceptsReturn="True"
             TextWrapping="Wrap"
             Height="200"
             ScrollViewer.VerticalScrollBarVisibility="Auto"/>
</StackPanel>
```

#### 步骤 4: 实现提问逻辑

```csharp
// MainWindow.xaml.cs

private async void AskButton_Click(object sender, RoutedEventArgs e)
{
    if (_ragService == null)
    {
        AnswerTextBox.Text = "RAG 服务未就绪";
        return;
    }
    
    var question = QuestionTextBox.Text;
    if (string.IsNullOrWhiteSpace(question))
    {
        return;
    }
    
    try
    {
        AskButton.IsEnabled = false;
        AnswerTextBox.Text = "思考中...";
        
        // 流式显示回答
        AnswerTextBox.Text = "";
        await foreach (var chunk in _ragService.QueryStreamAsync(question))
        {
            AnswerTextBox.Text += chunk;
        }
    }
    catch (Exception ex)
    {
        AnswerTextBox.Text = $"错误: {ex.Message}";
    }
    finally
    {
        AskButton.IsEnabled = true;
    }
}
```

#### 步骤 5: 清理资源（窗口关闭时）

```csharp
protected override void OnClosed()
{
    _ragService?.Dispose();
    _vectorDb?.Dispose();
    _workerClient?.Dispose();
    
    base.OnClosed();
}
```

---

### 方式 2: 创建独立的 RAG 窗口

#### RAGWindow.xaml

```xml
<Window
    x:Class="MeetingAI.Host.RAGWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="RAG 智能问答"
    Width="800"
    Height="600">
    
    <Grid Padding="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <!-- 标题 -->
        <TextBlock Grid.Row="0" 
                   Text="RAG 智能问答系统" 
                   FontSize="24" 
                   FontWeight="Bold"
                   Margin="0,0,0,20"/>
        
        <!-- 文档管理区 -->
        <StackPanel Grid.Row="1" Margin="0,0,0,20">
            <TextBlock Text="文档库" FontWeight="SemiBold" Margin="0,0,0,10"/>
            <StackPanel Orientation="Horizontal" Spacing="10">
                <Button Content="上传文档" Click="UploadDocument_Click"/>
                <Button Content="查看文档列表" Click="ViewDocuments_Click"/>
            </StackPanel>
        </StackPanel>
        
        <!-- 问答区 -->
        <ScrollViewer Grid.Row="2">
            <StackPanel x:Name="ChatPanel" Spacing="10"/>
        </ScrollViewer>
        
        <!-- 输入区 -->
        <Grid Grid.Row="3" Margin="0,20,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            
            <TextBox x:Name="InputBox"
                     Grid.Column="0"
                     PlaceholderText="输入你的问题..."
                     Height="40"
                     Margin="0,0,10,0"/>
            
            <Button x:Name="SendButton"
                    Grid.Column="1"
                    Content="发送"
                    Click="Send_Click"
                    Width="80"
                    Height="40"/>
        </Grid>
    </Grid>
</Window>
```

#### RAGWindow.xaml.cs

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace MeetingAI.Host;

public sealed partial class RAGWindow : Window
{
    private WorkerPipeClient? _workerClient;
    private RAGService? _ragService;
    private SqliteVectorDatabase? _vectorDb;

    public RAGWindow()
    {
        this.InitializeComponent();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var workerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MeetingAI.Worker.exe");
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingAI", "rag.db");

        _workerClient = new WorkerPipeClient(workerPath);
        await _workerClient.StartAsync();

        _vectorDb = new SqliteVectorDatabase(dbPath);
        await _vectorDb.InitializeAsync();

        var embeddingService = new EmbeddingNPUService(_workerClient);
        var graniteService = new GraniteNPUService(_workerClient);
        _ragService = new RAGService(_vectorDb, embeddingService, graniteService);

        AddSystemMessage("RAG 系统已就绪，您可以开始提问了！");
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var question = InputBox.Text;
        if (string.IsNullOrWhiteSpace(question) || _ragService == null) return;

        AddUserMessage(question);
        InputBox.Text = "";

        var answerPanel = AddAssistantMessage("");
        var answerText = answerPanel.Children[1] as TextBlock;

        try
        {
            await foreach (var chunk in _ragService.QueryStreamAsync(question))
            {
                if (answerText != null)
                    answerText.Text += chunk;
            }
        }
        catch (Exception ex)
        {
            if (answerText != null)
                answerText.Text = $"错误: {ex.Message}";
        }
    }

    private void AddUserMessage(string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(50, 0, 0, 0)
        };

        var bubble = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.LightBlue),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 10, 15, 10),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
        };

        panel.Children.Add(bubble);
        ChatPanel.Children.Add(panel);
    }

    private StackPanel AddAssistantMessage(string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 50, 0)
        };

        var bubble = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 10, 15, 10),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
        };

        panel.Children.Add(bubble);
        ChatPanel.Children.Add(panel);
        return panel;
    }

    private void AddSystemMessage(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 5)
        };
        ChatPanel.Children.Add(textBlock);
    }

    private async void UploadDocument_Click(object sender, RoutedEventArgs e)
    {
        // TODO: 实现文档上传逻辑
        // 需要先实现 PDF/Word 解析
    }

    private async void ViewDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (_vectorDb == null) return;
        
        var docs = await _vectorDb.GetAllDocumentsAsync();
        // TODO: 显示文档列表
    }
}
```

---

### 方式 3: 从主窗口打开 RAG 窗口

#### 在 MainWindow.xaml 添加按钮

```xml
<Button Content="打开 RAG 问答" Click="OpenRAGWindow_Click"/>
```

#### 在 MainWindow.xaml.cs 添加事件

```csharp
private void OpenRAGWindow_Click(object sender, RoutedEventArgs e)
{
    var ragWindow = new RAGWindow();
    ragWindow.Activate();
}
```

---

## 🔄 完整工作流程示例

### 场景：添加文档并查询

```csharp
// 1. 初始化 RAG
await InitializeRAGAsync();

// 2. 添加文档（假设已解析）
var chunks = new List<(string Content, int PageNumber)>
{
    ("人工智能（AI）是计算机科学的一个分支...", 1),
    ("深度学习是机器学习的子集...", 2),
    ("GPT 是一种大型语言模型...", 3)
};

await _ragService.AddDocumentAsync(
    "AI基础.pdf",
    @"C:\Documents\AI基础.pdf",
    "pdf",
    "zh",
    chunks);

// 3. 查询
var answer = await _ragService.QueryAsync("什么是人工智能？");
Console.WriteLine(answer);
```

---

## ⚡ 高级用法

### 流式显示优化

```csharp
private async Task StreamAnswerAsync(string question)
{
    AnswerTextBox.Text = "";
    var buffer = "";
    var lastUpdate = DateTime.Now;
    
    await foreach (var chunk in _ragService.QueryStreamAsync(question))
    {
        buffer += chunk;
        
        // 每 100ms 更新一次 UI
        if ((DateTime.Now - lastUpdate).TotalMilliseconds > 100)
        {
            AnswerTextBox.Text = buffer;
            lastUpdate = DateTime.Now;
        }
    }
    
    AnswerTextBox.Text = buffer; // 最终更新
}
```

### 查看文档库

```csharp
private async Task ShowDocumentsAsync()
{
    var docs = await _vectorDb.GetAllDocumentsAsync();
    
    foreach (var doc in docs)
    {
        Debug.WriteLine($"[{doc.DocId}] {doc.Filename}");
        Debug.WriteLine($"  类型: {doc.FileType}");
        Debug.WriteLine($"  语言: {doc.Language}");
        Debug.WriteLine($"  块数: {doc.TotalChunks}");
        Debug.WriteLine($"  上传: {doc.UploadTime}");
    }
}
```

---

## 📝 总结

**最简单的方式**：在 MainWindow 添加几个控件 + 几个方法即可使用！

**推荐流程**：
1. 在 `MainWindow` 初始化时调用 `InitializeRAGAsync()`
2. 添加一个 TextBox 输入问题
3. 添加一个 Button 调用 `QueryStreamAsync()`
4. 添加一个 TextBox 显示答案

**就这么简单！** 🎉
