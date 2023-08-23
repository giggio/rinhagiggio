var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<RinhaContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Rinha")));
builder.Services.AddHealthChecks().AddDbContextCheck<RinhaContext>();
builder.Services.AddOutputCache();

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
app.UseOutputCache();

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

