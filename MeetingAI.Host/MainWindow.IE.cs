using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;

namespace MeetingAI.Host;

/// <summary>
/// IE模式（信息提取）核心逻辑
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MAX_IE_TOKENS = 50000;  // 最大 50K tokens
    private const int MAX_IE_TURNS = 10;      // 最多 10 轮对话

    /// <summary>
    /// 加载文档按钮点击事件
    /// </summary>
    private async void BtnIELoad_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 创建文件选择器（仅支持 TXT, DOCX, PDF）
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".pdf");

            // 获取窗口句柄并初始化 picker
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            // 选择文件
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await AppendLineAsync($"[IE] 正在加载文档：{file.Name}");

            // 使用已有的 DocumentProcessor 解析文档
            if (_documentProcessor == null)
            {
                // 如果还没初始化，需要初始化
                var baseDir = AppContext.BaseDirectory;
                var tesseractDataPath = Path.Combine(baseDir, "tessdata");
                _documentProcessor = new RAG.Services.DocumentProcessor(tesseractDataPath);
            }

            // 提取文档内容
            var extracted = await _documentProcessor.ExtractAsync(file.Path);

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                await AppendLineAsync($"[IE] ⚠️ 文档内容为空");
                return;
            }

            await AppendLineAsync($"[IE] ✅ 提取完成，内容长度：{extracted.Content.Length} 字符");

            // 计算 Token 数
            await AppendLineAsync($"[IE] 🔢 正在计算 Token 数...");
            int tokenCount = await CountTokensAsync(extracted.Content);

            if (tokenCount <= 0)
            {
                await AppendLineAsync($"[IE] ⚠️ Token 计算失败，无法加载文档");
                return;
            }

            await AppendLineAsync($"[IE] Token 数：{tokenCount}");

            // 检查是否超过限制
            if (tokenCount > MAX_IE_TOKENS)
            {
                await AppendLineAsync($"[IE] ⚠️ 文档过大（{tokenCount} tokens > {MAX_IE_TOKENS} tokens）");
                await AppendLineAsync($"[IE] 💡 建议使用 RAG 模式");
                return;
            }

            // 加载成功，保存文档信息
            _ieDocumentContent = extracted.Content;
            _ieDocumentName = extracted.FileName;
            _ieDocumentSize = extracted.FileSize;
            _ieTokenCount = tokenCount;
            _ieExtractedJson = null;
            _ieDialogHistory.Clear();

            // 自动识别文档类型
            await AppendLineAsync($"[IE] 🔍 正在识别文档类型...");
            await DetectDocumentTypeAsync();

            // 更新 UI
            UpdateIEUI();

            await AppendLineAsync($"[IE] ✅ 文档加载成功：{_ieDocumentName}");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 自动识别文档类型
    /// </summary>
    private async Task DetectDocumentTypeAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_ieDocumentContent))
            {
                return;
            }

            await EnsurePipeAsync();

            // 设置识别状态
            _isIEDetecting = true;
            _ieDetectionBuffer = new StringBuilder();

            // 取前1000字符用于识别
            string contentSample = _ieDocumentContent.Length > 1000
                ? _ieDocumentContent.Substring(0, 1000)
                : _ieDocumentContent;

            // 构建识别 Prompt
            var detectionPrompt = $@"请快速判断以下文档属于哪种类型，只输出类型ID。

文档内容（前1000字）：
{contentSample}

可选类型：
- resume（简历/求职）
- contract（合同/协议）
- invoice（发票/收据）
- news（新闻/报道）
- paper（学术论文）
- manual（产品说明书）
- legal（法律文书）
- financial（财务报表）
- email（邮件/信函）
- meeting（会议纪要）
- general（通用/其他）

