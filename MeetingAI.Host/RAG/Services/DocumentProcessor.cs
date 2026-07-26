using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using Docnet.Core;
using Docnet.Core.Models;
using MeetingAI.Host.RAG.Models;
using Tesseract;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MeetingAI.Host.RAG.Services;

/// <summary>
/// 文档处理器：支持 TXT、DOCX、PPTX、PDF 和图片 OCR，并保留来源页码。
/// </summary>
public class DocumentProcessor : IDisposable
{
    private readonly string _tesseractDataPath;
    private readonly IDocLib? _docLib;

    public DocumentProcessor(string tesseractDataPath)
    {
        _tesseractDataPath = tesseractDataPath;
        try { _docLib = DocLib.Instance; }
        catch (Exception ex) { Console.WriteLine($"[DocumentProcessor] Docnet 初始化失败: {ex.Message}"); }
    }

    public async Task<ExtractedDocument> ExtractAsync(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("文件不存在", filePath);

        return info.Extension.ToLowerInvariant() switch
        {
            ".txt" => await ExtractTxtAsync(filePath, info),
            ".docx" => ExtractDocx(filePath, info),
            ".pptx" => ExtractPptx(filePath, info),
            ".pdf" => ExtractPdf(filePath, info),
            ".jpg" or ".jpeg" or ".png" or ".bmp" => ExtractImage(filePath, info),
            _ => throw new NotSupportedException($"不支持的文件类型: {info.Extension}")
        };
    }

    private static async Task<ExtractedDocument> ExtractTxtAsync(string path, FileInfo info)
        => SinglePage(info, "txt", await File.ReadAllTextAsync(path, Encoding.UTF8), false);

