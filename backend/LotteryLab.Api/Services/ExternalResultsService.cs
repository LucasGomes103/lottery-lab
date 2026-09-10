using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using LotteryLab.Api.Data;

namespace LotteryLab.Api.Services;

public sealed class ExternalResultsState
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public int LastInserted { get; set; }
    public DateTimeOffset? LookLastSuccess { get; set; }
    public string? LookLastError { get; set; }
    public int LookLastInserted { get; set; }
}

public sealed record ExternalSyncResult(DateOnly Date, int Inserted, int AlreadyPresent,
    List<string> InsertedTimes, List<string> UnavailableTimes, string Source, DateTimeOffset CheckedAt);

public sealed class ExternalResultsService(HttpClient http, Db db, PredictionService predictions, ResultFacilHistoryService history,
    ExternalResultsState state, ILogger<ExternalResultsService> logger)
{
    private const string Bank = "LT NACIONAL";
    private static readonly string[] Schedules = ["02:00", "08:00", "10:00", "12:00", "15:00", "17:00", "21:00", "23:00"];
    private static readonly string[] Animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA",
        "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO",
        "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];

    public async Task<ExternalSyncResult> Sync(DateOnly date, CancellationToken cancellationToken = default)
    {
        state.LastAttempt = DateTimeOffset.UtcNow;
        try
        {
            await using var connection = db.Open();
            var before = (await connection.QueryAsync<TimeSpan>("select extraction_time from extractions where bank=@Bank and extraction_date=@Date",
                new { Bank, Date = date.ToDateTime(TimeOnly.MinValue) })).Select(x => $"{x.Hours:00}:{x.Minutes:00}").ToHashSet();
            await history.Sync(Bank, date, date, cancellationToken);
            var after = (await connection.QueryAsync<TimeSpan>("select extraction_time from extractions where bank=@Bank and extraction_date=@Date",
                new { Bank, Date = date.ToDateTime(TimeOnly.MinValue) })).Select(x => $"{x.Hours:00}:{x.Minutes:00}").ToHashSet();
            var insertedTimes = after.Except(before).Order().ToList();
            var unavailable = Schedules.Where(x => !after.Contains(x)).ToList();
            state.LastSuccess = DateTimeOffset.UtcNow; state.LastError = null; state.LastInserted = insertedTimes.Count;
            return new ExternalSyncResult(date, insertedTimes.Count, before.Count, insertedTimes, unavailable,
                "https://www.resultadofacil.com.br/", DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            state.LastError = exception.Message;
            logger.LogError(exception, "Falha ao sincronizar resultados automáticos da Nacional de {Date}", date);
            throw;
        }
    }

    // Mantido temporariamente como referência da fonte legada; a sincronização ativa usa 1º–10º.
    private async Task<ExternalSyncResult> SyncLegacy(DateOnly date, CancellationToken cancellationToken = default)
    {
        state.LastAttempt = DateTimeOffset.UtcNow;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["dtSorteio"] = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), ["horario"] = ""
            });
            using var response = await http.PostAsync("resultado/busca", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SourceResponse>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("A fonte externa retornou uma resposta vazia.");
            if (!payload.Success) throw new InvalidDataException("A fonte externa informou falha na consulta.");

            var now = SaoPauloNow();
            var eligibleSchedules = Schedules.Where(schedule => date < DateOnly.FromDateTime(now.DateTime) ||
                (date == DateOnly.FromDateTime(now.DateTime) && TimeOnly.Parse(schedule).AddMinutes(10) <= TimeOnly.FromDateTime(now.DateTime))).ToHashSet();
            var national = payload.Data.Where(x => x.Lottery.Equals("Nacional", StringComparison.OrdinalIgnoreCase)
                && x.ResultType == "TD" && eligibleSchedules.Contains(x.Time) && int.TryParse(x.Prize, out var prize)
                && prize is >= 1 and <= 10).GroupBy(x => x.Time).ToDictionary(x => x.Key, x => x.OrderBy(r => int.Parse(r.Prize)).ToList());

            await using var connection = db.Open();
            var existing = (await connection.QueryAsync<TimeSpan>(
                "select extraction_time from extractions where bank=@Bank and extraction_date=@Date",
                new { Bank, Date = date.ToDateTime(TimeOnly.MinValue) }))
                .Select(x => $"{x.Hours:00}:{x.Minutes:00}").ToHashSet();
            var insertedTimes = new List<string>();
            var unavailable = new List<string>();
            foreach (var schedule in eligibleSchedules.Order())
            {
                if (existing.Contains(schedule)) continue;
                if (!national.TryGetValue(schedule, out var sourceRows) || sourceRows.Count != 10)
                {
                    unavailable.Add(schedule); continue;
                }
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var extractionId = await connection.ExecuteScalarAsync<long?>(
                    @"insert into extractions(bank,extraction_date,extraction_time,source_file)
                      values(@Bank,@Date,@Time::time,@Source) on conflict do nothing returning id",
                    new { Bank, Date = date.ToDateTime(TimeOnly.MinValue), Time = schedule,
                        Source = "AUTO:https://resultadonacional.com/" }, transaction);
                if (extractionId is null) { await transaction.RollbackAsync(cancellationToken); continue; }
                foreach (var row in sourceRows)
                {
                    var position = int.Parse(row.Prize);
                    var digits = new string(row.Result.Where(char.IsDigit).ToArray()).PadLeft(4, '0')[^4..];
                    var dezena = digits[^2..]; var centena = digits[^3..];
                    var value = int.Parse(dezena); var group = value == 0 ? 25 : (value + 3) / 4;
                    await connection.ExecuteAsync(
                        @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                          values(@ExtractionId,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                        new { ExtractionId = extractionId.Value, Position = position, Number = digits, Centena = centena,
                            Dezena = dezena, Group = group, Animal = Animals[group - 1] }, transaction);
                }
                await transaction.CommitAsync(cancellationToken);
                insertedTimes.Add(schedule);
                await predictions.EvaluatePending(Bank, date, schedule);
            }
            state.LastSuccess = DateTimeOffset.UtcNow; state.LastError = null; state.LastInserted = insertedTimes.Count;
            return new ExternalSyncResult(date, insertedTimes.Count, existing.Count, insertedTimes, unavailable,
                "https://resultadonacional.com/", DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            state.LastError = exception.Message;
            logger.LogError(exception, "Falha ao sincronizar resultados externos de {Date}", date);
            throw;
        }
    }

    public object Status() => new { state.LastAttempt, state.LastSuccess, state.LastError, state.LastInserted,
        look = new { state.LookLastSuccess, state.LookLastError, state.LookLastInserted, schedules = new[] { "07:00", "09:00", "11:00", "14:00", "16:00", "18:00", "21:00", "23:00" } },
        intervalMinutes = 5, source = "https://resultadonacional.com/", ignoredLottery = "26 da Sorte" };

    public async Task SyncLook(DateOnly date, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await history.Sync("LOOK LOTERIAS", date, date, cancellationToken);
            var inserted = result.ImportedExtractions;
            state.LookLastSuccess = DateTimeOffset.UtcNow; state.LookLastError = null; state.LookLastInserted = inserted;
        }
        catch (Exception exception)
        {
            state.LookLastError = exception.Message;
            logger.LogError(exception, "Falha ao sincronizar automaticamente Look Loterias de {Date}", date);
        }
    }

    private static DateTimeOffset SaoPauloNow()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
    }

    private sealed record SourceResponse([property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] List<SourceRow> Data);
    private sealed record SourceRow([property: JsonPropertyName("HORARIO")] string Time,
        [property: JsonPropertyName("RESULTADO")] string Result,
        [property: JsonPropertyName("PREMIO")] string Prize,
        [property: JsonPropertyName("TIPO_RESULTADO")] string ResultType,
        [property: JsonPropertyName("NO_LOTERIA")] string Lottery);
}

public sealed class ExternalResultsWorker(IServiceScopeFactory scopeFactory, ILogger<ExternalResultsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ExternalResultsService>();
                var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
                await service.Sync(today, stoppingToken);
                await service.SyncLook(today, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Sincronização automática será tentada novamente."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
