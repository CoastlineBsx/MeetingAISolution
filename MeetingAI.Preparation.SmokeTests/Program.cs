using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MeetingAI.Host.MeetingPreparation;
using MeetingAI.Host.RAG.Services;
using MeetingAI.Host.RAG.VectorStore;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

var temp = Path.Combine(Path.GetTempPath(), "MeetingAI-Preparation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var pptx = Path.Combine(temp, "product-review.pptx");
    CreatePresentation(pptx, ("Project Phoenix", "OpenVINO and Sherpa-ONNX roadmap"),
        ("Granite Review", "ONNX Runtime integration"));

    using var processor = new DocumentProcessor(Path.Combine(temp, "tessdata"));
    var extracted = await processor.ExtractAsync(pptx);
    Assert(extracted.PageCount == 2, "PPTX page count");
    Assert(extracted.Pages[0].PageNumber == 1 && extracted.Pages[1].PageNumber == 2, "page numbering");
    Assert(extracted.Pages[0].Content.Contains("Project Phoenix"), "slide text extraction");

    var chunks = new DocumentChunker(80, 120, 10).ChunkDocument(extracted.Pages, extracted.FileName);
    Assert(chunks.Count >= 2, "page-aware chunk generation");
    Assert(chunks.All(chunk => chunk.PageNumber is 1 or 2), "chunk page metadata");

    var hotwords = new HotwordExtractor().Extract(extracted.Pages);
    Assert(hotwords.Any(word => word.Text.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase)), "hotword extraction");

    await using var database = new AsyncDatabase(Path.Combine(temp, "meeting_rag.db"));
    await database.Db.InitializeAsync();
    var preparationId = await database.Db.CreatePreparationAsync("Product Review");
    var docId = await database.Db.AddDocumentAsync("product-review.pptx", pptx, "pptx", "auto");
    await database.Db.AddChunkAsync(docId, 0, 1, "Project Phoenix", new[] { 1f, 0f });
    await database.Db.UpdateDocumentChunkCountAsync(docId, 1);
    await database.Db.AttachDocumentToPreparationAsync(preparationId, docId, 2);
    await database.Db.SaveHotwordsAsync(preparationId, hotwords);
    Assert((await database.Db.GetPreparationMaterialsAsync(preparationId)).Single().PageCount == 2, "material binding");
    Assert((await database.Db.GetHotwordsAsync(preparationId)).Count > 0, "hotword persistence");

    var attachedDocumentIds = new HashSet<long> { docId };
    for (var number = 2; number <= 5; number++)
    {
        var attachedId = await database.Db.AddDocumentAsync(
            $"reference-{number}.txt",
            Path.Combine(temp, $"reference-{number}.txt"),
            "txt",
            "auto");
        await database.Db.AddChunkAsync(
            attachedId,
            0,
            1,
            $"Bound reference {number}",
            new[] { 0f, 1f });
        await database.Db.AttachDocumentToPreparationAsync(
            preparationId,
            attachedId,
            1);
        attachedDocumentIds.Add(attachedId);
    }
    Assert(
        await database.Db.GetPreparationMaterialCountAsync(preparationId) == 5,
        "five-material limit accepts five");

    var sixthDocumentId = await database.Db.AddDocumentAsync(
        "sixth.txt",
        Path.Combine(temp, "sixth.txt"),
        "txt",
        "auto");
    var rejectedSixth = false;
    try
    {
        await database.Db.AttachDocumentToPreparationAsync(
            preparationId,
            sixthDocumentId,
            1);
    }
    catch (InvalidOperationException)
    {
        rejectedSixth = true;
    }
    Assert(rejectedSixth, "five-material limit rejects sixth");

    var otherPreparationId = await database.Db.CreatePreparationAsync("Other Meeting");
    var otherDocumentId = await database.Db.AddDocumentAsync(
        "other-meeting.txt",
        Path.Combine(temp, "other-meeting.txt"),
        "txt",
        "auto");
    await database.Db.AddChunkAsync(
        otherDocumentId,
        0,
        1,
        "Other meeting private context",
        new[] { 1f, 0f });
    await database.Db.AttachDocumentToPreparationAsync(
        otherPreparationId,
        otherDocumentId,
        1);

    var scopedResults = await database.Db.SearchPreparationAsync(
        preparationId,
        new[] { 1f, 0f },
        10);
    Assert(scopedResults.Count > 0, "scoped RAG returns bound chunks");
    Assert(
        scopedResults.All(result => attachedDocumentIds.Contains(result.DocId)),
        "scoped RAG excludes documents from other meetings");

    var preparation = (await database.Db.GetPreparationsAsync())
        .Single(item => item.PreparationId == preparationId);
    Assert(
        preparation.MaterialCount == 5 &&
        preparation.EnabledHotwordCount > 0,
        "meeting context list counts");

    Console.WriteLine("PASS: preparation extraction, five-file limit, hotwords, and scoped RAG");
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
}

static void CreatePresentation(string path, params (string Title, string Body)[] slides)
{
    using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
    var presentationPart = doc.AddPresentationPart();
    presentationPart.Presentation = new P.Presentation(new P.SlideIdList());
    uint id = 256;
    foreach (var slide in slides)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            CreateShape(2, "Title", slide.Title, P.PlaceholderValues.Title),
            CreateShape(3, "Body", slide.Body, P.PlaceholderValues.Body))));
        slidePart.Slide.Save();
        presentationPart.Presentation.SlideIdList!.Append(
            new P.SlideId { Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
    }
    presentationPart.Presentation.Save();
}

static P.Shape CreateShape(uint id, string name, string text, P.PlaceholderValues placeholder)
{
    return new P.Shape(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = id, Name = name },
            new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
            new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = placeholder })),
        new P.ShapeProperties(),
        new P.TextBody(
            new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(new A.Run(new A.RunProperties { Language = "en-US" }, new A.Text(text)))));
}

sealed class AsyncDatabase : IAsyncDisposable
{
    public AsyncDatabase(string path) => Db = new SqliteVectorDatabase(path);
    public SqliteVectorDatabase Db { get; }
    public ValueTask DisposeAsync() { Db.Dispose(); return ValueTask.CompletedTask; }
}