    private static ExtractedDocument ExtractDocx(string path, FileInfo info)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var lines = doc.MainDocumentPart?.Document?.Body?
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? Enumerable.Empty<string>();
        return SinglePage(info, "docx", string.Join(Environment.NewLine, lines), false);
    }

    private ExtractedDocument ExtractPptx(string path, FileInfo info)
    {
        using var doc = PresentationDocument.Open(path, false);
        var part = doc.PresentationPart ?? throw new InvalidDataException("PPTX 缺少 PresentationPart");
        var ids = part.Presentation.SlideIdList?.Elements<P.SlideId>().ToList() ?? new();
        var pages = new List<ExtractedDocumentPage>();

        for (var i = 0; i < ids.Count; i++)
        {
            var relId = ids[i].RelationshipId?.Value
                ?? throw new InvalidDataException($"第 {i + 1} 张幻灯片缺少关系 ID");
            pages.Add(ExtractSlide((SlidePart)part.GetPartById(relId), i + 1));
        }

        return new ExtractedDocument
        {
            FileName = info.Name,
            FileType = "pptx",
            FileSize = info.Length,
            PageCount = pages.Count,
            UsedOcr = pages.Any(page => page.UsedOcr),
            Pages = pages,
            Content = string.Join(Environment.NewLine + Environment.NewLine, pages.Select(page => page.Content))
        };
    }

    private ExtractedDocumentPage ExtractSlide(SlidePart slidePart, int pageNumber)
    {
        var shapes = slidePart.Slide.Descendants<P.Shape>().ToList();
        var title = shapes
            .Where(shape =>
            {
                var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
                    .GetFirstChild<P.PlaceholderShape>()?.Type?.InnerText;
                return placeholder is "title" or "ctrTitle";
            })
            .Select(ShapeText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;

        var body = shapes.Select(ShapeText)
            .Where(text => !string.IsNullOrWhiteSpace(text) && !string.Equals(text, title, StringComparison.Ordinal))
            .ToList();

        var tables = new List<string>();
        foreach (var table in slidePart.Slide.Descendants<A.Table>())
        foreach (var row in table.Elements<A.TableRow>())
        {
            var cells = row.Elements<A.TableCell>()
                .Select(cell => string.Join(" ", cell.Descendants<A.Text>().Select(t => t.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))));
            tables.Add(string.Join(" | ", cells));
        }

        var notes = slidePart.NotesSlidePart?.NotesSlide?.Descendants<A.Text>()
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text) &&
                           !text.Contains("Click to edit Master text styles", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new();

        var ocr = new List<string>();
        var imageCount = 0;
        foreach (var image in slidePart.ImageParts)
        {
            imageCount++;
            var text = OcrImagePart(image);
            if (!string.IsNullOrWhiteSpace(text)) ocr.Add(text.Trim());
        }

        var result = new StringBuilder($"[幻灯片 {pageNumber}]");
        if (!string.IsNullOrWhiteSpace(title)) result.AppendLine().Append("标题：").Append(title);
        if (body.Count > 0) result.AppendLine().Append(string.Join(Environment.NewLine, body));
        if (tables.Count > 0) result.AppendLine().AppendLine("表格：").Append(string.Join(Environment.NewLine, tables));
        if (notes.Count > 0) result.AppendLine().AppendLine("演讲者备注：").Append(string.Join(Environment.NewLine, notes));
        if (ocr.Count > 0) result.AppendLine().AppendLine("图片 OCR：").Append(string.Join(Environment.NewLine, ocr));

        return new ExtractedDocumentPage
        {
            PageNumber = pageNumber,
            Title = title,
            Content = result.ToString(),
            UsedOcr = ocr.Count > 0,
            ImageCount = imageCount
        };
    }

    private static string ShapeText(P.Shape shape) => string.Join(" ", shape.Descendants<A.Text>()
        .Select(text => text.Text?.Trim()).Where(text => !string.IsNullOrWhiteSpace(text)));

    private string OcrImagePart(ImagePart image)
    {
        try
        {
            using var source = image.GetStream(FileMode.Open, FileAccess.Read);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            using var engine = new TesseractEngine(_tesseractDataPath, "chi_sim+eng", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(memory.ToArray());
            using var page = engine.Process(pix);
            return page.GetText();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] PPT 图片 OCR 失败: {ex.Message}");
            return string.Empty;
        }
    }

    private ExtractedDocument ExtractPdf(string path, FileInfo info)
    {
        if (_docLib == null) throw new InvalidOperationException("Docnet 库未初始化");
        var pages = new List<ExtractedDocumentPage>();
        using var doc = _docLib.GetDocReader(path, new PageDimensions(1080, 1920));
        var count = doc.GetPageCount();
        for (var i = 0; i < count; i++)
        {
            using var reader = doc.GetPageReader(i);
            var text = reader.GetText();
            var useOcr = string.IsNullOrWhiteSpace(text) || text.Length < 50;
            if (useOcr) text = OcrPdfPage(reader);
            pages.Add(new ExtractedDocumentPage { PageNumber = i + 1, Content = text, UsedOcr = useOcr });
        }

        return new ExtractedDocument
        {
            FileName = info.Name,
            FileType = "pdf",
            FileSize = info.Length,
            PageCount = count,
            UsedOcr = pages.Any(page => page.UsedOcr),
            Pages = pages,
            Content = string.Join(Environment.NewLine, pages.Select(page => page.Content))
        };
    }

    private string OcrPdfPage(Docnet.Core.Readers.IPageReader reader)
    {
        try
        {
            using var engine = new TesseractEngine(_tesseractDataPath, "chi_sim+eng", EngineMode.Default);
            using var pix = Pix.LoadTiffFromMemory(reader.GetImage());
            using var page = engine.Process(pix);
            return page.GetText();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] PDF OCR 失败: {ex.Message}");
            return string.Empty;
        }
    }

    private ExtractedDocument ExtractImage(string path, FileInfo info)
    {
        string text;
        try
        {
            using var engine = new TesseractEngine(_tesseractDataPath, "chi_sim+eng", EngineMode.Default);
            using var image = Pix.LoadFromFile(path);
            using var page = engine.Process(image);
            text = page.GetText();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentProcessor] 图片 OCR 失败: {ex.Message}");
            text = $"[OCR 识别失败: {ex.Message}]";
        }

        return SinglePage(info, info.Extension.TrimStart('.').ToLowerInvariant(), text, true);
    }

    private static ExtractedDocument SinglePage(FileInfo info, string type, string content, bool usedOcr)
    {
        var page = new ExtractedDocumentPage { PageNumber = 1, Content = content, UsedOcr = usedOcr };
        return new ExtractedDocument
        {
            FileName = info.Name,
            Content = content,
            FileType = type,
            FileSize = info.Length,
            PageCount = 1,
            UsedOcr = usedOcr,
            Pages = new List<ExtractedDocumentPage> { page }
        };
    }

    public void Dispose() { }
}
