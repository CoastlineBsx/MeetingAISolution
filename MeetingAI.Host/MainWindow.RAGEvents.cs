using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingAI.Host;

/// <summary>
/// RAG UI 事件处理
/// </summary>
public sealed partial class MainWindow : Window
{
    // ========== 以下函数已废弃，由新的模式按钮系统替代 ==========
    // /// <summary>
    // /// RAG 模式切换事件（已废弃 - 使用 BtnRAGMode_Click 替代）
    // /// </summary>
    // private async void ToggleRAGMode_Toggled(object sender, RoutedEventArgs e)
    // {
    //     try
    //     {
    //         _isRAGMode = ToggleRAGMode.IsOn;
    //
    //         if (_isRAGMode)
    //         {
    //             await AppendLineAsync("[RAG] 已切换到 RAG 模式");
    //
    //             // 如果 RAG 未初始化，自动初始化
    //             if (!_isRAGInitialized)
    //             {
    //                 await AppendLineAsync("[RAG] 自动初始化 RAG 服务...");
    //                 await InitializeRAGAsync();
    //             }
    //         }
    //         else
    //         {
    //             await AppendLineAsync("[RAG] 已切换到普通模式");
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         await AppendLineAsync($"[RAG] 切换模式失败: {ex.Message}");
    //         // 回滚开关状态
    //         ToggleRAGMode.IsOn = !_isRAGMode;
    //     }
    // }

    // /// <summary>
    // /// 初始化 RAG 按钮点击（已废弃 - RAG模式自动初始化）
    // /// </summary>
    // private async void BtnRAGInit_Click(object sender, RoutedEventArgs e)
    // {
    //     try
    //     {
    //         BtnRAGInit.IsEnabled = false;
    //         await InitializeRAGAsync();
    //         BtnRAGInit.Content = "✅ 已初始化";
    //
    //         // 初始化成功后启用相关按钮
    //         ToggleRAGMode.IsEnabled = true;
    //         BtnRAGTest.IsEnabled = true;
    //     }
    //     catch (Exception ex)
    //     {
    //         await AppendLineAsync($"[RAG] 初始化失败: {ex.Message}");
    //         BtnRAGInit.IsEnabled = true;
    //     }
    // }

    /// <summary>
    /// 添加测试文档按钮点击
    /// </summary>
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
