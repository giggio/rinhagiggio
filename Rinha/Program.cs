using Microsoft.AspNetCore.Http.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging(opt => opt.AddSimpleConsole(options => options.TimestampFormat = "[HH:mm:ss:fff] "));
builder.Services.Configure<DbConfig>(dbConfig => dbConfig.ConnectionString = builder.Configuration.GetConnectionString("Rinha"));
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<CacheQueue>();
builder.Services.AddSingleton<IBackgroundTaskQueue, NewPessoasBackgroundTaskQueue>();
var cacheConfig = builder.Configuration.GetSection("Cache");
builder.Services.Configure<CacheOptions>(cacheConfig);
var isLeader = cacheConfig.GetValue<bool>("Leader");
if (isLeader)
{
    builder.Services.AddGrpc();
    builder.Services.AddSingleton<Db>();
    builder.Services.AddHostedService<SaveToDbQueueWorker>();
}
else
{
    builder.Services.AddSingleton<PeerCacheClient>();
    builder.Services.AddHostedService<CacheStreamingWorker>();
}

#if DEBUG
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SupportNonNullableReferenceTypes();
    o.MapType<DateOnly>(() => new()
    {
        Type = "string",
        Example = new Microsoft.OpenApi.Any.OpenApiString("2023-05-20")
    });
    o.EnableAnnotations();
});
#endif

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new DateOnlyJsonConverter());
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PessoaJsonContext.Default);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            app.Logger.AppError(context.Request.GetDisplayUrl(), ex.ToString());
            throw;
        }
    });
#if DEBUG
    app.UseSwagger();
    app.UseSwaggerUI();
#endif
}

app.MapPessoas(isLeader);
#if DEBUG
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
#endif
app.MapHealthChecks("/healthz");
{
    var logger = app.Services.GetRequiredService<ILogger<AppLogs>>();
    logger.IsLeader(isLeader);
    if (isLeader)
    {
        // get all existing, add to cache and tell peer
        var db = app.Services.GetRequiredService<Db>();
        var pessoas = new List<Pessoa>();
        await foreach (var pessoa in db.GetAllAsync(CancellationToken.None))
            pessoas.Add(pessoa);
        if (pessoas.Count > 0)
        {
            CacheData.AddRange(pessoas);
            var cacheQueue = app.Services.GetRequiredService<CacheQueue>();
            await cacheQueue.EnqueueAsync(pessoas, CancellationToken.None);
            await CacheData.GetPessoaAsync(pessoas[^1].Id, CancellationToken.None); // start cache
        }

        CacheData.SetQueue(app.Services.GetRequiredService<IBackgroundTaskQueue>());
        app.MapGrpcService<CacheService>();
    }
    var configEndpoints = app.Configuration.GetSection("Kestrel:Endpoints");
    var httpEndpoint = configEndpoints?.GetValue<string>("Http:Url");
    var grpcEndpoint = configEndpoints?.GetValue<string>("gRPC:Url");
    if (httpEndpoint != null && grpcEndpoint != null)
    {
        logger.ServerAddresses(httpEndpoint, grpcEndpoint);
    }
    else
    {
        logger.ServerAddressesNotFound();
        throw new Exception("Missing server addresses.");
    }
}
app.Run();

