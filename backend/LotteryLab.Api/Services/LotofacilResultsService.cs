using System.Globalization;
using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed record LotofacilHistorySyncResult(int Latest, int AlreadyPresent, int Imported, List<string> Errors);

public sealed class LotofacilSyncState
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public int? LastContest { get; set; }
    public string? LastSource { get; set; }
    public string? LastError { get; set; }
    public string? LastCaixaError { get; set; }
}

public sealed class LotofacilResultsService(HttpClient http, Db db, LotofacilSyncState state, ILogger<LotofacilResultsService> logger)
{
    public const string CaixaUrl = "https://servicebus2.caixa.gov.br/portaldeloterias/api/lotofacil";
    public const string FallbackUrl = "https://loteriascaixa-api.herokuapp.com/api/lotofacil";
    private static readonly TimeSpan CaixaTimeout = TimeSpan.FromSeconds(20);
    private const int UpsertBatchSize = 500;
    // O serviço é criado por escopo (uma carga ou um ciclo do worker): após uma recusa da Caixa, o restante usa direto o fallback.
    private bool caixaUnavailable;

    public static LotofacilDraw ParseCaixa(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var numbers = ReadNumbers(root, "listaDezenas") ?? ReadNumbers(root, "dezenasSorteadasOrdemSorteio")
            ?? throw new InvalidDataException("A resposta da Caixa não contém as dezenas sorteadas.");
        var prizes = ReadPrizes(root, "listaRateioPremio", "numeroDeGanhadores");
        return Create(root.GetProperty("numero").GetInt32(), ParseDate(root.GetProperty("dataApuracao").GetString()), numbers, prizes,
            ReadBool(root, "acumulado"), ReadDecimal(root, "valorEstimadoProximoConcurso"), CaixaUrl);
    }

    public static LotofacilDraw ParseFallback(JsonElement root)
    {
        var numbers = ReadNumbers(root, "dezenas") ?? ReadNumbers(root, "dezenasOrdemSorteio")
            ?? throw new InvalidDataException("A fonte alternativa não contém as dezenas sorteadas.");
        var prizes = ReadPrizes(root, "premiacoes", "ganhadores");
        return Create(root.GetProperty("concurso").GetInt32(), ParseDate(root.GetProperty("data").GetString()), numbers, prizes,
            ReadBool(root, "acumulou"), ReadDecimal(root, "valorEstimadoProximoConcurso"), FallbackUrl);
    }

    public static LotofacilDraw ParseFallback(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseFallback(document.RootElement);
    }

    // Faixas do rateio: 1 = 15 acertos, 2 = 14, 3 = 13, 4 = 12, 5 = 11.
    private static LotofacilDraw Create(int contest, DateOnly date, int[] numbers, Dictionary<int, (int Winners, decimal Prize)> prizes,
        bool accumulated, decimal? nextEstimate, string source) =>
        new(contest, date, numbers, prizes[1].Winners, prizes[1].Prize, prizes[2].Winners, prizes[2].Prize,
            prizes[3].Winners, prizes[3].Prize, prizes[4].Winners, prizes[4].Prize, prizes[5].Winners, prizes[5].Prize,
            accumulated, nextEstimate, source);

    /// <summary>Busca um concurso (ou o último, quando nulo) na Caixa e, se falhar, na fonte alternativa.</summary>
    public async Task<LotofacilDraw> Fetch(int? contest, CancellationToken cancellationToken = default)
    {
        var suffix = contest is null ? "" : $"/{contest}";
        if (caixaUnavailable)
            return ParseFallback(await GetString(FallbackUrl + (contest is null ? "/latest" : suffix), cancellationToken));
        try
        {
            // A Caixa costuma bloquear ou segurar conexões de datacenters fora do Brasil; não vale esperar o timeout geral.
            using var caixaTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            caixaTimeout.CancelAfter(CaixaTimeout);
            return ParseCaixa(await GetString(CaixaUrl + suffix, caixaTimeout.Token));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            caixaUnavailable = true;
            state.LastCaixaError = $"{DateTimeOffset.UtcNow:u} {Describe(error)}";
            logger.LogWarning(error, "Caixa indisponível para a Lotofácil, concurso {Contest}; usando a fonte alternativa.", contest?.ToString() ?? "último");
            return ParseFallback(await GetString(FallbackUrl + (contest is null ? "/latest" : suffix), cancellationToken));
        }
    }

