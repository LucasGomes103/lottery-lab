using System.Globalization;
using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed record MegaSenaHistorySyncResult(int Latest, int AlreadyPresent, int Imported, List<string> Errors);

public sealed class MegaSenaResultsService(HttpClient http, Db db, ILogger<MegaSenaResultsService> logger)
{
    public const string CaixaUrl = "https://servicebus2.caixa.gov.br/portaldeloterias/api/megasena";
    public const string FallbackUrl = "https://loteriascaixa-api.herokuapp.com/api/megasena";

    public static MegaSenaDraw ParseCaixa(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var numbers = ReadNumbers(root, "listaDezenas") ?? ReadNumbers(root, "dezenasSorteadasOrdemSorteio")
            ?? throw new InvalidDataException("A resposta da Caixa não contém as dezenas sorteadas.");
        var prizes = ReadPrizes(root, "listaRateioPremio", "numeroDeGanhadores");
        return new MegaSenaDraw(root.GetProperty("numero").GetInt32(), ParseDate(root.GetProperty("dataApuracao").GetString()),
            numbers, prizes[1].Winners, prizes[1].Prize, prizes[2].Winners, prizes[2].Prize, prizes[3].Winners, prizes[3].Prize,
            ReadBool(root, "acumulado"), ReadDecimal(root, "valorEstimadoProximoConcurso"), CaixaUrl);
    }

    public static MegaSenaDraw ParseFallback(JsonElement root)
    {
        var numbers = ReadNumbers(root, "dezenas") ?? ReadNumbers(root, "dezenasOrdemSorteio")
            ?? throw new InvalidDataException("A fonte alternativa não contém as dezenas sorteadas.");
        var prizes = ReadPrizes(root, "premiacoes", "ganhadores");
        return new MegaSenaDraw(root.GetProperty("concurso").GetInt32(), ParseDate(root.GetProperty("data").GetString()),
            numbers, prizes[1].Winners, prizes[1].Prize, prizes[2].Winners, prizes[2].Prize, prizes[3].Winners, prizes[3].Prize,
            ReadBool(root, "acumulou"), ReadDecimal(root, "valorEstimadoProximoConcurso"), FallbackUrl);
    }

    public static MegaSenaDraw ParseFallback(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseFallback(document.RootElement);
    }

    /// <summary>Busca um concurso (ou o último, quando nulo) na Caixa e, se falhar, na fonte alternativa.</summary>
    public async Task<MegaSenaDraw> Fetch(int? contest, CancellationToken cancellationToken = default)
    {
        var suffix = contest is null ? "" : $"/{contest}";
        try
        {
            return ParseCaixa(await GetString(CaixaUrl + suffix, cancellationToken));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Caixa indisponível para o concurso {Contest}; usando a fonte alternativa.", contest?.ToString() ?? "último");
            return ParseFallback(await GetString(FallbackUrl + (contest is null ? "/latest" : suffix), cancellationToken));
        }
    }

