using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using Docnet.Core;
using Docnet.Core.Models;
using Tesseract;
using MeetingAI.Host.RAG.Models;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// 文档处理器：支持 TXT, DOCX, PDF, 图片(OCR)
/// </summary>
public class DocumentProcessor : IDisposable
{
    private readonly string _tesseractDataPath;
    private readonly IDocLib? _docLib;

    public DocumentProcessor(string tesseractDataPath)
    {
        _tesseractDataPath = tesseractDataPath;

        // 初始化 Docnet 库
        try
        {
            _docLib = DocLib.Instance;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] Docnet 初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据文件类型自动提取文档内容
    /// </summary>
    public async Task<ExtractedDocument> ExtractAsync(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("文件不存在", filePath);
        }

        var extension = fileInfo.Extension.ToLowerInvariant();
        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;

        return extension switch
        {
            ".txt" => await ExtractFromTxtAsync(filePath, fileName, fileSize),
            ".docx" => await ExtractFromDocxAsync(filePath, fileName, fileSize),
            ".pdf" => await ExtractFromPdfAsync(filePath, fileName, fileSize),
            ".jpg" or ".jpeg" or ".png" or ".bmp" => await ExtractFromImageAsync(filePath, fileName, fileSize),
            _ => throw new NotSupportedException($"不支持的文件类型: {extension}")
        };
    }

    /// <summary>
    /// 提取 TXT 文件内容
    /// </summary>
    private async Task<ExtractedDocument> ExtractFromTxtAsync(string filePath, string fileName, long fileSize)
    {
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);

        return new ExtractedDocument
        {
            FileName = fileName,
            Content = content,
            FileType = "txt",
            FileSize = fileSize,
            PageCount = 0,
            UsedOcr = false
        };
    }

    /// <summary>
    /// 提取 DOCX 文件内容（使用 Open XML SDK）
    /// </summary>
    private Task<ExtractedDocument> ExtractFromDocxAsync(string filePath, string fileName, long fileSize)
    {
        var content = new StringBuilder();

        using (var doc = WordprocessingDocument.Open(filePath, false))
        {
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
            {
                // 提取所有段落文本
                foreach (var paragraph in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                {
                    var text = paragraph.InnerText;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        content.AppendLine(text);
                    }
                }
            }
        }

        return Task.FromResult(new ExtractedDocument
        {
            FileName = fileName,
            Content = content.ToString(),
            FileType = "docx",
            FileSize = fileSize,
            PageCount = 0,
            UsedOcr = false
        });
    }

    /// <summary>
    /// 提取 PDF 文件内容（使用 Docnet.Core + Tesseract OCR）
    /// </summary>
    private Task<ExtractedDocument> ExtractFromPdfAsync(string filePath, string fileName, long fileSize)
    {
        if (_docLib == null)
        {
            throw new InvalidOperationException("Docnet 库未初始化");
        }

        var content = new StringBuilder();
        var usedOcr = false;
        var pageCount = 0;

        using (var docReader = _docLib.GetDocReader(filePath, new PageDimensions(1080, 1920)))
        {
            pageCount = docReader.GetPageCount();

            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    // 先尝试提取文本
                    var pageText = pageReader.GetText();

                    // 如果文本内容太少（可能是扫描版），使用 OCR
                    if (string.IsNullOrWhiteSpace(pageText) || pageText.Length < 50)
                    {
                        usedOcr = true;
                        var ocrText = PerformOcrOnPdfPage(pageReader);
                        content.AppendLine(ocrText);
                    }
                    else
                    {
                        content.AppendLine(pageText);
                    }
                }
            }
        }

        return Task.FromResult(new ExtractedDocument
        {
            FileName = fileName,
            Content = content.ToString(),
            FileType = "pdf",
            FileSize = fileSize,
            PageCount = pageCount,
            UsedOcr = usedOcr
        });
    }

    /// <summary>
    /// 对 PDF 页面执行 OCR
    /// </summary>
    private string PerformOcrOnPdfPage(Docnet.Core.Readers.IPageReader pageReader)
    {
        try
        {
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();

            using (var engine = new TesseractEngine(_tesseractDataPath, "chi_sim+eng", EngineMode.Default))
            {
                using (var pix = Pix.LoadTiffFromMemory(rawBytes))
                {
                    using (var page = engine.Process(pix))
                    {
                        return page.GetText();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] PDF OCR 失败: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 提取图片文件内容（使用 Tesseract OCR）
    /// </summary>
    private Task<ExtractedDocument> ExtractFromImageAsync(string filePath, string fileName, long fileSize)
    {
        var content = string.Empty;

        try
        {
            using (var engine = new TesseractEngine(_tesseractDataPath, "chi_sim+eng", EngineMode.Default))
            {
                using (var img = Pix.LoadFromFile(filePath))
                {
                    using (var page = engine.Process(img))
                    {
                        content = page.GetText();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] 图片 OCR 失败: {ex.Message}");
            content = $"[OCR 识别失败: {ex.Message}]";
        }

        return Task.FromResult(new ExtractedDocument
        {
            FileName = fileName,
            Content = content,
            FileType = Path.GetExtension(filePath).TrimStart('.').ToLower(),
            FileSize = fileSize,
            PageCount = 1,
            UsedOcr = true
        });
    }

    public void Dispose()
    {
        // Docnet 使用单例模式，不需要手动释放
    }
}
