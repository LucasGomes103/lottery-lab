using System.Text.Json;
using Dapper;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed partial class PredictionService
{
    private sealed record TernoStored(Guid Id, string Bank, DateTime TargetDate, TimeSpan TargetTime,
        int Quantity, int WindowDays, int FirstPrize, int LastPrize, string GamesJson, DateTime GeneratedAt, decimal TotalStake);

    public async Task<object> GenerateTernos(TernoRequest request)
    {
        if (!TimeOnly.TryParse(request.Time, out var time)) throw new ArgumentException("Horário inválido.");
        if (request.TotalStake <= 0 || request.TotalStake > 1_000_000m || decimal.Round(request.TotalStake, 2) != request.TotalStake)
            throw new ArgumentException("Informe um valor total positivo com até duas casas decimais, limitado a R$ 1.000.000.");
        if (request.Quantity < 1 || request.Quantity > 10_000)
            throw new ArgumentException("Informe entre 1 e 10000 ternos.");
        if (request.WindowDays < 7 || request.WindowDays > 3650)
            throw new ArgumentException("A janela deve ser de 7 a 3650 dias.");
        var groups = (request.Groups ?? []).Distinct().ToArray();
        if (groups.Any(x => x < 1 || x > 25)) throw new ArgumentException("Animal inválido.");
        var bank = request.Bank.Trim();
        var target = request.TargetDate.ToDateTime(time);
        await using var connection = db.Open();
        var rows = (await connection.QueryAsync<Row>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                     r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_date>=@start
                and (e.extraction_date<@date or (e.extraction_date=@date and e.extraction_time<@time::time))
                and r.position between @first and @last
              order by e.extraction_date,e.extraction_time,r.position",
            new { bank, start = target.AddDays(-request.WindowDays).Date, date = target.Date,
                time = time.ToString("HH:mm"), first = 1, last = 5 })).ToList();
        if (rows.Count == 0) throw new ArgumentException("Não há histórico no período selecionado para gerar ternos.");
        var scores = Score(rows, time, []).Where(x => groups.Length == 0 || groups.Contains(x.Group))
            .GroupBy(x => x.Dezena).ToDictionary(g => g.Key, g => g.Average(x => x.FinalScore));
        var games = BuildTernos(scores, request.Quantity);
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            @"insert into terno_predictions(id,bank,target_date,target_time,quantity,window_days,first_prize,last_prize,games,groups,total_stake)
              values(@id,@bank,@date,@time::time,@Quantity,@WindowDays,1,5,@games::jsonb,@groups::jsonb,@TotalStake)",
            new { id, bank, date = target.Date, time = time.ToString("HH:mm"), request.Quantity, request.WindowDays,
                request.TotalStake, games = JsonSerializer.Serialize(games, JsonOptions),
                groups = JsonSerializer.Serialize(groups) });
        return await GetTerno(id) ?? throw new InvalidOperationException("Terno não encontrado após gravação.");
    }

    public static List<TernoGame> BuildTernos(IReadOnlyDictionary<string, double> scores, int quantity)
    {
        var dezenas = scores.Keys.Order(StringComparer.Ordinal).ToArray();
        var capacity = dezenas.Length * (dezenas.Length - 1) * (dezenas.Length - 2) / 6;
        if (quantity < 1 || quantity > Math.Min(10_000, capacity))
            throw new ArgumentException($"A seleção permite no máximo {Math.Min(10_000, capacity)} ternos distintos.");
        var combinations = new List<TernoGame>(capacity);
        for (var a = 0; a < dezenas.Length - 2; a++)
        for (var b = a + 1; b < dezenas.Length - 1; b++)
        for (var c = b + 1; c < dezenas.Length; c++)
        {
            var trio = new[] { dezenas[a], dezenas[b], dezenas[c] };
            combinations.Add(new TernoGame(0, trio, Math.Round(trio.Average(d => scores[d]), 4)));
        }
        return combinations.OrderByDescending(x => x.Score).ThenBy(x => string.Join('-', x.Dezenas), StringComparer.Ordinal)
            .Take(quantity).Select((x, index) => x with { Rank = index + 1 }).ToList();
    }

    public static TernoResult CheckTerno(TernoGame game, IEnumerable<(int Position, string Number)> results,
        int firstPrize, int lastPrize)
    {
        var actual = results.Where(x => x.Position >= firstPrize && x.Position <= lastPrize)
            .Select(x => x.Number.PadLeft(2, '0')[^2..]).ToHashSet();
        var matched = game.Dezenas.Count(actual.Contains);
        return new TernoResult(game.Rank, game.Dezenas, game.Score, matched, matched == 3);
    }

    public async Task<object?> GetTerno(Guid id, bool includeGames = true)
    {
        await using var connection = db.Open();
        var stored = await connection.QuerySingleOrDefaultAsync<TernoStored>(
            @"select id,bank,target_date as TargetDate,target_time as TargetTime,quantity,window_days as WindowDays,
                first_prize as FirstPrize,last_prize as LastPrize,games::text as GamesJson,generated_at as GeneratedAt,total_stake as TotalStake
              from terno_predictions where id=@id", new { id });
        if (stored is null) return null;
        var actual = (await connection.QueryAsync<(int Position, string Number)>(
            @"select r.position,r.number from results r join extractions e on e.id=r.extraction_id
              where e.bank=@Bank and e.extraction_date=@date and e.extraction_time=@time
                and r.position between @FirstPrize and @LastPrize order by r.position",
            new { stored.Bank, date = stored.TargetDate.Date, time = stored.TargetTime, stored.FirstPrize, stored.LastPrize })).ToList();
        var complete = Enumerable.Range(stored.FirstPrize, stored.LastPrize - stored.FirstPrize + 1)
            .All(p => actual.Any(x => x.Position == p));
        var games = (JsonSerializer.Deserialize<List<TernoGame>>(stored.GamesJson, JsonOptions) ?? [])
            .Select(game => CheckTerno(game, actual, stored.FirstPrize, stored.LastPrize)).ToList();
        var payoutPerHit = TernoPayout(stored.TotalStake, stored.Quantity);
        decimal? returnAmount = complete ? games.Count(x => x.Hit) * payoutPerHit : null;
        return new { stored.Id, stored.Bank, targetDate = stored.TargetDate.ToString("yyyy-MM-dd"),
            time = $"{stored.TargetTime.Hours:00}:{stored.TargetTime.Minutes:00}", stored.Quantity, stored.WindowDays,
            stored.FirstPrize, stored.LastPrize, stored.GeneratedAt, status = complete ? "EVALUATED" : "PENDING",
            hits = complete ? games.Count(x => x.Hit) : (int?)null, games = includeGames ? games : null,
            stored.TotalStake, stakePerGame = stored.TotalStake / stored.Quantity, quote = 13000,
            payoutPerHit, returnAmount, profitAmount = returnAmount - stored.TotalStake,
            actualResults = actual.Select(x => new { x.Position, x.Number }),
            warning = "O score ordena as dezenas pelo histórico; não representa a probabilidade de acerto do terno." };
    }

    public static decimal TernoPayout(decimal totalStake, int quantity) => Math.Round(totalStake / quantity * 13000m, 2);

    public async Task<object> ListTernos(string? bank, int page = 1)
    {
        page = Math.Max(1, page);
        await using var connection = db.Open();
        var args = new { bank = string.IsNullOrWhiteSpace(bank) ? null : bank.Trim(), offset = (page - 1) * 20 };
        var total = await connection.ExecuteScalarAsync<int>("select count(*) from terno_predictions where (@bank::text is null or bank=@bank)", args);
        page = Math.Min(page, Math.Max(1, (int)Math.Ceiling(total / 20d)));
        args = new { args.bank, offset = (page - 1) * 20 };
        var ids = await connection.QueryAsync<Guid>(
            "select id from terno_predictions where (@bank::text is null or bank=@bank) order by generated_at desc,id offset @offset limit 20", args);
        var items = new List<object>();
        foreach (var id in ids) if (await GetTerno(id, includeGames: false) is { } item) items.Add(item);
        return new { items, total, page, totalPages = (int)Math.Ceiling(total / 20d) };
    }

    public async Task<int> DeleteTernos(List<Guid> ids)
    {
        await using var connection = db.Open();
        return await connection.ExecuteAsync("delete from terno_predictions where id=any(@ids)", new { ids = ids.Distinct().ToArray() });
    }
}
