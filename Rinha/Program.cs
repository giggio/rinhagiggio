using Microsoft.AspNetCore.Http.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging(opt => opt.AddSimpleConsole(options => options.TimestampFormat = "[HH:mm:ss:fff] "));
builder.Services.AddPooledDbContextFactory<RinhaContext>(options =>
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
    .UseNpgsql(builder.Configuration.GetConnectionString("Rinha"),
        o => o.ExecutionStrategy(d => new Microsoft.EntityFrameworkCore.Storage.NonRetryingExecutionStrategy(d)))
        .EnableThreadSafetyChecks(!builder.Environment.IsProduction()));
builder.Services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<RinhaContext>>().CreateDbContext());
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<PeerCacheClient>();
builder.Services.AddGrpc();
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.AddHostedService<SaveToDbQueueWorker>();
builder.Services.AddSingleton<IBackgroundTaskQueue, NewPessoasBackgroundTaskQueue>();

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

app.MapPessoas();
#if DEBUG
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
#endif
app.MapHealthChecks("/healthz");
app.MapGrpcService<CacheService>();

using (var context = app.Services.GetRequiredService<IDbContextFactory<RinhaContext>>().CreateDbContext())
{
    await CacheData.AddRangeAsync(context.GetAll(), CancellationToken.None);
}
{
    var configEndpoints = app.Configuration.GetSection("Kestrel:Endpoints");
    var httpEndpoint = configEndpoints?.GetValue<string>("Http:Url");
    var grpcEndpoint = configEndpoints?.GetValue<string>("gRPC:Url");
    if (httpEndpoint != null && grpcEndpoint != null)
    {
        app.Logger.ServerAddresses(httpEndpoint, grpcEndpoint);
    }
    else
    {
        app.Logger.ServerAddressesNotFound();
        throw new Exception("Missing server addresses.");
    }
}
app.Run();

