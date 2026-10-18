using Microsoft.AspNetCore.Http.HttpResults;
using Template.AgenticETL.Services;

namespace Template.AgenticETL.Endpoints;

public class RemoveDocumentEndpoint : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapDelete("/{recordId}", Handle)
            .WithSummary("Removes an existing document.");
    }

    private static async Task<NoContent> Handle(
        string recordId,
        IDocumentRepository repository,
        CancellationToken ct)
    {
        await repository.RemoveDocumentById(recordId, ct);
        return TypedResults.NoContent();
    }
}