    /// <summary>Todos os concursos em uma requisição, pela fonte alternativa. Usado para acelerar a carga histórica.</summary>
    public async Task<List<LotofacilDraw>> FetchAllFromFallback(CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await GetString(FallbackUrl, cancellationToken));
        var draws = new List<LotofacilDraw>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            try { draws.Add(ParseFallback(item)); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                logger.LogWarning(error, "Concurso da Lotofácil ignorado na carga em lote da fonte alternativa.");
            }
        }
        return draws;
    }

    public Task Upsert(LotofacilDraw draw) => UpsertMany([draw]);

    /// <summary>Grava vários concursos por comando (unnest), evitando uma ida ao banco por concurso.</summary>
    public async Task<int> UpsertMany(IReadOnlyList<LotofacilDraw> draws, Action<int>? progress = null)
    {
        foreach (var draw in draws) Validate(draw);
        await using var connection = db.Open();
        var written = 0;
        foreach (var batch in draws.Chunk(UpsertBatchSize))
        {
            await connection.ExecuteAsync(
                @"insert into lotofacil_draws(contest,draw_date,numbers,winners15,prize15,winners14,prize14,winners13,prize13,
                    winners12,prize12,winners11,prize11,accumulated,next_estimate,source,imported_at)
                  select x.contest,x.draw_date,string_to_array(x.numbers,',')::smallint[],x.winners15,x.prize15,x.winners14,x.prize14,
                    x.winners13,x.prize13,x.winners12,x.prize12,x.winners11,x.prize11,x.accumulated,x.next_estimate,x.source,now()
                  from unnest(@contests::integer[],@dates::date[],@numbers::text[],@winners15::integer[],@prizes15::numeric[],
                    @winners14::integer[],@prizes14::numeric[],@winners13::integer[],@prizes13::numeric[],
                    @winners12::integer[],@prizes12::numeric[],@winners11::integer[],@prizes11::numeric[],
                    @accumulated::boolean[],@nextEstimates::numeric[],@sources::text[])
                    as x(contest,draw_date,numbers,winners15,prize15,winners14,prize14,winners13,prize13,winners12,prize12,
                      winners11,prize11,accumulated,next_estimate,source)
                  on conflict(contest) do update set draw_date=excluded.draw_date,numbers=excluded.numbers,
                    winners15=excluded.winners15,prize15=excluded.prize15,winners14=excluded.winners14,prize14=excluded.prize14,
                    winners13=excluded.winners13,prize13=excluded.prize13,winners12=excluded.winners12,prize12=excluded.prize12,
                    winners11=excluded.winners11,prize11=excluded.prize11,accumulated=excluded.accumulated,
                    next_estimate=excluded.next_estimate,source=excluded.source,imported_at=now()",
                new
                {
                    contests = batch.Select(x => x.Contest).ToArray(),
                    dates = batch.Select(x => x.DrawDate.ToDateTime(TimeOnly.MinValue)).ToArray(),
                    numbers = batch.Select(x => string.Join(',', x.Numbers.Order())).ToArray(),
                    winners15 = batch.Select(x => x.Winners15).ToArray(), prizes15 = batch.Select(x => x.Prize15).ToArray(),
                    winners14 = batch.Select(x => x.Winners14).ToArray(), prizes14 = batch.Select(x => x.Prize14).ToArray(),
                    winners13 = batch.Select(x => x.Winners13).ToArray(), prizes13 = batch.Select(x => x.Prize13).ToArray(),
                    winners12 = batch.Select(x => x.Winners12).ToArray(), prizes12 = batch.Select(x => x.Prize12).ToArray(),
                    winners11 = batch.Select(x => x.Winners11).ToArray(), prizes11 = batch.Select(x => x.Prize11).ToArray(),
                    accumulated = batch.Select(x => x.Accumulated).ToArray(), nextEstimates = batch.Select(x => x.NextEstimate).ToArray(),
                    sources = batch.Select(x => x.Source).ToArray()
                });
            written += batch.Length;
            progress?.Invoke(written);
        }
        return written;
    }

    public async Task<LotofacilDraw> SyncLatest(CancellationToken cancellationToken = default)
    {
        state.LastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var draw = await Fetch(null, cancellationToken);
            await Upsert(draw);
            state.LastSuccess = DateTimeOffset.UtcNow; state.LastContest = draw.Contest; state.LastSource = draw.Source; state.LastError = null;
            return draw;
        }
        catch (Exception error)
        {
            state.LastError = Describe(error);
            throw;
        }
    }

    public static string Describe(Exception error) => MegaSenaResultsService.Describe(error);

    public async Task<LotofacilHistorySyncResult> SyncHistory(CancellationToken cancellationToken,
        Action<int, int, string>? progress = null, TimeSpan? throttle = null)
    {
        progress?.Invoke(0, 0, "Consultando o último concurso");
        var latest = await SyncLatest(cancellationToken);
        progress?.Invoke(0, 0, $"Último concurso: {latest.Contest} ({latest.Source})");
        HashSet<int> existing;
        await using (var connection = db.Open())
            existing = (await connection.QueryAsync<int>("select contest from lotofacil_draws")).ToHashSet();
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
                var valid = new List<LotofacilDraw>();
                foreach (var draw in await FetchAllFromFallback(cancellationToken))
                {
                    if (!wanted.Contains(draw.Contest)) continue;
                    try { Validate(draw); valid.Add(draw); wanted.Remove(draw.Contest); }
                    catch (InvalidDataException error) { logger.LogWarning(error, "Concurso {Contest} da Lotofácil inválido no lote.", draw.Contest); }
                }
                progress?.Invoke(0, missing.Count, $"Gravando {valid.Count} concursos");
                imported = await UpsertMany(valid, written => progress?.Invoke(written, missing.Count, $"Gravando concursos ({written} de {valid.Count})"));
                missing = wanted.Order().ToList();
                progress?.Invoke(imported, imported + missing.Count, $"{imported} concursos importados em lote");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Carga em lote: {Describe(error)}");
                logger.LogWarning(error, "Carga em lote da Lotofácil indisponível; seguindo concurso a concurso.");
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
                errors.Add($"Concurso {contest}: {Describe(error)}");
            }
            progress?.Invoke(imported + errors.Count, total, $"Concurso {contest}");
            await Task.Delay(throttle ?? TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        return new LotofacilHistorySyncResult(latest.Contest, alreadyPresent, imported, errors);
    }

    private async Task<string> GetString(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void Validate(LotofacilDraw draw)
    {
        if (draw.Contest < 1) throw new InvalidDataException("Número de concurso inválido.");
        if (draw.Numbers.Length != 15 || draw.Numbers.Distinct().Count() != 15 || draw.Numbers.Any(x => x is < 1 or > 25))
            throw new InvalidDataException($"Concurso {draw.Contest}: dezenas inválidas.");
    }

    private static int[]? ReadNumbers(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var numbers = list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32()
            : int.Parse(x.GetString() ?? "", CultureInfo.InvariantCulture)).Order().ToArray();
        return numbers.Length == 15 ? numbers : null;
    }

    private static Dictionary<int, (int Winners, decimal Prize)> ReadPrizes(JsonElement root, string property, string winnersProperty)
    {
        var prizes = Enumerable.Range(1, 5).ToDictionary(x => x, _ => (0, 0m));
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
