using Microsoft.AspNetCore.Http.HttpResults;
using Template.AgenticETL.Models;
using Template.AgenticETL.Services;

namespace Template.AgenticETL.Endpoints;

public class GetDocumentStatusEndpoint : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/{recordId}/status", Handle)
            .WithSummary("Gets the processing status of a document");
    }

    private static async Task<Results<Ok<DocumentRecord>, NotFound>> Handle(
        string recordId,
        IDocumentRepository repository,
        CancellationToken ct)
    {
        var record = await repository.GetDocumentById(recordId, ct);
        if (record is null) return TypedResults.NotFound();

        return TypedResults.Ok(record);
    }
}
