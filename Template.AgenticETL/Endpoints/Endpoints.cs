using Asp.Versioning;

namespace Template.AgenticETL.Endpoints;

public interface IEndpoint
{
    static abstract void Map(IEndpointRouteBuilder app);
}

public static class Endpoints
{
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        var apiVersionSet = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        var versionedGroup = app.MapGroup("/api/v{version:apiVersion}")
            .WithApiVersionSet(apiVersionSet)
            .WithTags("API");

        MapEndpoints(versionedGroup);

        return app;
    }

    private static void MapEndpoints(this RouteGroupBuilder routeGroupBuilder)
    {
        var endpoints = routeGroupBuilder
            .MapGroup("/documents")
            .WithTags("Documents");

        endpoints.MapEndpoint<UploadDocumentEndpoint>();
        endpoints.MapEndpoint<GetDocumentStatusEndpoint>();
        endpoints.MapEndpoint<RemoveDocumentEndpoint>();
    }

    private static IEndpointRouteBuilder MapEndpoint<TEndpoint>(this IEndpointRouteBuilder routeBuilder) where TEndpoint : IEndpoint
    {
        TEndpoint.Map(routeBuilder);
        return routeBuilder;
    }
}
