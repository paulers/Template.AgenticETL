using Microsoft.Agents.AI.Workflows;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Template.AgenticETL.Workflow;

public sealed partial class DocumentParserExecutor(string id) : Executor<string, string[]>(id)
{
    public override ValueTask<string[]> HandleAsync(string base64Payload, IWorkflowContext context, CancellationToken ct)
    {
        // The worker requests cancellation on host shutdown or queue lease renewal failure.
        // These checkpoints stop work cooperatively: they do nothing normally and throw
        // OperationCanceledException when canceled, unwinding and disposing using scopes.
        // PdfPig's synchronous calls cannot be interrupted, so check around that work.
        ct.ThrowIfCancellationRequested();
        if (!TryDecodeDocument(base64Payload, out var documentBytes)) throw new InvalidOperationException("Invalid base64 payload");

        var binaryData = BinaryData.FromBytes(documentBytes);

        List<string> parsedPages = new();
        using (PdfDocument document = PdfDocument.Open(binaryData))
        {
            foreach (Page page in document.GetPages())
            {
                // Observe cancellation between pages before extracting more text.
                // GetPages may already have parsed this page while advancing the enumerator;
                // neither that parsing nor an in-progress text extraction can be interrupted.
                ct.ThrowIfCancellationRequested();
                string text = ContentOrderTextExtractor.GetText(page);
                parsedPages.Add(text);
            }
        }

        // Cancellation may have arrived during the final page. Stop before reporting
        // an empty-text error or returning results for the next workflow stage.
        ct.ThrowIfCancellationRequested();
        if (!parsedPages.Any(text => !string.IsNullOrWhiteSpace(text)))
            throw new InvalidOperationException("No extractable text was found in the PDF. Scanned or image-only documents require OCR before processing.");

        return ValueTask.FromResult(parsedPages.ToArray());
    }

    private static bool TryDecodeDocument(string base64Payload, out byte[] documentBytes)
    {
        documentBytes = [];
        if (string.IsNullOrWhiteSpace(base64Payload)) return false;
        
        try
        {
            documentBytes = Convert.FromBase64String(base64Payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
