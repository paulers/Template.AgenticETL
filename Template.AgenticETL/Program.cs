using Serilog;
using Template.AgenticETL;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();
Log.Information("Starting AgenticETL...");

try
{
    var builder = WebApplication.CreateBuilder(args);
    // Add services
    builder.AddAllServices();

    await using var app = builder.Build();

    await app.ConfigureAsync();
    await app.RunAsync();

    return 0;
}
catch (HostAbortedException ex)
{
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;

}
finally
{
    Log.Information("Shutting down AgenticETL...");
    Log.CloseAndFlush();
}