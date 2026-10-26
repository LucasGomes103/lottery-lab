using LotteryLab.Api.Data;
using LotteryLab.Api.Security;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<PredictionSchema>();
builder.Services.AddSingleton<AuthSchema>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthentication("Session")
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("Session", _ => { });
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Permissions.All)
        options.AddPolicy(permission, policy => policy.RequireClaim("permission", permission));
});
// No Render a API fica atrás de um único proxy; sem isso o limitador de login veria só o IP do proxy.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddScoped<PdfImportService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<NumberGeneratorService>();
builder.Services.AddScoped<PredictionService>();
builder.Services.AddScoped<RandomnessService>();
builder.Services.AddSingleton<DecisionBatteryJobService>();
builder.Services.AddSingleton<HistorySyncJobService>();
builder.Services.AddSingleton<ExternalResultsState>();
builder.Services.AddHttpClient<ExternalResultsService>(client =>
{
    client.BaseAddress = new Uri("https://resultadonacional.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LotteryLab/1.0 (+result synchronization)");
});
builder.Services.AddHttpClient<ResultFacilHistoryService>(client =>
{
    client.BaseAddress = new Uri("https://www.resultadofacil.com.br/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
});
builder.Services.AddSingleton<MegaSenaSchema>();
builder.Services.AddScoped<MegaSenaService>();
builder.Services.AddSingleton<MegaSenaWheelService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MegaSenaHistoryJobService>();
builder.Services.AddSingleton<MegaSenaSyncState>();
builder.Services.AddHttpClient<MegaSenaResultsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
});
builder.Services.AddSingleton<LotofacilSchema>();
builder.Services.AddScoped<LotofacilService>();
builder.Services.AddSingleton<LotofacilHistoryJobService>();
builder.Services.AddSingleton<LotofacilSyncState>();
builder.Services.AddHttpClient<LotofacilResultsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
});
builder.Services.AddHostedService<ExternalResultsWorker>();
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ??
    ["https://lottery-lab.gomeslucas103.workers.dev", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
await app.Services.GetRequiredService<PredictionSchema>().Initialize();
await app.Services.GetRequiredService<AuthSchema>().Initialize();
await app.Services.GetRequiredService<MegaSenaSchema>().Initialize();
await app.Services.GetRequiredService<LotofacilSchema>().Initialize();
// A reavaliação roda em segundo plano para a API responder logo após o deploy ou o cold start.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PredictionService>().ReevaluateAll();
    }
    catch (Exception error) { app.Logger.LogError(error, "Falha ao reavaliar previsões na inicialização."); }
}));
app.UseForwardedHeaders();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRouting();
app.UseCors("web");
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!app.Environment.IsDevelopment())
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.Run();
