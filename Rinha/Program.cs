using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging(opt => opt.AddSimpleConsole(options => options.TimestampFormat = "[HH:mm:ss:fff] "));
builder.Services.AddDbContextPool<RinhaContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Rinha"),
    o => o.ExecutionStrategy(d => new Microsoft.EntityFrameworkCore.Storage.NonRetryingExecutionStrategy(d)))
#if !DEBUG
    .EnableThreadSafetyChecks(false)
#endif
);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions { }));

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
});
#endif

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new DateOnlyJsonConverter());
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PessoaJsonContext.Default);
});

var app = builder.Build();

#if DEBUG
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
#endif

app.MapPessoas();
#if DEBUG
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
#endif
app.MapHealthChecks("/healthz");

app.Run();

