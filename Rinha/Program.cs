var builder = WebApplication.CreateBuilder(args);

#if DEBUG
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSwaggerGen(_ => _.MapType<DateOnly>(() =>
new()
{
    Type = "string",
    Example = new Microsoft.OpenApi.Any.OpenApiString("yyyy-MM-dd")
}));
#endif

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new DateOnlyJsonConverter());
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PessoaContext.Default);
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
app.MapGet("/", () => Results.Redirect("/swagger"));
#endif

app.Run();