只输出一个类型ID，不要任何其他文字。";

            // 使用 Granite 单轮模式（和正常对话一样的格式）
            string systemMessage = "You are a document classification assistant. Output only the document type ID.";
            string fullPrompt =
                $"<|start_of_role|>system<|end_of_role|>{systemMessage}<|end_of_text|>" +
                $"<|start_of_role|>user<|end_of_role|>{detectionPrompt}<|end_of_text|>" +
                $"<|start_of_role|>assistant<|end_of_role|>";

            var cmd = new GraniteGenerateStreamCommand
            {
                prompt = fullPrompt,
                max_tokens = 50,
                temperature = 0.3f
            };

            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync("[IE] 🔍 正在识别文档类型...");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ⚠️ 类型识别失败：{ex.Message}，使用通用模板");
            _ieSelectedTemplateId = "general";
            _isIEDetecting = false;
            _ieDetectionBuffer = null;
        }
    }

    /// <summary>
    /// 处理文档类型识别结果
    /// </summary>
    private async Task HandleIEDetectionResult(string typeId)
    {
        try
        {
            // 清理typeId（去除空白和可能的引号）
            typeId = typeId.Trim().Trim('"', '\'').ToLower();

            // 验证typeId是否有效
            var template = IETemplates.AllTemplates.FirstOrDefault(t => t.Id == typeId);
            if (template == null)
            {
                // 无效类型，使用通用模板
                typeId = "general";
                template = IETemplates.GetTemplate(typeId);
                await AppendLineAsync($"[IE] ⚠️ 无法识别类型（返回：{typeId}），使用通用模板");
            }
            else
            {
                await AppendLineAsync($"[IE] ✅ 检测到这是 **{template.Name}**，已自动选择对应字段包");
            }

            // 保存选中的模板ID
            _ieSelectedTemplateId = typeId;

            // 更新下拉菜单选择
            DispatcherQueue.TryEnqueue(() =>
            {
                for (int i = 0; i < CmbIETemplate.Items.Count; i++)
                {
                    if (CmbIETemplate.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == typeId)
                    {
                        CmbIETemplate.SelectedIndex = i;
                        break;
                    }
                }

                // 更新UI状态
                UpdateIEUI();
            });
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 处理识别结果失败：{ex.Message}");
            _ieSelectedTemplateId = "general";
        }
    }

    /// <summary>
    /// 开始提取按钮点击事件
    /// </summary>
    private async void BtnIEExtract_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ieDocumentContent))
            {
                await AppendLineAsync("[IE] ⚠️ 请先加载文档");
                return;
            }

            if (string.IsNullOrEmpty(_ieSelectedTemplateId))
            {
                await AppendLineAsync("[IE] ⚠️ 请选择文档类型");
                return;
            }

            await EnsurePipeAsync();

            // 设置提取状态
            _isIEExtracting = true;
            _ieExtractionBuffer = new StringBuilder();

            await AppendLineAsync($"[IE] 开始提取信息...");

            // 获取选中的模板
            var template = IETemplates.GetTemplate(_ieSelectedTemplateId);

            // 构建提取 Prompt
            string extractionPrompt = BuildIEExtractionPrompt(template, _ieDocumentContent);

            // 使用 Granite 单轮模式（和正常对话一样的格式）
            string systemMessage = "You are a professional information extraction assistant. Output only valid JSON format.";
            string fullPrompt =
                $"<|start_of_role|>system<|end_of_role|>{systemMessage}<|end_of_text|>" +
                $"<|start_of_role|>user<|end_of_role|>{extractionPrompt}<|end_of_text|>" +
                $"<|start_of_role|>assistant<|end_of_role|>";

            var cmd = new GraniteGenerateStreamCommand
            {
                prompt = fullPrompt,
                max_tokens = 2000,
                temperature = 0.1f
            };

            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";
            await SendJsonAsync(json);

            await AppendLineAsync($"[IE] 已发送提取请求，等待AI响应...");

            // 更新UI状态
            UpdateIEUI();
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 提取失败：{ex.Message}");
            _isIEExtracting = false;
            _ieExtractionBuffer = null;
        }
    }

    /// <summary>
    /// 构建IE提取Prompt
    /// </summary>
    private string BuildIEExtractionPrompt(DocumentTypeTemplate template, string documentContent)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是一个专业的信息提取助手。请仔细阅读文档，严格按照JSON格式提取指定字段。");
        sb.AppendLine();
        sb.AppendLine("=== 文档内容 ===");
        sb.AppendLine(documentContent);
        sb.AppendLine("=== 文档结束 ===");
        sb.AppendLine();
        sb.AppendLine("请提取以下字段：");
        sb.AppendLine();

        // 动态生成字段说明
        foreach (var field in template.Fields)
        {
            if (field.Type == "array" && field.SubFields != null)
            {
                sb.AppendLine($"- {field.Key} (array): {field.Description}");
                sb.AppendLine($"  每项包含:");
                foreach (var subField in field.SubFields)
                {
                    sb.AppendLine($"    - {subField.Key} ({subField.Type}): {subField.Label}");
                }
            }
            else
            {
                sb.AppendLine($"- {field.Key} ({field.Type}): {field.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("输出要求：");
        sb.AppendLine("1. 必须输出有效的JSON格式");
        sb.AppendLine("2. 只输出JSON，不要包含任何解释性文字");
        sb.AppendLine("3. 如果某个字段在文档中找不到，设置为 null");
        sb.AppendLine("4. 数组类型的字段，如果没有内容设为空数组 []");
        sb.AppendLine("5. 确保所有字段都包含在输出中");
        sb.AppendLine();
        sb.AppendLine("示例输出格式：");
        sb.AppendLine(template.ExampleJson);

        return sb.ToString();
    }

    /// <summary>
    /// 处理IE提取结果（由Pipe消息处理调用）
    /// </summary>
    public async Task HandleIEExtractionResult(string aiResponse)
    {
        try
        {
            _isIEExtracting = false;

            await AppendLineAsync("[IE] 收到AI响应，正在解析JSON...");

            // 提取JSON
            string jsonStr = ExtractJSON(aiResponse);

            // 验证JSON格式
            try
            {
                var testParse = JsonDocument.Parse(jsonStr);
                testParse.Dispose();

                _ieExtractedJson = jsonStr;

                // 格式化显示
                string formattedResult = FormatExtractedData(jsonStr);

                await AppendLineAsync("[IE] ✅ 提取成功！");
                await AppendLineAsync("=== 提取结果 ===");
                await AppendLineAsync(formattedResult);
                await AppendLineAsync("=================");

                // 更新UI
                UpdateIEUI();
            }
            catch (JsonException je)
            {
                await AppendLineAsync($"[IE] ⚠️ JSON解析失败：{je.Message}");
                await AppendLineAsync($"[IE] 原始响应：{aiResponse}");

                // 保存原始响应
                _ieExtractedJson = aiResponse;
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 处理提取结果失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 提取JSON（容错处理）
    /// </summary>
    private string ExtractJSON(string aiResponse)
    {
        aiResponse = aiResponse.Trim();

        // 尝试提取 {...}
        var match = Regex.Match(aiResponse, @"\{[\s\S]*\}", RegexOptions.Singleline);
        if (match.Success)
        {
            return match.Value;
        }

        // 尝试提取 [...]
        match = Regex.Match(aiResponse, @"\[[\s\S]*\]", RegexOptions.Singleline);
        if (match.Success)
        {
            return match.Value;
        }

        // 容错处理：检测是否是缺少开头 { 的JSON对象
        // 特征：以 " 开头，包含 :，可能有结尾的 }
        if (aiResponse.StartsWith("\"") && aiResponse.Contains(":"))
        {
            bool hasEndBrace = aiResponse.EndsWith("}");
            bool hasStartBrace = aiResponse.StartsWith("{");

            if (!hasStartBrace && hasEndBrace)
            {
                // 缺少开头的 {，补上
                return "{" + aiResponse;
            }
            else if (!hasStartBrace && !hasEndBrace)
            {
                // 两端都缺少，补全
                return "{" + aiResponse + "}";
            }
            else if (hasStartBrace && !hasEndBrace)
            {
                // 缺少结尾的 }，补上
                return aiResponse + "}";
            }
        }

        // 实在不行就返回原文
        return aiResponse;
    }

    /// <summary>
    /// 格式化提取的数据显示
    /// </summary>
    private string FormatExtractedData(string jsonStr)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonStr);
            var sb = new StringBuilder();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                sb.Append($"{prop.Name}: ");
                sb.AppendLine(FormatJsonValue(prop.Value, 0));
            }

            doc.Dispose();
            return sb.ToString();
        }
        catch
        {
            return jsonStr; // 解析失败就返回原文
        }
    }

    /// <summary>
    /// 格式化JSON值
    /// </summary>
    private string FormatJsonValue(JsonElement element, int indent)
    {
        string indentStr = new string(' ', indent * 2);

        return element.ValueKind switch
        {
            JsonValueKind.Null => "(未找到)",
            JsonValueKind.String => element.GetString() ?? "(空)",
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Array => FormatJsonArray(element, indent),
            JsonValueKind.Object => FormatJsonObject(element, indent),
            _ => element.ToString()
        };
    }

    private string FormatJsonArray(JsonElement element, int indent)
    {
        var items = element.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return "(无)";
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        for (int i = 0; i < items.Count; i++)
        {
            string indentStr = new string(' ', (indent + 1) * 2);
            sb.Append($"{indentStr}{i + 1}. ");
            sb.AppendLine(FormatJsonValue(items[i], indent + 1));
        }
        return sb.ToString();
    }

    private string FormatJsonObject(JsonElement element, int indent)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        foreach (var prop in element.EnumerateObject())
        {
            string indentStr = new string(' ', (indent + 1) * 2);
            sb.Append($"{indentStr}{prop.Name}: ");
            sb.AppendLine(FormatJsonValue(prop.Value, indent + 1));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 继续提问按钮点击事件
    /// </summary>
    private async void BtnIEContinueDialog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ieExtractedJson))
            {
                await AppendLineAsync("[IE] ⚠️ 请先完成信息提取");
                return;
            }

            _isIEDialogMode = true;
            UpdateIEUI();

            await AppendLineAsync("[IE] 💬 已进入对话模式");
            await AppendLineAsync("[IE] 你可以基于提取结果和原文档提问");
            await AppendLineAsync("[IE] 提示：使用全局的 [单轮模式]/[多轮模式] 按钮切换对话方式");
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 进入对话模式失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 构建IE对话Prompt
    /// </summary>
    public string BuildIEDialogPrompt(string userQuestion)
    {
        if (string.IsNullOrEmpty(_ieExtractedJson) || string.IsNullOrEmpty(_ieDocumentContent))
        {
            throw new InvalidOperationException("IE对话模式未初始化");
        }

        // 调试：输出数据状态
        _ = AppendLineAsync($"[IE DEBUG] 提取的JSON长度：{_ieExtractedJson.Length} 字符");
        _ = AppendLineAsync($"[IE DEBUG] JSON前100字符：{(_ieExtractedJson.Length > 100 ? _ieExtractedJson.Substring(0, 100) : _ieExtractedJson)}...");
        _ = AppendLineAsync($"[IE DEBUG] 原始文档长度：{_ieDocumentContent.Length} 字符");
        _ = AppendLineAsync($"[IE DEBUG] 文档前100字符：{(_ieDocumentContent.Length > 100 ? _ieDocumentContent.Substring(0, 100) : _ieDocumentContent)}...");

        var sb = new StringBuilder();

        // 单轮或多轮
        if (_ieDialogHistory.Count == 0)
        {
            // 单轮模式（第一轮）
            sb.AppendLine("你是一个智能文档助手。我已经从一份文档中提取了结构化信息，现在请回答用户的问题。");
            sb.AppendLine();
            sb.AppendLine("=== 提取的结构化信息 ===");
            sb.AppendLine(_ieExtractedJson);
            sb.AppendLine("=== 结构化信息结束 ===");
            sb.AppendLine();
            sb.AppendLine("=== 原始文档内容 ===");
            sb.AppendLine(_ieDocumentContent);
            sb.AppendLine("=== 文档结束 ===");
            sb.AppendLine();
            sb.AppendLine($"用户问题：{userQuestion}");
            sb.AppendLine();
            sb.AppendLine("回答要求：");
            sb.AppendLine("1. 优先从结构化信息中查找答案");
            sb.AppendLine("2. 如果结构化信息不足，参考原始文档内容");
            sb.AppendLine("3. 如果两者有冲突，以原始文档为准");
            sb.AppendLine("4. 给出准确、简洁的回答");
            sb.AppendLine("5. 如果无法找到答案，明确告知");
        }
        else
        {
            // 多轮模式
            sb.AppendLine("你是一个智能文档助手。我已经从一份文档中提取了结构化信息，现在请基于上下文回答用户的问题。");
            sb.AppendLine();
            sb.AppendLine("=== 提取的结构化信息 ===");
            sb.AppendLine(_ieExtractedJson);
            sb.AppendLine("=== 结构化信息结束 ===");
            sb.AppendLine();
            sb.AppendLine("=== 原始文档内容 ===");
            sb.AppendLine(_ieDocumentContent);
            sb.AppendLine("=== 文档结束 ===");
            sb.AppendLine();
            sb.AppendLine("=== 对话历史 ===");
            foreach (var (q, a) in _ieDialogHistory)
            {
                sb.AppendLine($"用户：{q}");
                sb.AppendLine($"助手：{a}");
            }
            sb.AppendLine("=== 历史结束 ===");
            sb.AppendLine();
            sb.AppendLine($"用户问题：{userQuestion}");
            sb.AppendLine();
            sb.AppendLine("回答要求：");
            sb.AppendLine("1. 结合对话历史理解用户意图");
            sb.AppendLine("2. 优先从结构化信息中查找答案");
            sb.AppendLine("3. 如果结构化信息不足，参考原始文档内容");
            sb.AppendLine("4. 如果两者有冲突，以原始文档为准");
            sb.AppendLine("5. 给出准确、简洁的回答");
        }

        string finalPrompt = sb.ToString();

        // 调试：输出最终prompt状态
        _ = AppendLineAsync($"[IE DEBUG] 构建的完整Prompt长度：{finalPrompt.Length} 字符");
        _ = AppendLineAsync($"[IE DEBUG] Prompt前200字符：{(finalPrompt.Length > 200 ? finalPrompt.Substring(0, 200) : finalPrompt)}...");

        return finalPrompt;
    }

    /// <summary>
    /// 检查是否可以继续IE对话
    /// </summary>
    public bool CanContinueIEDialog(out string errorMessage)
    {
        // 检查是否在IE对话模式
        if (!_isIEDialogMode || string.IsNullOrEmpty(_ieExtractedJson))
        {
            errorMessage = "⚠️ 请先点击'💬 继续提问'进入对话模式";
            return false;
        }

        // 检查是否达到轮数限制
        if (_ieDialogHistory.Count >= MAX_IE_TURNS)
        {
            errorMessage = $"⚠️ 已达到 {MAX_IE_TURNS} 轮上限，请点击'🗑️ 清空历史'重新开始";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// 添加IE对话历史
    /// </summary>
    public void AddIEDialogHistory(string question, string answer)
    {
        _ieDialogHistory.Add((question, answer));
        UpdateIEUI();
    }

    /// <summary>
    /// 清空IE对话历史（由全局"清空历史"按钮调用）
    /// </summary>
    public void ClearIEDialogHistory()
    {
        _ieDialogHistory.Clear();
        UpdateIEUI();
        _ = AppendLineAsync("[IE] 对话历史已清空");
    }

    /// <summary>
    /// 更新IE UI状态
    /// </summary>
    private void UpdateIEUI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 加载文档后的状态
            if (!string.IsNullOrEmpty(_ieDocumentName))
            {
                BtnIEExtract.IsEnabled = true;

                var template = IETemplates.GetTemplate(_ieSelectedTemplateId ?? "general");
                string sizeStr = FormatFileSize(_ieDocumentSize);
                LblIEDoc.Text = $"文件: {_ieDocumentName} ({sizeStr}, {_ieTokenCount} tokens)";
            }
            else
            {
                BtnIEExtract.IsEnabled = false;
                LblIEDoc.Text = "未加载文档";
            }

            // 提取完成后的状态
            if (!string.IsNullOrEmpty(_ieExtractedJson))
            {
                BtnIECopyJSON.IsEnabled = true;
                BtnIEExport.IsEnabled = true;
                BtnIEReExtract.IsEnabled = true;
                BtnIEContinueDialog.IsEnabled = true;

                if (_isIEDialogMode)
                {
                    int currentTurn = _ieDialogHistory.Count;
                    LblIEStatus.Text = $"💬 对话模式 ({currentTurn}/{MAX_IE_TURNS}轮)";
                }
                else
                {
                    LblIEStatus.Text = "✅ 提取完成";
                }
            }
            else
            {
                BtnIECopyJSON.IsEnabled = false;
                BtnIEExport.IsEnabled = false;
                BtnIEReExtract.IsEnabled = false;
                BtnIEContinueDialog.IsEnabled = false;
                LblIEStatus.Text = "";
            }
        });
    }

    /// <summary>
    /// 复制JSON按钮
    /// </summary>
    private void BtnIECopyJSON_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_ieExtractedJson))
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(_ieExtractedJson);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                _ = AppendLineAsync("[IE] ✅ JSON已复制到剪贴板");
            }
        }
        catch (Exception ex)
        {
            _ = AppendLineAsync($"[IE] ❌ 复制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 导出文件按钮
    /// </summary>
    private async void BtnIEExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ieExtractedJson))
            {
                return;
            }

            var savePicker = new FileSavePicker();
            savePicker.FileTypeChoices.Add("JSON文件", new List<string> { ".json" });
            savePicker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_ieDocumentName)}_提取结果.json";

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, _ieExtractedJson);
                await AppendLineAsync($"[IE] ✅ 已导出到：{file.Path}");
            }
        }
        catch (Exception ex)
        {
            await AppendLineAsync($"[IE] ❌ 导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重新提取按钮
    /// </summary>
    private void BtnIEReExtract_Click(object sender, RoutedEventArgs e)
    {
        _ieExtractedJson = null;
        _ieDialogHistory.Clear();
        _isIEDialogMode = false;
        UpdateIEUI();
        _ = AppendLineAsync("[IE] 已清除提取结果，可以重新提取");
    }

    /// <summary>
    /// 文档类型下拉菜单改变事件
    /// </summary>
    private void CmbIETemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string templateId)
        {
            _ieSelectedTemplateId = templateId;
            var template = IETemplates.GetTemplate(templateId);
            _ = AppendLineAsync($"[IE] 已选择：{template.Name}");
        }
    }

    // FormatFileSize 方法已在 MainWindow.QuickQA.cs 中定义，无需重复
}
