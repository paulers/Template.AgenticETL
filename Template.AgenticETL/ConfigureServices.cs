using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Queues;
using Foundatio.Serializer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;
using Template.AgenticETL.Models;
using Template.AgenticETL.Models.ConfigBindings;
using Template.AgenticETL.Services;

namespace Template.AgenticETL;

public static class ConfigureServices
{
    private const int RequestSizeLimitBytes = 100 * 1024 * 1024; // 100 MB
    private const int TextLengthLimit = 524_288; // 512 KB

    public static void AddAllServices(this WebApplicationBuilder builder)
    {
        // Configure form options to allow large file uploads
        // and larger text bodies (for example JSON)
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartHeadersLengthLimit = TextLengthLimit;
            options.MultipartBodyLengthLimit = RequestSizeLimitBytes;
            options.ValueLengthLimit = TextLengthLimit;
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, config) => config
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console());

        // Add API versioning support
        builder.Services.AddApiVersioning();

        builder.Services.AddHealthChecks();

        builder.Services.AddProblemDetails();

        builder.AddInfrastructureServices();
    }

    private static void AddInfrastructureServices(this WebApplicationBuilder builder)
    {
        #region Redis Connection Multiplexer
        // Get the options from the configuration and bind them to the RedisOptions class
        var redisOptions = builder.Configuration.GetSection("Redis").Get<RedisOptions>();
        if (redisOptions is null)
            throw new InvalidOperationException("Redis configuration section is missing.");

        // Create a connection multiplexer for Redis using the options
        var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints =
            {
                { redisOptions.Host, redisOptions.Port }
            },
            KeepAlive = 180,
            Password = redisOptions.Password,
            Ssl = redisOptions.Ssl
        });

        // Register the connection multiplexer as a singleton service
        builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        #endregion

        #region Redis Cache
        // Create a new ICacheClient and use the existing ConnectionMultiplexer
        builder.Services.AddSingleton<ICacheClient>(sp =>
            new RedisCacheClient(new RedisCacheClientOptions
            {
                ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>(),
                Serializer = new SystemTextJsonSerializer(),
                Database = 0
            }));
        #endregion

        #region Redis Queue
        builder.Services.AddOptions<DocumentProcessingOptions>()
            .BindConfiguration("DocumentProcessing")
            .Validate(options => options.WorkItemTimeout > TimeSpan.Zero,
                "DocumentProcessing:WorkItemTimeout must be positive.")
            .Validate(options => options.LockRenewalInterval > TimeSpan.Zero
                && options.LockRenewalInterval <= TimeSpan.FromMilliseconds(uint.MaxValue - 1)
                && options.LockRenewalInterval < options.WorkItemTimeout,
                "DocumentProcessing:LockRenewalInterval must be positive, supported by PeriodicTimer, and shorter than WorkItemTimeout.")
            .ValidateOnStart();

        // Create a new queue with the DocumentWorkItem type and use the existing ConnectionMultiplexer
        // Retry 5 times before dropping into DLQ, with a 30 second delay between retries,
        // and a work item timeout of 10 minutes
        builder.Services.AddSingleton<IQueue<DocumentWorkItem>>(sp =>
            new RedisQueue<DocumentWorkItem>(new RedisQueueOptions<DocumentWorkItem>
            {
                ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>(),
                Serializer = new SystemTextJsonSerializer(),
                Name = "document-work-items",
                WorkItemTimeout = sp.GetRequiredService<IOptions<DocumentProcessingOptions>>().Value.WorkItemTimeout,
                RetryDelay = TimeSpan.FromSeconds(30),
                Retries = 5,
                Database = 0
            })
        );
        #endregion

        #region Document Processing Job
        builder.Services.AddJob<DocumentProcessingQueueJob>(options =>
        {
            options.Name("DocumentProcessingQueueJob");
            options.InstanceCount(5);
        });
        #endregion

        builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();

        builder.Services.AddOptions<OpenAIOptions>()
            .BindConfiguration("OpenAI")
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
