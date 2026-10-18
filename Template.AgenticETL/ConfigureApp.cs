using Serilog;
using Template.AgenticETL.Endpoints;

namespace Template.AgenticETL;

public static class ConfigureApp
{
    public static async Task ConfigureAsync(this WebApplication app)
    {
        // Configure the middleware pipeline
        app.UseExceptionHandler();
        app.UseSerilogRequestLogging();
        app.UseHealthChecks("/health");

        //app.UseHttpsRedirection();

        // TODO: Add Authentication and Authorization middleware if needed

        app.MapEndpoints();
    }
}
