using LotteryLab.Api.Data;
using LotteryLab.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<PdfImportService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<NumberGeneratorService>();
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ??
    ["https://lottery-lab.gomeslucas103.workers.dev", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors("web"); app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.Run();