    /// <summary>Todos os concursos em uma requisição, pela fonte alternativa. Usado para acelerar a carga histórica.</summary>
    public async Task<List<MegaSenaDraw>> FetchAllFromFallback(CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await GetString(FallbackUrl, cancellationToken));
        var draws = new List<MegaSenaDraw>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            try { draws.Add(ParseFallback(item)); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                logger.LogWarning(error, "Concurso ignorado na carga em lote da fonte alternativa.");
            }
        }
        return draws;
    }

    public async Task Upsert(MegaSenaDraw draw)
    {
        Validate(draw);
        await using var connection = db.Open();
        await connection.ExecuteAsync(
            @"insert into megasena_draws(contest,draw_date,numbers,sena_winners,sena_prize,quina_winners,quina_prize,
                quadra_winners,quadra_prize,accumulated,next_estimate,source,imported_at)
              values(@Contest,@DrawDate,@Numbers,@SenaWinners,@SenaPrize,@QuinaWinners,@QuinaPrize,
                @QuadraWinners,@QuadraPrize,@Accumulated,@NextEstimate,@Source,now())
              on conflict(contest) do update set draw_date=excluded.draw_date,numbers=excluded.numbers,
                sena_winners=excluded.sena_winners,sena_prize=excluded.sena_prize,quina_winners=excluded.quina_winners,
                quina_prize=excluded.quina_prize,quadra_winners=excluded.quadra_winners,quadra_prize=excluded.quadra_prize,
                accumulated=excluded.accumulated,next_estimate=excluded.next_estimate,source=excluded.source,imported_at=now()",
            new { draw.Contest, DrawDate = draw.DrawDate.ToDateTime(TimeOnly.MinValue),
                Numbers = draw.Numbers.Order().Select(x => (short)x).ToArray(), draw.SenaWinners, draw.SenaPrize,
                draw.QuinaWinners, draw.QuinaPrize, draw.QuadraWinners, draw.QuadraPrize, draw.Accumulated,
                draw.NextEstimate, draw.Source });
    }

    public async Task<MegaSenaDraw> SyncLatest(CancellationToken cancellationToken = default)
    {
        var draw = await Fetch(null, cancellationToken);
        await Upsert(draw);
        return draw;
    }

    public async Task<MegaSenaHistorySyncResult> SyncHistory(CancellationToken cancellationToken,
        Action<int, int, string>? progress = null, TimeSpan? throttle = null)
    {
        var latest = await Fetch(null, cancellationToken);
        await Upsert(latest);
        HashSet<int> existing;
        await using (var connection = db.Open())
            existing = (await connection.QueryAsync<int>("select contest from megasena_draws")).ToHashSet();
        var missing = Enumerable.Range(1, latest.Contest).Where(x => !existing.Contains(x)).ToList();
        var alreadyPresent = latest.Contest - missing.Count;
        var imported = 0;
        var errors = new List<string>();
        progress?.Invoke(0, missing.Count, $"{missing.Count} concursos ausentes");

        // Uma única requisição em lote evita milhares de chamadas à Caixa; o que faltar segue concurso a concurso.
        if (missing.Count > 20)
        {
            try
            {
                progress?.Invoke(0, missing.Count, "Baixando o histórico em lote");
                var wanted = missing.ToHashSet();
                foreach (var draw in await FetchAllFromFallback(cancellationToken))
                {
                    if (!wanted.Remove(draw.Contest)) continue;
                    try { await Upsert(draw); imported++; }
                    catch (InvalidDataException error) { wanted.Add(draw.Contest); logger.LogWarning(error, "Concurso {Contest} inválido no lote.", draw.Contest); }
                }
                missing = wanted.Order().ToList();
                progress?.Invoke(imported, imported + missing.Count, $"{imported} concursos importados em lote");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Carga em lote indisponível; seguindo concurso a concurso.");
            }
        }

        var total = imported + missing.Count;
        foreach (var contest in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Upsert(await Fetch(contest, cancellationToken));
                imported++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Concurso {contest}: {error.Message}");
            }
            progress?.Invoke(imported + errors.Count, total, $"Concurso {contest}");
            await Task.Delay(throttle ?? TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        return new MegaSenaHistorySyncResult(latest.Contest, alreadyPresent, imported, errors);
    }

    private async Task<string> GetString(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void Validate(MegaSenaDraw draw)
    {
        if (draw.Contest < 1) throw new InvalidDataException("Número de concurso inválido.");
        if (draw.Numbers.Length != 6 || draw.Numbers.Distinct().Count() != 6 || draw.Numbers.Any(x => x is < 1 or > 60))
            throw new InvalidDataException($"Concurso {draw.Contest}: dezenas inválidas.");
    }

    private static int[]? ReadNumbers(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var numbers = list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32()
            : int.Parse(x.GetString() ?? "", CultureInfo.InvariantCulture)).Order().ToArray();
        return numbers.Length == 6 ? numbers : null;
    }

    private static Dictionary<int, (int Winners, decimal Prize)> ReadPrizes(JsonElement root, string property, string winnersProperty)
    {
        var prizes = new Dictionary<int, (int, decimal)> { [1] = (0, 0m), [2] = (0, 0m), [3] = (0, 0m) };
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return prizes;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("faixa", out var tier) || tier.ValueKind != JsonValueKind.Number) continue;
            var winners = item.TryGetProperty(winnersProperty, out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
            prizes[tier.GetInt32()] = (winners, ReadDecimal(item, "valorPremio") ?? 0m);
        }
        return prizes;
    }

    private static decimal? ReadDecimal(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? Math.Round(value.GetDecimal(), 2) : null;

    private static bool ReadBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateOnly ParseDate(string? value) =>
        DateOnly.ParseExact(value ?? "", "dd/MM/yyyy", CultureInfo.InvariantCulture);
}
