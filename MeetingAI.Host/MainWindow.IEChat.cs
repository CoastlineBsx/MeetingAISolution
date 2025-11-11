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
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MeetingAI.Host.Contracts;
using MeetingAI.Host.Contracts.Messages;
using MeetingAI.Host.Models;

namespace MeetingAI.Host;

/// <summary>
/// IE Chat模式（信息提取聊天模式）核心逻辑
/// 与主页IE模式完全隔离
/// </summary>
public sealed partial class MainWindow
{
    // ========== IE Chat 模式常量 ==========
    private const int MAX_IE_CHAT_TOKENS = 50000;  // 最大 50K tokens
    private const int MAX_IE_CHAT_TURNS = 10;      // 最多 10 轮对话

    /// <summary>
    /// Initialize IE Chat page
    /// </summary>
    private void InitializeIEChatPage()
    {
        // Populate template combo box
        DispatcherQueue.TryEnqueue(() =>
        {
            CmbIEChatTemplate.Items.Clear();
            foreach (var template in IETemplates.AllTemplates)
            {
                var item = new ComboBoxItem
                {
                    Content = template.Name,
                    Tag = template.Id
                };
                CmbIEChatTemplate.Items.Add(item);
            }

            // Enable upload button when Granite is loaded
            UpdateIEChatUI();
        });
    }

    /// <summary>
    /// Upload document button click event
    /// </summary>
    private async void BtnIEChatUpload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create file picker (support TXT, DOCX, PDF)
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".pdf");

            // Get window handle and initialize picker
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            // Select file
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            // Add system message to chat
            AddIEChatSystemMessage($"Loading document: {file.Name}");

            // Use existing DocumentProcessor to parse document
            if (_documentProcessor == null)
            {
                var baseDir = AppContext.BaseDirectory;
                var tesseractDataPath = Path.Combine(baseDir, "tessdata");
                _documentProcessor = new RAG.Services.DocumentProcessor(tesseractDataPath);
            }

            // Extract document content
            var extracted = await _documentProcessor.ExtractAsync(file.Path);

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                AddIEChatSystemMessage("Warning: Document content is empty");
                return;
            }

            AddIEChatSystemMessage($"Extraction complete, content length: {extracted.Content.Length} characters");

            // Calculate token count
            AddIEChatSystemMessage("Calculating token count...");
            int tokenCount = await CountTokensAsync(extracted.Content);

            if (tokenCount <= 0)
            {
                AddIEChatSystemMessage("Warning: Token calculation failed, cannot load document");
                return;
            }

            AddIEChatSystemMessage($"Token count: {tokenCount}");

            // Check if exceeds limit
            if (tokenCount > MAX_IE_CHAT_TOKENS)
            {
                AddIEChatSystemMessage($"Warning: Document too large ({tokenCount} tokens > {MAX_IE_CHAT_TOKENS} tokens)");
                AddIEChatSystemMessage("Suggestion: Use RAG mode for large documents");
                return;
            }

            // Load success, save document info
            _ieChatDocumentContent = extracted.Content;
            _ieChatDocumentName = extracted.FileName;
            _ieChatDocumentSize = extracted.FileSize;
            _ieChatTokenCount = tokenCount;
            _ieChatExtractedJson = null;

            // Auto-detect document type
            AddIEChatSystemMessage("Detecting document type...");
            await DetectIEChatDocumentTypeAsync();

            // Update UI
            UpdateIEChatUI();

            AddIEChatSystemMessage($"Document loaded successfully: {_ieChatDocumentName}");
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Failed to load document - {ex.Message}");
        }
    }

    /// <summary>
    /// Auto-detect document type
    /// </summary>
    private async Task DetectIEChatDocumentTypeAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_ieChatDocumentContent))
            {
                return;
            }

            await EnsurePipeAsync();

            // Set detection state
            _isChatDetecting = true;
            _ieChatDetectionBuffer = new StringBuilder();

            // Take first 1000 characters for detection
            string contentSample = _ieChatDocumentContent.Length > 1000
                ? _ieChatDocumentContent.Substring(0, 1000)
                : _ieChatDocumentContent;

            // Build detection prompt
            var detectionPrompt = $@"Please quickly determine what type of document the following belongs to. Output only the type ID.

Document content (first 1000 characters):
{contentSample}

