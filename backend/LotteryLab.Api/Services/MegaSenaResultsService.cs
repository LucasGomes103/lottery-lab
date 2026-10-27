using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed record MegaSenaHistorySyncResult(int Latest, int AlreadyPresent, int Imported, List<string> Errors);

public sealed class MegaSenaSyncState
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public int? LastContest { get; set; }
    public string? LastSource { get; set; }
    public string? LastError { get; set; }
    public string? LastCaixaError { get; set; }
}

public sealed class MegaSenaResultsService(HttpClient http, Db db, MegaSenaSyncState state, ILogger<MegaSenaResultsService> logger)
{
    public const string CaixaUrl = "https://servicebus2.caixa.gov.br/portaldeloterias/api/megasena";
    public const string FallbackUrl = "https://loteriascaixa-api.herokuapp.com/api/megasena";
    public const string ResultadoFacilUrl = ResultadoFacilLotteryPage.BaseUrl + "mega-sena";
    private static readonly TimeSpan CaixaTimeout = TimeSpan.FromSeconds(20);
    private const int UpsertBatchSize = 500;
    // O serviço é criado por escopo (uma carga ou um ciclo do worker): após uma recusa da Caixa, o restante usa direto o fallback.
    private bool caixaUnavailable;

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

    public static MegaSenaDraw ParseResultadoFacil(string html)
    {
        var page = ResultadoFacilLotteryPage.Parse(html, 6, [6, 5, 4]);
        var prizes = page.Prizes;
        return new MegaSenaDraw(page.Contest, page.DrawDate, page.Numbers, prizes[1].Winners, prizes[1].Prize, prizes[2].Winners, prizes[2].Prize,
            prizes[3].Winners, prizes[3].Prize, prizes[1].Winners == 0, page.NextEstimate, ResultadoFacilUrl);
    }

    /// <summary>Dezenas válidas e rateio já publicado (a quadra sempre tem ganhadores depois da apuração).</summary>
    public static bool IsComplete(MegaSenaDraw draw)
    {
        try { Validate(draw); }
        catch (InvalidDataException) { return false; }
        return draw.QuadraWinners > 0;
    }

