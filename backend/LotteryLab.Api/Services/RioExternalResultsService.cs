using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using LotteryLab.Api.Data;

namespace LotteryLab.Api.Services;

public sealed class RioExternalResultsState
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public int LastInserted { get; set; }
}

public sealed partial class RioExternalResultsService(HttpClient http, Db db, PredictionService predictions,
    RioExternalResultsState state, ILogger<RioExternalResultsService> logger)
{
    public const string Bank = "PT RIO";
    private const string SourceUrl = "https://www.resultadofacil.com.br/";
    private static readonly string[] Animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA",
        "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO",
        "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];

    public async Task<ExternalSyncResult> Sync(DateOnly date, CancellationToken cancellationToken = default)
    {
        state.LastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var now = SaoPauloNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            var path = date == today ? "resultados-pt-rio-de-hoje" : $"resultados-pt-rio-do-dia-{date:yyyy-MM-dd}";
            var html = await http.GetStringAsync(path, cancellationToken);
            var expected = ExpectedSchedules(date);
            var eligible = expected.Where(schedule => date < today ||
                (date == today && TimeOnly.Parse(schedule).AddMinutes(25) <= TimeOnly.FromDateTime(now.DateTime))).ToHashSet();
            var source = Parse(html, date).Where(x => eligible.Contains(x.Time))
                .GroupBy(x => x.Time).ToDictionary(x => x.Key, x => x.OrderBy(r => r.Position).ToList());

            await using var connection = db.Open();
            var existing = (await connection.QueryAsync<TimeSpan>(
                "select extraction_time from extractions where bank=@Bank and extraction_date=@Date",
                new { Bank, Date = date.ToDateTime(TimeOnly.MinValue) }))
                .Select(x => $"{x.Hours:00}:{x.Minutes:00}").ToHashSet();
            var insertedTimes = new List<string>();
            var unavailable = new List<string>();
            foreach (var schedule in eligible.Order())
            {
                if (existing.Contains(schedule)) continue;
                if (!source.TryGetValue(schedule, out var rows) || rows.Count != 5 ||
                    !rows.Select(x => x.Position).SequenceEqual(Enumerable.Range(1, 5)))
                { unavailable.Add(schedule); continue; }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var extractionId = await connection.ExecuteScalarAsync<long?>(
                    @"insert into extractions(bank,extraction_date,extraction_time,source_file)
                      values(@Bank,@Date,@Time::time,@Source) on conflict do nothing returning id",
                    new { Bank, Date = date.ToDateTime(TimeOnly.MinValue), Time = schedule,
                        Source = $"AUTO:{SourceUrl}{path}" }, transaction);
                if (extractionId is null) { await transaction.RollbackAsync(cancellationToken); continue; }
                foreach (var row in rows)
                {
                    var number = row.Number.PadLeft(4, '0')[^4..];
                    var dezena = number[^2..]; var centena = number[^3..];
                    var value = int.Parse(dezena); var group = value == 0 ? 25 : (value + 3) / 4;
                    await connection.ExecuteAsync(
                        @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                          values(@ExtractionId,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                        new { ExtractionId = extractionId.Value, row.Position, Number = number, Centena = centena,
                            Dezena = dezena, Group = group, Animal = Animals[group - 1] }, transaction);
                }
                await transaction.CommitAsync(cancellationToken);
                insertedTimes.Add(schedule);
                await predictions.EvaluatePending(Bank, date, schedule);
            }
            state.LastSuccess = DateTimeOffset.UtcNow; state.LastError = null; state.LastInserted = insertedTimes.Count;
            return new ExternalSyncResult(date, insertedTimes.Count, existing.Count(x => expected.Contains(x)),
                insertedTimes, unavailable, $"{SourceUrl}{path}", DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            state.LastError = exception.Message;
            logger.LogError(exception, "Falha ao sincronizar PT RIO de {Date}", date);
            throw;
        }
    }

    public object Status() => new { bank = Bank, state.LastAttempt, state.LastSuccess, state.LastError,
        state.LastInserted, intervalMinutes = 5, source = SourceUrl };

    public static IReadOnlyList<SourceResult> Parse(string html, DateOnly date)
    {
        foreach (Match script in JsonLdRegex().Matches(html))
        {
            using var document = JsonDocument.Parse(script.Groups[1].Value);
            if (!document.RootElement.TryGetProperty("@graph", out var graph)) continue;
            foreach (var node in graph.EnumerateArray())
            {
                if (!node.TryGetProperty("@type", out var type) || type.GetString() != "Dataset" ||
                    !node.TryGetProperty("temporalCoverage", out var coverage) || coverage.GetString() != date.ToString("yyyy-MM-dd") ||
                    !node.TryGetProperty("variableMeasured", out var values)) continue;
                var results = new List<SourceResult>();
                foreach (var item in values.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("DEU NO POSTE - RJ", StringComparison.OrdinalIgnoreCase)) continue;
                    var match = ResultNameRegex().Match(name);
                    var number = NumberRegex().Match(item.GetProperty("value").GetString() ?? "").Value;
                    if (!match.Success || number.Length != 4) continue;
                    var sourceTime = TimeOnly.ParseExact(match.Groups["time"].Value, "HH:mm", CultureInfo.InvariantCulture);
                    results.Add(new SourceResult($"{sourceTime.Hour:00}:00", int.Parse(match.Groups["position"].Value), number));
                }
                return results;
            }
        }
        return [];
    }

    private static HashSet<string> ExpectedSchedules(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday
        ? ["14:00", "16:00"] : ["09:00", "11:00", "14:00", "16:00", "18:00", "21:00"];
    private static DateTimeOffset SaoPauloNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));

    public sealed record SourceResult(string Time, int Position, string Number);
    [GeneratedRegex("<script[^>]+type=[\\\"']application/ld\\+json[\\\"'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLdRegex();
    [GeneratedRegex("(?<time>\\d{2}:\\d{2})\\s+—\\s+(?<position>[1-5])º prêmio", RegexOptions.IgnoreCase)]
    private static partial Regex ResultNameRegex();
    [GeneratedRegex("^\\d{4}")]
    private static partial Regex NumberRegex();
}