Available types:
- resume (CV/Job application)
- contract (Contract/Agreement)
- invoice (Invoice/Receipt)
- news (News/Report)
- paper (Academic paper)
- manual (Product manual)
- legal (Legal document)
- financial (Financial statement)
- email (Email/Letter)
- meeting (Meeting minutes)
- general (General/Other)

Output only one type ID, no other text.";

            // Use Granite single-turn mode
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
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Warning: Type detection failed - {ex.Message}, using general template");
            _ieChatSelectedTemplateId = "general";
            _isChatDetecting = false;
            _ieChatDetectionBuffer = null;
        }
    }

    /// <summary>
    /// Handle document type detection result
    /// </summary>
    private async Task HandleIEChatDetectionResult(string typeId)
    {
        try
        {
            // Clean typeId
            typeId = typeId.Trim().Trim('"', '\'').ToLower();

            // Validate typeId
            var template = IETemplates.AllTemplates.FirstOrDefault(t => t.Id == typeId);
            if (template == null)
            {
                // Invalid type, use general template
                typeId = "general";
                template = IETemplates.GetTemplate(typeId);
                AddIEChatSystemMessage($"Warning: Cannot recognize type (returned: {typeId}), using general template");
            }
            else
            {
                AddIEChatSystemMessage($"Detected as {template.Name}, auto-selected corresponding field set");
            }

            // Save selected template ID
            _ieChatSelectedTemplateId = typeId;

            // Update dropdown selection
            DispatcherQueue.TryEnqueue(() =>
            {
                for (int i = 0; i < CmbIEChatTemplate.Items.Count; i++)
                {
                    if (CmbIEChatTemplate.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == typeId)
                    {
                        CmbIEChatTemplate.SelectedIndex = i;
                        break;
                    }
                }

                // Update UI state
                UpdateIEChatUI();
            });
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Failed to process detection result - {ex.Message}");
            _ieChatSelectedTemplateId = "general";
        }
    }

    /// <summary>
    /// Extract button click event
    /// </summary>
    private async void BtnIEChatExtract_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ieChatDocumentContent))
            {
                AddIEChatSystemMessage("Warning: Please upload a document first");
                return;
            }

            if (string.IsNullOrEmpty(_ieChatSelectedTemplateId))
            {
                AddIEChatSystemMessage("Warning: Please select a document type");
                return;
            }

            await EnsurePipeAsync();

            // Set extraction state
            _isChatExtracting = true;
            _ieChatExtractionBuffer = new StringBuilder();

            // Add user message
            AddIEChatUserMessage("Extract structured information from this document");

            // Add AI message (processing)
            var aiMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                IsGenerating = true,
                Timestamp = DateTime.Now
            };
            _ieChatHistory.Add(aiMsg);
            _ieChatStreamingMessage = aiMsg;

            // Scroll to bottom
            ScrollIEChatToBottom();

            // Get selected template
            var template = IETemplates.GetTemplate(_ieChatSelectedTemplateId);

            // Build extraction prompt
            string extractionPrompt = BuildIEChatExtractionPrompt(template, _ieChatDocumentContent);

            // Use Granite single-turn mode
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

            // Update UI state
            UpdateIEChatUI();
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Extraction failed - {ex.Message}");
            _isChatExtracting = false;
            _ieChatExtractionBuffer = null;
        }
    }

    /// <summary>
    /// Build IE extraction prompt
    /// </summary>
    private string BuildIEChatExtractionPrompt(DocumentTypeTemplate template, string documentContent)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a professional information extraction assistant. Please carefully read the document and strictly extract the specified fields in JSON format.");
        sb.AppendLine();
        sb.AppendLine("=== Document Content ===");
        sb.AppendLine(documentContent);
        sb.AppendLine("=== Document End ===");
        sb.AppendLine();
        sb.AppendLine("Please extract the following fields:");
        sb.AppendLine();

        // Dynamically generate field descriptions
        foreach (var field in template.Fields)
        {
            if (field.Type == "array" && field.SubFields != null)
            {
                sb.AppendLine($"- {field.Key} (array): {field.Description}");
                sb.AppendLine($"  Each item contains:");
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
        sb.AppendLine("Output requirements:");
        sb.AppendLine("1. Must output valid JSON format");
        sb.AppendLine("2. Output only JSON, no explanatory text");
        sb.AppendLine("3. If a field cannot be found in the document, set it to null");
        sb.AppendLine("4. For array type fields, if there is no content, set to empty array []");
        sb.AppendLine("5. Ensure all fields are included in the output");
        sb.AppendLine();
        sb.AppendLine("Example output format:");
        sb.AppendLine(template.ExampleJson);

        return sb.ToString();
    }

    /// <summary>
    /// Handle IE extraction result
    /// </summary>
    public async Task HandleIEChatExtractionResult(string aiResponse)
    {
        try
        {
            _isChatExtracting = false;

            // Extract JSON
            string jsonStr = ExtractJSON(aiResponse);

            // Validate JSON format
            try
            {
                var testParse = JsonDocument.Parse(jsonStr);
                testParse.Dispose();

                _ieChatExtractedJson = jsonStr;

                // Format and display
                string formattedResult = FormatExtractedData(jsonStr);

                // Update streaming message
                if (_ieChatStreamingMessage != null)
                {
                    _ieChatStreamingMessage.IsGenerating = false;
                    _ieChatStreamingMessage.Content = "Extraction completed successfully";
                    _ieChatStreamingMessage.JsonContent = formattedResult;
                    _ieChatStreamingMessage.JsonVisible = Visibility.Visible;
                    _ieChatStreamingMessage = null;
                }

                // Scroll to bottom
                ScrollIEChatToBottom();

                // Update UI
                UpdateIEChatUI();
            }
            catch (JsonException je)
            {
                if (_ieChatStreamingMessage != null)
                {
                    _ieChatStreamingMessage.IsGenerating = false;
                    _ieChatStreamingMessage.Content = $"Warning: JSON parsing failed - {je.Message}\n\nRaw response:\n{aiResponse}";
                    _ieChatStreamingMessage = null;
                }

                // Save raw response
                _ieChatExtractedJson = aiResponse;
            }
        }
        catch (Exception ex)
        {
            if (_ieChatStreamingMessage != null)
            {
                _ieChatStreamingMessage.IsGenerating = false;
                _ieChatStreamingMessage.Content = $"Error: Failed to process extraction result - {ex.Message}";
                _ieChatStreamingMessage = null;
            }
        }
    }

    /// <summary>
    /// Send message button click event
    /// </summary>
    private async void BtnIEChatSend_Click(object sender, RoutedEventArgs e)
    {
        await SendIEChatMessageAsync();
    }

    /// <summary>
    /// Input box key down event
    /// </summary>
    private async void TxtIEChatInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool shiftPressed = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool ctrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrlPressed && !shiftPressed)
            {
                e.Handled = true;
                await SendIEChatMessageAsync();
            }
        }
    }

    /// <summary>
    /// Send IE Chat message
    /// </summary>
    private async Task SendIEChatMessageAsync()
    {
        try
        {
            string userInput = TxtIEChatInput.Text.Trim();
            if (string.IsNullOrEmpty(userInput))
            {
                return;
            }

            // Check if extraction is completed
            if (string.IsNullOrEmpty(_ieChatExtractedJson))
            {
                AddIEChatSystemMessage("Please complete information extraction first");
                return;
            }

            // Clear input box
            TxtIEChatInput.Text = "";

            // Add user message
            AddIEChatUserMessage(userInput);

            // Add AI message (streaming)
            var aiMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                IsGenerating = true,
                Timestamp = DateTime.Now
            };
            _ieChatHistory.Add(aiMsg);
            _ieChatStreamingMessage = aiMsg;

            // Scroll to bottom
            ScrollIEChatToBottom();

            // Build dialog prompt
            string dialogPrompt = BuildIEChatDialogPrompt(userInput);

            // Use Granite single-turn mode
            string systemMessage = "You are a helpful assistant. Answer questions based on the provided extracted data and document context.";
            string fullPrompt =
                $"<|start_of_role|>system<|end_of_role|>{systemMessage}<|end_of_text|>" +
                $"<|start_of_role|>user<|end_of_role|>{dialogPrompt}<|end_of_text|>" +
                $"<|start_of_role|>assistant<|end_of_role|>";

            var cmd = new GraniteGenerateStreamCommand
            {
                prompt = fullPrompt,
                max_tokens = 1024,
                temperature = 0.7f
            };

            var json = JsonSerializer.Serialize(cmd, AppJsonContext.Utf8.GraniteGenerateStreamCommand) + "\n";
            await SendJsonAsync(json);
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Failed to send message - {ex.Message}");
        }
    }

    /// <summary>
    /// Build IE Chat dialog prompt
    /// </summary>
    private string BuildIEChatDialogPrompt(string userQuestion)
    {
        if (string.IsNullOrEmpty(_ieChatExtractedJson))
        {
            throw new InvalidOperationException("IE Chat dialog mode not initialized");
        }

        var sb = new StringBuilder();
        sb.AppendLine("Here is the structured information extracted from the document:");
        sb.AppendLine(_ieChatExtractedJson);
        sb.AppendLine();
        sb.AppendLine($"Question: {userQuestion}");

        return sb.ToString();
    }

    /// <summary>
    /// Clear history button click event
    /// </summary>
    private void BtnIEChatClear_Click(object sender, RoutedEventArgs e)
    {
        _ieChatHistory.Clear();
        _ieChatDocumentContent = null;
        _ieChatDocumentName = null;
        _ieChatDocumentSize = 0;
        _ieChatTokenCount = 0;
        _ieChatSelectedTemplateId = null;
        _ieChatExtractedJson = null;
        UpdateIEChatUI();
    }

    /// <summary>
    /// Template selection changed event
    /// </summary>
    private void CmbIEChatTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string templateId)
        {
            _ieChatSelectedTemplateId = templateId;
            var template = IETemplates.GetTemplate(templateId);
            AddIEChatSystemMessage($"Selected: {template.Name}");
        }
    }

    /// <summary>
    /// Copy message context menu click event
    /// </summary>
    private void CopyIEChatMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.DataContext is ChatMessage message)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(message.Content ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
    }

    /// <summary>
    /// Copy JSON button click event
    /// </summary>
    private void BtnCopyIEJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_ieChatExtractedJson))
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(_ieChatExtractedJson);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                AddIEChatSystemMessage("JSON copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Failed to copy - {ex.Message}");
        }
    }

    /// <summary>
    /// Export JSON button click event
    /// </summary>
    private async void BtnExportIEJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_ieChatExtractedJson))
            {
                return;
            }

            var savePicker = new FileSavePicker();
            savePicker.FileTypeChoices.Add("JSON File", new List<string> { ".json" });
            savePicker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_ieChatDocumentName)}_extracted.json";

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, _ieChatExtractedJson);
                AddIEChatSystemMessage($"Exported to: {file.Path}");
            }
        }
        catch (Exception ex)
        {
            AddIEChatSystemMessage($"Error: Export failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Update IE Chat UI state
    /// </summary>
    private void UpdateIEChatUI()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Enable upload button when Granite is loaded
            BtnIEChatUpload.IsEnabled = _isGraniteLoaded;

            // Enable template selection when document is loaded
            CmbIEChatTemplate.IsEnabled = !string.IsNullOrEmpty(_ieChatDocumentContent);

            // Enable extract button when document and template are ready
            BtnIEChatExtract.IsEnabled = !string.IsNullOrEmpty(_ieChatDocumentContent) &&
                                         !string.IsNullOrEmpty(_ieChatSelectedTemplateId);

            // Enable send button when extraction is completed
            BtnIEChatSend.IsEnabled = !string.IsNullOrEmpty(_ieChatExtractedJson);

            // Enable clear button when there's history
            BtnIEChatClear.IsEnabled = _ieChatHistory.Count > 0 || !string.IsNullOrEmpty(_ieChatDocumentContent);

            // Update document status
            if (!string.IsNullOrEmpty(_ieChatDocumentName))
            {
                string sizeStr = FormatFileSize(_ieChatDocumentSize);
                LblIEChatDocStatus.Text = $"{_ieChatDocumentName} ({sizeStr}, {_ieChatTokenCount} tokens)";
            }
            else
            {
                LblIEChatDocStatus.Text = "No document loaded";
            }
        });
    }

    /// <summary>
    /// Add system message to IE Chat
    /// </summary>
    private void AddIEChatSystemMessage(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var systemMsg = new ChatMessage
            {
                Role = "system",
                Content = message,
                Timestamp = DateTime.Now
            };
            _ieChatHistory.Add(systemMsg);
            ScrollIEChatToBottom();
        });
    }

    /// <summary>
    /// Add user message to IE Chat
    /// </summary>
    private void AddIEChatUserMessage(string message)
    {
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = message,
            Timestamp = DateTime.Now
        };
        _ieChatHistory.Add(userMsg);
        ScrollIEChatToBottom();
    }

    /// <summary>
    /// Scroll IE Chat to bottom
    /// </summary>
    private void ScrollIEChatToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ChatHistoryListIE.Items.Count > 0)
            {
                ChatHistoryListIE.ScrollIntoView(ChatHistoryListIE.Items[^1]);
            }
        });
    }
}