    /// <summary>Busca um concurso (ou o último, quando nulo) na Caixa e, se falhar, na fonte alternativa.</summary>
    public async Task<MegaSenaDraw> Fetch(int? contest, CancellationToken cancellationToken = default)
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
            logger.LogWarning(error, "Caixa indisponível para o concurso {Contest}; usando a fonte alternativa.", contest?.ToString() ?? "último");
            return ParseFallback(await GetString(FallbackUrl + (contest is null ? "/latest" : suffix), cancellationToken));
        }
    }

    /// <summary>
    /// O concurso mais recente disponível. O "último" da Caixa e da fonte alternativa costuma atrasar horas em relação à consulta
    /// por número, então também tenta o concurso seguinte e, por fim, o Resultado Fácil (acessível do Render, ao contrário da Caixa).
    /// </summary>
    public async Task<MegaSenaDraw> FetchNewest(CancellationToken cancellationToken = default)
    {
        MegaSenaDraw? latest = null;
        Exception? latestError = null;
        try { latest = await Fetch(null, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            latestError = error;
            logger.LogWarning(error, "Último concurso da Mega-Sena indisponível na Caixa e na fonte alternativa; tentando o Resultado Fácil.");
        }
        if (latest is not null && await TryFetchComplete(latest.Contest + 1, cancellationToken) is { } next) return next;
        var scraped = await TryComplete(async () => ParseResultadoFacil(await GetString(ResultadoFacilUrl, cancellationToken, "text/html")),
            cancellationToken, "Resultado Fácil");
        if (scraped is not null && scraped.Contest > (latest?.Contest ?? 0)) return scraped;
        if (latest is not null) return latest;
        ExceptionDispatchInfo.Throw(latestError!);
        return null!;
    }

    // Concurso ainda não sorteado faz a Caixa e a fonte alternativa responderem com erro: aqui isso é esperado e não marca a Caixa como indisponível.
    private async Task<MegaSenaDraw?> TryFetchComplete(int contest, CancellationToken cancellationToken)
    {
        if (!caixaUnavailable)
        {
            using var caixaTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            caixaTimeout.CancelAfter(CaixaTimeout);
            if (await TryComplete(async () => ParseCaixa(await GetString($"{CaixaUrl}/{contest}", caixaTimeout.Token)), cancellationToken, null) is { } caixa)
                return caixa;
        }
        return await TryComplete(async () => ParseFallback(await GetString($"{FallbackUrl}/{contest}", cancellationToken)), cancellationToken, null);
    }

    private async Task<MegaSenaDraw?> TryComplete(Func<Task<MegaSenaDraw>> fetch, CancellationToken cancellationToken, string? warnSource)
    {
        try
        {
            var draw = await fetch();
            return IsComplete(draw) ? draw : null;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (warnSource is not null) logger.LogWarning(error, "Falha ao consultar a Mega-Sena no {Source}.", warnSource);
            return null;
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

    public Task Upsert(MegaSenaDraw draw) => UpsertMany([draw]);

    /// <summary>Grava vários concursos por comando (unnest), evitando uma ida ao banco por concurso.</summary>
    public async Task<int> UpsertMany(IReadOnlyList<MegaSenaDraw> draws, Action<int>? progress = null)
    {
        foreach (var draw in draws) Validate(draw);
        await using var connection = db.Open();
        var written = 0;
        foreach (var batch in draws.Chunk(UpsertBatchSize))
        {
            await connection.ExecuteAsync(
                @"insert into megasena_draws(contest,draw_date,numbers,sena_winners,sena_prize,quina_winners,quina_prize,
                    quadra_winners,quadra_prize,accumulated,next_estimate,source,imported_at)
                  select x.contest,x.draw_date,string_to_array(x.numbers,',')::smallint[],x.sena_winners,x.sena_prize,
                    x.quina_winners,x.quina_prize,x.quadra_winners,x.quadra_prize,x.accumulated,x.next_estimate,x.source,now()
                  from unnest(@contests::integer[],@dates::date[],@numbers::text[],@senaWinners::integer[],@senaPrizes::numeric[],
                    @quinaWinners::integer[],@quinaPrizes::numeric[],@quadraWinners::integer[],@quadraPrizes::numeric[],
                    @accumulated::boolean[],@nextEstimates::numeric[],@sources::text[])
                    as x(contest,draw_date,numbers,sena_winners,sena_prize,quina_winners,quina_prize,quadra_winners,quadra_prize,
                      accumulated,next_estimate,source)
                  on conflict(contest) do update set draw_date=excluded.draw_date,numbers=excluded.numbers,
                    sena_winners=excluded.sena_winners,sena_prize=excluded.sena_prize,quina_winners=excluded.quina_winners,
                    quina_prize=excluded.quina_prize,quadra_winners=excluded.quadra_winners,quadra_prize=excluded.quadra_prize,
                    accumulated=excluded.accumulated,next_estimate=excluded.next_estimate,source=excluded.source,imported_at=now()",
                new
                {
                    contests = batch.Select(x => x.Contest).ToArray(),
                    dates = batch.Select(x => x.DrawDate.ToDateTime(TimeOnly.MinValue)).ToArray(),
                    numbers = batch.Select(x => string.Join(',', x.Numbers.Order())).ToArray(),
                    senaWinners = batch.Select(x => x.SenaWinners).ToArray(), senaPrizes = batch.Select(x => x.SenaPrize).ToArray(),
                    quinaWinners = batch.Select(x => x.QuinaWinners).ToArray(), quinaPrizes = batch.Select(x => x.QuinaPrize).ToArray(),
                    quadraWinners = batch.Select(x => x.QuadraWinners).ToArray(), quadraPrizes = batch.Select(x => x.QuadraPrize).ToArray(),
                    accumulated = batch.Select(x => x.Accumulated).ToArray(), nextEstimates = batch.Select(x => x.NextEstimate).ToArray(),
                    sources = batch.Select(x => x.Source).ToArray()
                });
            written += batch.Length;
            progress?.Invoke(written);
        }
        return written;
    }

    public async Task<MegaSenaDraw> SyncLatest(CancellationToken cancellationToken = default)
    {
        state.LastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var draw = await FetchNewest(cancellationToken);
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

    public object Status() => new { state.LastAttempt, state.LastSuccess, state.LastContest, state.LastSource, state.LastError,
        state.LastCaixaError, intervalMinutes = 5 };

    public static string Describe(Exception error)
    {
        var root = error.GetBaseException();
        return root == error ? $"{error.GetType().Name}: {error.Message}" : $"{error.GetType().Name}: {error.Message} ({root.GetType().Name}: {root.Message})";
    }

    public async Task<MegaSenaHistorySyncResult> SyncHistory(CancellationToken cancellationToken,
        Action<int, int, string>? progress = null, TimeSpan? throttle = null)
    {
        progress?.Invoke(0, 0, "Consultando o último concurso");
        var latest = await SyncLatest(cancellationToken);
        progress?.Invoke(0, 0, $"Último concurso: {latest.Contest} ({latest.Source})");
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
                var valid = new List<MegaSenaDraw>();
                foreach (var draw in await FetchAllFromFallback(cancellationToken))
                {
                    if (!wanted.Contains(draw.Contest)) continue;
                    try { Validate(draw); valid.Add(draw); wanted.Remove(draw.Contest); }
                    catch (InvalidDataException error) { logger.LogWarning(error, "Concurso {Contest} inválido no lote.", draw.Contest); }
                }
                progress?.Invoke(0, missing.Count, $"Gravando {valid.Count} concursos");
                imported = await UpsertMany(valid, written => progress?.Invoke(written, missing.Count, $"Gravando concursos ({written} de {valid.Count})"));
                missing = wanted.Order().ToList();
                progress?.Invoke(imported, imported + missing.Count, $"{imported} concursos importados em lote");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Carga em lote: {Describe(error)}");
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
                errors.Add($"Concurso {contest}: {Describe(error)}");
            }
            progress?.Invoke(imported + errors.Count, total, $"Concurso {contest}");
            await Task.Delay(throttle ?? TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        return new MegaSenaHistorySyncResult(latest.Contest, alreadyPresent, imported, errors);
    }

    private async Task<string> GetString(string url, CancellationToken cancellationToken, string accept = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
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
