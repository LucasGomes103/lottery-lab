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
builder.Services.AddCors(o => o.AddPolicy("web", p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();
app.UseSwagger(); app.UseSwaggerUI(); app.UseCors("web"); app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.Run();
