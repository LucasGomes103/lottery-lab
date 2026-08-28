using LotteryLab.Api.Data;
using LotteryLab.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<PredictionSchema>();
builder.Services.AddScoped<PdfImportService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<NumberGeneratorService>();
builder.Services.AddScoped<PredictionService>();
builder.Services.AddSingleton<ExternalResultsState>();
builder.Services.AddHttpClient<ExternalResultsService>(client =>
{
    client.BaseAddress = new Uri("https://resultadonacional.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LotteryLab/1.0 (+result synchronization)");
});
builder.Services.AddHostedService<ExternalResultsWorker>();
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ??
    ["https://lottery-lab.gomeslucas103.workers.dev", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
await app.Services.GetRequiredService<PredictionSchema>().Initialize();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors("web"); app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.Run();
