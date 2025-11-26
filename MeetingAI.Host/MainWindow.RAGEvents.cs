using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host;


public sealed partial class MainWindow : Window
{

    private async void BtnRAGTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AddTestDocumentAsync();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[RAG] 添加测试文档失败: {ex.Message}");
        }
    }
}
