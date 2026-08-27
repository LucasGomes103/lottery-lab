using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed class PredictionService(Db db)
{
    private const string Algorithm = "HYBRID_EXPLORATION";
    private const int Version = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Row(long ExtractionId, DateTime Date, TimeSpan Time, int Position, string Number);
    private sealed record Scored(string Milhar, string Centena, string Dezena, int Group,
        double StatisticalScore, double FinalScore, PredictionFeatures Features, List<string> Reasons);

    public async Task<PredictionResponse> GenerateAndSave(PredictionRequest request)
    {
        var bank = request.Bank.Trim();
        var date = request.TargetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        if (!TimeOnly.TryParse(request.Time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        var windowDays = Math.Clamp(request.WindowDays, 7, 3650);
        var quantity = Math.Clamp(request.Quantity, 1, 100);
        var seed = StableSeed($"{Algorithm}:{Version}:{bank}:{date:yyyy-MM-dd}:{targetTime:HH:mm}:{windowDays}:{quantity}");

        await using var connection = db.Open();
        var target = date.ToDateTime(targetTime);
        var start = target.AddDays(-windowDays);
        var rows = (await connection.QueryAsync<Row>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                     r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_date>=@start
                and (e.extraction_date<@date or (e.extraction_date=@date and e.extraction_time<@time::time))
                and r.position between 1 and 6
              order by e.extraction_date,e.extraction_time,r.position",
            new { bank, start = start.Date, date = target.Date, time = targetTime.ToString("HH:mm") })).ToList();

        var priorAppearances = (await connection.QueryAsync<string>(
            @"select pc.milhar from prediction_candidates pc join predictions p on p.id=pc.prediction_id
              where p.bank=@bank and p.target_time=@time::time order by p.generated_at desc limit 500",
            new { bank, time = targetTime.ToString("HH:mm") })).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

        var selected = Select(Score(rows, targetTime, priorAppearances), quantity, seed);
        var id = Guid.NewGuid();
        var sampleExtractions = rows.Select(x => x.ExtractionId).Distinct().Count();
        var robustness = sampleExtractions < 30 ? "INSUFICIENTE" : sampleExtractions < 100 ? "BAIXA" : "EXPERIMENTAL";
        var composition = new
        {
            exploitation = selected.Count(x => x.SelectionType == "EXPLOITATION"),
            emerging = selected.Count(x => x.SelectionType == "EMERGING"),
            exploration = selected.Count(x => x.SelectionType == "EXPLORATION"),
            deterministicSeed = seed
        };

        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into predictions(id,bank,target_date,target_time,algorithm_code,algorithm_version,
                window_days,quantity,random_seed,sample_extractions,sample_results,robustness,config)
              values(@id,@bank,@date,@time::time,@Algorithm,@Version,@windowDays,@quantity,@seed,
                @sampleExtractions,@sampleResults,@robustness,@config::jsonb)",
            new { id, bank, date = target.Date, time = targetTime.ToString("HH:mm"), Algorithm, Version, windowDays,
                quantity, seed, sampleExtractions, sampleResults = rows.Count, robustness,
                config = JsonSerializer.Serialize(composition, JsonOptions) }, transaction);
        foreach (var candidate in selected)
            await connection.ExecuteAsync(
                @"insert into prediction_candidates(prediction_id,rank,milhar,centena,dezena,group_no,
                    selection_type,statistical_score,final_score,features,reasons)
                  values(@id,@Rank,@Milhar,@Centena,@Dezena,@Group,@SelectionType,@StatisticalScore,@FinalScore,
                    @features::jsonb,@reasons::jsonb)",
                new { id, candidate.Rank, candidate.Milhar, candidate.Centena, candidate.Dezena, candidate.Group,
                    candidate.SelectionType, candidate.StatisticalScore, candidate.FinalScore,
                    features = JsonSerializer.Serialize(candidate.Features, JsonOptions),
                    reasons = JsonSerializer.Serialize(candidate.Reasons, JsonOptions) }, transaction);
        await transaction.CommitAsync();

        return new PredictionResponse(id, Algorithm, Version, bank, targetTime.ToString("HH:mm"), date,
            windowDays, quantity, seed, sampleExtractions, rows.Count, robustness, composition, selected,
            "Os scores são rankings estatísticos, não probabilidades nem garantia de acerto. A vantagem deve ser confirmada por backtest fora da amostra.");
    }

    public async Task<object> List(string? bank, int page, int pageSize)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var connection = db.Open();
        var filter = string.IsNullOrWhiteSpace(bank) ? "" : "where p.bank ilike @bank";
        var args = new { bank = $"%{bank?.Trim()}%", offset = (page - 1) * pageSize, pageSize };
        var total = await connection.ExecuteScalarAsync<long>($"select count(*) from predictions p {filter}", args);
        var items = await connection.QueryAsync($@"select p.id,p.bank,p.target_date,p.target_time,p.algorithm_code,
            p.algorithm_version,p.quantity,p.robustness,p.status,p.generated_at,
            pe.hit_milhar,pe.hit_centena,pe.hit_dezena
            from predictions p left join prediction_evaluations pe on pe.prediction_id=p.id {filter}
            order by p.generated_at desc offset @offset limit @pageSize", args);
        return new { items, total, page, pageSize, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) };
    }

    public async Task<object?> Detail(Guid id)
    {
        await using var connection = db.Open();
        var prediction = await connection.QuerySingleOrDefaultAsync("select * from predictions where id=@id", new { id });
        if (prediction is null) return null;
        var candidates = await connection.QueryAsync("select * from prediction_candidates where prediction_id=@id order by rank", new { id });
        var evaluation = await connection.QuerySingleOrDefaultAsync("select * from prediction_evaluations where prediction_id=@id", new { id });
        return new { prediction, candidates, evaluation };
    }

    public async Task EvaluatePending(string bank, DateOnly date, string time)
    {
        await using var connection = db.Open();
        var extraction = await connection.QuerySingleOrDefaultAsync<long?>(
            "select id from extractions where bank=@bank and extraction_date=@date and extraction_time=@time::time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue), time });
        if (extraction is null) return;
        var actual = (await connection.QueryAsync<(int Position, string Number)>(
            "select position,number from results where extraction_id=@id and position between 1 and 6", new { id = extraction })).ToList();
        var predictions = await connection.QueryAsync<Guid>(
            @"select id from predictions where bank=@bank and target_date=@date and target_time=@time::time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue), time });
        foreach (var predictionId in predictions)
        {
            var candidates = (await connection.QueryAsync<(string Milhar, string Centena, string Dezena)>(
                "select milhar,centena,dezena from prediction_candidates where prediction_id=@predictionId", new { predictionId })).ToList();
            int? milharPos = actual.Where(a => candidates.Any(c => c.Milhar == a.Number)).Select(a => (int?)a.Position).Min();
            int? centenaPos = actual.Where(a => candidates.Any(c => c.Centena == a.Number[^3..])).Select(a => (int?)a.Position).Min();
            int? dezenaPos = actual.Where(a => candidates.Any(c => c.Dezena == a.Number[^2..])).Select(a => (int?)a.Position).Min();
            var details = JsonSerializer.Serialize(new { actual = actual.Select(x => x.Number) }, JsonOptions);
            await connection.ExecuteAsync(
                @"insert into prediction_evaluations(prediction_id,extraction_id,hit_milhar,hit_centena,hit_dezena,
                    best_milhar_position,best_centena_position,best_dezena_position,details)
                  values(@predictionId,@extraction,@hitMilhar,@hitCentena,@hitDezena,@milharPos,@centenaPos,@dezenaPos,@details::jsonb)
                  on conflict(prediction_id) do update set
                    extraction_id=excluded.extraction_id,evaluated_at=now(),
                    hit_milhar=excluded.hit_milhar,hit_centena=excluded.hit_centena,hit_dezena=excluded.hit_dezena,
                    best_milhar_position=excluded.best_milhar_position,best_centena_position=excluded.best_centena_position,
                    best_dezena_position=excluded.best_dezena_position,details=excluded.details;
                  update predictions set status='EVALUATED' where id=@predictionId",
                new { predictionId, extraction, hitMilhar = milharPos is not null, hitCentena = centenaPos is not null,
                    hitDezena = dezenaPos is not null, milharPos, centenaPos, dezenaPos, details });
        }
    }

    private static List<Scored> Score(List<Row> rows, TimeOnly targetTime, Dictionary<string, int> priorAppearances)
    {
        if (rows.Count == 0) return [];
        var extractions = rows.GroupBy(x => x.ExtractionId).Select(g => new
        {
            Id = g.Key, Date = g.First().Date.Date, Time = g.First().Time,
            Numbers = g.Select(x => x.Number.PadLeft(4, '0')[^4..]).ToList()
        }).ToList();
        var targetRows = extractions.Where(x => x.Time == targetTime.ToTimeSpan()).ToList();
        var recent = targetRows.TakeLast(5).SelectMany(x => x.Numbers).ToList();
        var longTerm = targetRows.TakeLast(30).SelectMany(x => x.Numbers).ToList();
        var all = extractions.SelectMany(x => x.Numbers).ToList();
        var currentGroups = extractions.Where(x => x.Date == extractions.Max(e => e.Date) && x.Time < targetTime.ToTimeSpan())
            .SelectMany(x => x.Numbers).Select(GroupOf).ToHashSet();
        var transitionTargets = targetRows.Where(t =>
            extractions.Where(e => e.Date == t.Date && e.Time < targetTime.ToTimeSpan())
                .SelectMany(e => e.Numbers).Select(GroupOf).Any(currentGroups.Contains)).SelectMany(x => x.Numbers).ToList();

        var freqM = Counts(all, x => x); var freqC = Counts(all, x => x[^3..]); var freqD = Counts(all, x => x[^2..]);
        var timeM = Counts(longTerm, x => x); var timeC = Counts(longTerm, x => x[^3..]); var timeD = Counts(longTerm, x => x[^2..]);
        var recentD = Counts(recent, x => x[^2..]); var longD = Counts(longTerm, x => x[^2..]);
        var transitionD = Counts(transitionTargets, x => x[^2..]);
        var digitCounts = new int[4, 10]; foreach (var n in longTerm) for (var p = 0; p < 4; p++) digitCounts[p, n[p] - '0']++;
        var maxPrior = Math.Max(1, priorAppearances.Values.DefaultIfEmpty().Max());

        return Enumerable.Range(0, 10_000).Select(value =>
        {
            var m = value.ToString("0000"); var c = m[^3..]; var d = m[^2..];
            var frequency = .15 * Norm(freqM, m) + .35 * Norm(freqC, c) + .50 * Norm(freqD, d);
            var timeFrequency = .15 * Norm(timeM, m) + .35 * Norm(timeC, c) + .50 * Norm(timeD, d);
            var continuity = Norm(recentD, d);
            var momentum = Math.Clamp(Norm(recentD, d) - Norm(longD, d), -1, 1);
            var transition = Norm(transitionD, d);
            var reversed = new string(d.Reverse().ToArray());
            var reversal = Norm(recentD, reversed);
            var digitAffinity = Enumerable.Range(0, 4).Average(p => digitCounts[p, m[p] - '0'] / (double)Math.Max(1, Enumerable.Range(0, 10).Max(digit => digitCounts[p, digit])));
            var lastIndex = longTerm.FindLastIndex(x => x == m || x.EndsWith(c) || x.EndsWith(d));
            var delay = lastIndex < 0 ? 1 : Math.Clamp((longTerm.Count - 1 - lastIndex) / 50d, 0, 1);
            var repetitionPenalty = priorAppearances.GetValueOrDefault(m) / (double)maxPrior;
            var novelty = 1 - repetitionPenalty;
            var features = new PredictionFeatures(frequency, timeFrequency, delay, continuity, transition,
                momentum, reversal, digitAffinity, novelty, repetitionPenalty);
            var statistical = 100 * (.22 * frequency + .12 * timeFrequency + .08 * delay + .12 * continuity +
                .12 * transition + .10 * Math.Max(0, momentum) + .05 * reversal + .09 * digitAffinity + .10 * novelty);
            var final = Math.Clamp(statistical - 8 * repetitionPenalty, 0, 100);
            var reasons = Explain(features);
            return new Scored(m, c, d, GroupOf(m), Math.Round(statistical, 4), Math.Round(final, 4), features, reasons);
        }).OrderByDescending(x => x.FinalScore).ThenBy(x => x.Milhar).ToList();
    }

    private static List<PredictionCandidate> Select(List<Scored> ranked, int quantity, long seed)
    {
        if (ranked.Count == 0) return [];
        var exploitationTarget = (int)Math.Round(quantity * .6, MidpointRounding.AwayFromZero);
        var emergingTarget = (int)Math.Round(quantity * .2, MidpointRounding.AwayFromZero);
        var explorationTarget = quantity - exploitationTarget - emergingTarget;
        var selected = new List<(Scored Candidate, string Type)>();
        var usedC = new HashSet<string>(); var usedD = new HashSet<string>(); var groups = new Dictionary<int, int>();
        bool Add(Scored x, string type)
        {
            if (usedC.Contains(x.Centena) || usedD.Contains(x.Dezena) || groups.GetValueOrDefault(x.Group) >= 2) return false;
            usedC.Add(x.Centena); usedD.Add(x.Dezena); groups[x.Group] = groups.GetValueOrDefault(x.Group) + 1;
            selected.Add((x, type)); return true;
        }
        foreach (var x in ranked) { if (selected.Count >= exploitationTarget) break; Add(x, "EXPLOITATION"); }
        foreach (var x in ranked.Where(x => x.Features.Momentum > .05).OrderByDescending(x => x.Features.Momentum).ThenByDescending(x => x.FinalScore))
        { if (selected.Count >= exploitationTarget + emergingTarget) break; Add(x, "EMERGING"); }
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        var pool = ranked.Take(Math.Max(500, ranked.Count * 40 / 100)).OrderBy(_ => random.NextDouble()).ToList();
        foreach (var x in pool) { if (selected.Count >= exploitationTarget + emergingTarget + explorationTarget) break; Add(x, "EXPLORATION"); }
        foreach (var x in ranked) { if (selected.Count >= quantity) break; Add(x, "EXPLORATION"); }
        return selected.Select((x, i) => new PredictionCandidate(i + 1, x.Candidate.Milhar, x.Candidate.Centena,
            x.Candidate.Dezena, x.Candidate.Group, x.Type, x.Candidate.StatisticalScore, x.Candidate.FinalScore,
            x.Candidate.Features, x.Candidate.Reasons)).ToList();
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> values, Func<string, string> key) =>
        values.GroupBy(key).ToDictionary(x => x.Key, x => x.Count());
    private static double Norm(Dictionary<string, int> values, string key) =>
        values.Count == 0 ? 0 : values.GetValueOrDefault(key) / (double)Math.Max(1, values.Values.Max());
    private static int GroupOf(string number) { var value = int.Parse(number[^2..]); return value == 0 ? 25 : (value + 3) / 4; }
    private static long StableSeed(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0) & long.MaxValue;
    private static List<string> Explain(PredictionFeatures f)
    {
        var values = new List<(double Value, string Text)>
        {
            (f.Frequency, "boa frequência combinada no histórico"), (f.TimeFrequency, "aderência ao horário escolhido"),
            (f.Continuity, "continuidade nas extrações recentes"), (f.Transition, "sinal nas transições entre horários"),
            (Math.Max(0, f.Momentum), "momento recente acima da média"), (f.Delay, "atraso moderado como sinal secundário"),
            (f.Reversal, "reversão de dezena presente"), (f.DigitAffinity, "dígitos compatíveis com o padrão histórico"),
            (f.Novelty, "diversificação em relação às previsões anteriores")
        };
        return values.OrderByDescending(x => x.Value).Take(3).Where(x => x.Value > 0).Select(x => x.Text).ToList();
    }
}
