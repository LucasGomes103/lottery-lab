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
    private static readonly string[] Animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA",
        "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO",
        "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];

    private sealed record Row(long ExtractionId, DateTime Date, TimeSpan Time, int Position, string Number);
    private sealed record Scored(string Milhar, string Centena, string Dezena, int Group,
        double StatisticalScore, double FinalScore, PredictionFeatures Features, List<string> Reasons);
    private sealed record StoredCandidate(int Rank, string Milhar, string Centena, string Dezena, int Group,
        string SelectionType, double StatisticalScore, double FinalScore, string FeaturesJson, string ReasonsJson);
    private sealed record EvaluationRow(long ExtractionId, bool HitMilhar, bool HitCentena, bool HitDezena,
        int MilharHitCount, int CentenaHitCount, int DezenaHitCount,
        int? BestMilharPosition, int? BestCentenaPosition, int? BestDezenaPosition, DateTime EvaluatedAt);
    private sealed record PredictionTarget(string Bank, DateTime TargetDate, TimeSpan TargetTime);

    public async Task<PredictionResponse> GenerateAndSave(PredictionRequest request)
    {
        var bank = request.Bank.Trim();
        var date = request.TargetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        if (!TimeOnly.TryParse(request.Time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        var windowDays = Math.Clamp(request.WindowDays, 7, 3650);
        var quantity = Math.Clamp(request.Quantity, 1, 100);
        var requestedGroups = (request.Groups ?? []).Where(x => x is >= 1 and <= 25).Distinct().Order().ToArray();
        var groupKey = requestedGroups.Length == 0 ? "ALL" : string.Join('-', requestedGroups);
        var seed = StableSeed($"{Algorithm}:{Version}:{bank}:{date:yyyy-MM-dd}:{targetTime:HH:mm}:{windowDays}:{quantity}:{groupKey}");

        await using var connection = db.Open();
        var target = date.ToDateTime(targetTime);
        var start = target.AddDays(-windowDays);
        var rows = (await connection.QueryAsync<Row>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                     r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_date>=@start
                and (e.extraction_date<@date or (e.extraction_date=@date and e.extraction_time<@time::time))
                and r.position between 1 and 5
              order by e.extraction_date,e.extraction_time,r.position",
            new { bank, start = start.Date, date = target.Date, time = targetTime.ToString("HH:mm") })).ToList();

        var priorAppearances = (await connection.QueryAsync<string>(
            @"select pc.milhar from prediction_candidates pc join predictions p on p.id=pc.prediction_id
              where p.bank=@bank and p.target_time=@time::time order by p.generated_at desc limit 500",
            new { bank, time = targetTime.ToString("HH:mm") })).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

        var ranked = Score(rows, targetTime, priorAppearances);
        if (requestedGroups.Length > 0) ranked = ranked.Where(x => requestedGroups.Contains(x.Group)).ToList();
        var selected = Select(ranked, quantity, seed, requestedGroups.Length > 0);
        var id = Guid.NewGuid();
        var sampleExtractions = rows.Select(x => x.ExtractionId).Distinct().Count();
        var robustness = sampleExtractions < 30 ? "INSUFICIENTE" : sampleExtractions < 100 ? "BAIXA" : "EXPERIMENTAL";
        var composition = new
        {
            exploitation = selected.Count(x => x.SelectionType == "EXPLOITATION"),
            emerging = selected.Count(x => x.SelectionType == "EMERGING"),
            exploration = selected.Count(x => x.SelectionType == "EXPLORATION"),
            deterministicSeed = seed,
            restrictedGroups = requestedGroups
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
        await EvaluatePending(bank, date, targetTime.ToString("HH:mm"));

        return new PredictionResponse(id, Algorithm, Version, bank, targetTime.ToString("HH:mm"), date,
            windowDays, quantity, seed, sampleExtractions, rows.Count, robustness, composition, selected,
            "Os scores são rankings estatísticos, não probabilidades nem garantia de acerto. A vantagem deve ser confirmada por backtest fora da amostra.");
    }

    public async Task<AnimalTrendResponse> AnimalTrends(string bank, string time, DateOnly targetDate, int windowDays)
    {
        if (!TimeOnly.TryParse(time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        await using var connection = db.Open();
        var rows = (await connection.QueryAsync<(long ExtractionId, DateTime Date, string Number)>(
            @"select e.id as ExtractionId,e.extraction_date as Date,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_time=@time::time and e.extraction_date<@targetDate
                and e.extraction_date>=@startDate and r.position between 1 and 5
              order by e.extraction_date,r.position",
            new { bank, time = targetTime.ToString("HH:mm"), targetDate = targetDate.ToDateTime(TimeOnly.MinValue),
                startDate = targetDate.AddDays(-windowDays).ToDateTime(TimeOnly.MinValue) })).ToList();
        var groups = rows.Select(x => GroupOf(x.Number)).ToList();
        var recent = groups.TakeLast(60).ToList();
        var frequencyCounts = groups.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var recentCounts = recent.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var maxFrequency = Math.Max(1, frequencyCounts.Values.DefaultIfEmpty().Max());
        var maxRecent = Math.Max(1, recentCounts.Values.DefaultIfEmpty().Max());
        var ranked = Enumerable.Range(1, 25).Select(group =>
        {
            var frequency = frequencyCounts.GetValueOrDefault(group) / (double)maxFrequency;
            var recentStrength = recentCounts.GetValueOrDefault(group) / (double)maxRecent;
            var last = groups.FindLastIndex(x => x == group);
            var delay = last < 0 ? 1 : Math.Clamp((groups.Count - 1 - last) / 60d, 0, 1);
            var score = 100 * (.50 * frequency + .40 * recentStrength + .10 * delay);
            var reasons = new List<(double Value, string Text)> { (frequency, "frequência no horário"),
                (recentStrength, "força nas últimas 10 extrações"), (delay, "atraso como sinal secundário") };
            var dezenas = Enumerable.Range((group - 1) * 4 + 1, 4).Select(x => (x % 100).ToString("00")).ToList();
            return new { group, score, frequency, recentStrength, delay, dezenas,
                reasons = reasons.OrderByDescending(x => x.Value).Take(2).Select(x => x.Text).ToList() };
        }).OrderByDescending(x => x.score).ThenBy(x => x.group)
          .Select((x, index) => new AnimalTrend(index + 1, x.group, Animals[x.group - 1], x.dezenas,
              Math.Round(x.score, 2), Math.Round(x.frequency * 100, 2), Math.Round(x.recentStrength * 100, 2),
              Math.Round(x.delay * 100, 2), x.reasons)).ToList();
        return new AnimalTrendResponse(bank, targetTime.ToString("HH:mm"), targetDate, windowDays,
            rows.Select(x => x.ExtractionId).Distinct().Count(), ranked,
            "Tendência é um ranking histórico descritivo e não altera a probabilidade matemática do sorteio.");
    }

    public async Task<object> List(string? bank, DateOnly? targetDate, string? time, string? status, int page, int pageSize)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var connection = db.Open();
        var where = new List<string>();
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(bank)) { where.Add("p.bank ilike @bank"); args.Add("bank", $"%{bank.Trim()}%"); }
        if (targetDate is not null) { where.Add("p.target_date=@targetDate"); args.Add("targetDate", targetDate.Value.ToDateTime(TimeOnly.MinValue)); }
        if (!string.IsNullOrWhiteSpace(time)) { where.Add("p.target_time=@time::time"); args.Add("time", time); }
        if (!string.IsNullOrWhiteSpace(status)) { where.Add("p.status=@status"); args.Add("status", status.Trim().ToUpperInvariant()); }
        var filter = where.Count == 0 ? "" : "where " + string.Join(" and ", where);
        args.Add("offset", (page - 1) * pageSize);
        args.Add("pageSize", pageSize);
        var total = await connection.ExecuteScalarAsync<long>($"select count(*) from predictions p {filter}", args);
        var items = await connection.QueryAsync($@"select p.id,p.bank,p.target_date,p.target_time,p.algorithm_code,
            p.algorithm_version,p.quantity,p.robustness,p.status,p.generated_at,
            pe.hit_milhar,pe.hit_centena,pe.hit_dezena,
            pe.milhar_hit_count,pe.centena_hit_count,pe.dezena_hit_count
            from predictions p left join prediction_evaluations pe on pe.prediction_id=p.id {filter}
            order by p.generated_at desc offset @offset limit @pageSize", args);
        return new { items, total, page, pageSize, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) };
    }

    public async Task<object?> Detail(Guid id)
    {
        await using var connection = db.Open();
        var prediction = await connection.QuerySingleOrDefaultAsync("select * from predictions where id=@id", new { id });
        if (prediction is null) return null;
        var target = await connection.QuerySingleAsync<PredictionTarget>(
            @"select bank as Bank,target_date as TargetDate,target_time as TargetTime
              from predictions where id=@id", new { id });
        var candidates = (await connection.QueryAsync<StoredCandidate>(
            @"select rank,milhar,centena,dezena,group_no as ""Group"",selection_type as SelectionType,
                     statistical_score::double precision as StatisticalScore,
                     final_score::double precision as FinalScore,
                     features::text as FeaturesJson,reasons::text as ReasonsJson
              from prediction_candidates where prediction_id=@id order by rank", new { id })).ToList();
        var evaluation = await connection.QuerySingleOrDefaultAsync<EvaluationRow>(
            @"select extraction_id as ExtractionId,hit_milhar as HitMilhar,hit_centena as HitCentena,
                     hit_dezena as HitDezena,milhar_hit_count as MilharHitCount,
                     centena_hit_count as CentenaHitCount,dezena_hit_count as DezenaHitCount,
                     best_milhar_position as BestMilharPosition,
                     best_centena_position as BestCentenaPosition,best_dezena_position as BestDezenaPosition,
                     evaluated_at as EvaluatedAt
              from prediction_evaluations where prediction_id=@id", new { id });
        var actual = evaluation is null ? [] : (await connection.QueryAsync<(int Position, string Number)>(
            "select position,number from results where extraction_id=@id and position between 1 and 5 order by position",
            new { id = evaluation.ExtractionId })).ToList();
        var dayResults = (await connection.QueryAsync<(TimeSpan Time, int Position, string Number)>(
            @"select e.extraction_time as Time,r.position as Position,r.number as Number
              from extractions e join results r on r.extraction_id=e.id
              where e.bank=@bank and e.extraction_date=@date and r.position between 1 and 5
              order by e.extraction_time,r.position",
            new { bank = target.Bank, date = target.TargetDate.Date })).ToList();
        var beforeTarget = dayResults.Where(x => x.Time < target.TargetTime).ToList();
        var detailed = candidates.Select(candidate =>
        {
            var milharMatches = actual.Where(x => x.Number == candidate.Milhar).Select(x => new { x.Position, x.Number }).ToList();
            var centenaMatches = actual.Where(x => x.Number.EndsWith(candidate.Centena)).Select(x => new { x.Position, x.Number }).ToList();
            var dezenaMatches = actual.Where(x => x.Number.EndsWith(candidate.Dezena)).Select(x => new { x.Position, x.Number }).ToList();
            object Match(TimeSpan matchTime, int position, string number) => new
            {
                Time = $"{matchTime.Hours:00}:{matchTime.Minutes:00}", Position = position, Number = number
            };
            var beforeMilhar = beforeTarget.Where(x => x.Number == candidate.Milhar).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var beforeCentena = beforeTarget.Where(x => x.Number.EndsWith(candidate.Centena)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var beforeDezena = beforeTarget.Where(x => x.Number.EndsWith(candidate.Dezena)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayMilhar = dayResults.Where(x => x.Number == candidate.Milhar).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayCentena = dayResults.Where(x => x.Number.EndsWith(candidate.Centena)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayDezena = dayResults.Where(x => x.Number.EndsWith(candidate.Dezena)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            return new
            {
                candidate.Rank, candidate.Milhar, candidate.Centena, candidate.Dezena, candidate.Group,
                candidate.SelectionType, candidate.StatisticalScore, candidate.FinalScore,
                Features = JsonSerializer.Deserialize<JsonElement>(candidate.FeaturesJson),
                Reasons = JsonSerializer.Deserialize<List<string>>(candidate.ReasonsJson) ?? [],
                Hits = new
                {
                    Milhar = milharMatches.Count > 0, Centena = centenaMatches.Count > 0, Dezena = dezenaMatches.Count > 0,
                    MilharMatches = milharMatches, CentenaMatches = centenaMatches, DezenaMatches = dezenaMatches
                },
                BeforeTargetHits = new { Milhar = beforeMilhar.Count > 0, Centena = beforeCentena.Count > 0,
                    Dezena = beforeDezena.Count > 0, MilharMatches = beforeMilhar, CentenaMatches = beforeCentena,
                    DezenaMatches = beforeDezena },
                WholeDayHits = new { Milhar = dayMilhar.Count > 0, Centena = dayCentena.Count > 0,
                    Dezena = dayDezena.Count > 0, MilharMatches = dayMilhar, CentenaMatches = dayCentena,
                    DezenaMatches = dayDezena }
            };
        }).ToList();
        var beforeSummary = new { MilharHits = detailed.Sum(x => x.BeforeTargetHits.MilharMatches.Count),
            CentenaHits = detailed.Sum(x => x.BeforeTargetHits.CentenaMatches.Count),
            DezenaHits = detailed.Sum(x => x.BeforeTargetHits.DezenaMatches.Count) };
        var wholeDaySummary = new { MilharHits = detailed.Sum(x => x.WholeDayHits.MilharMatches.Count),
            CentenaHits = detailed.Sum(x => x.WholeDayHits.CentenaMatches.Count),
            DezenaHits = detailed.Sum(x => x.WholeDayHits.DezenaMatches.Count) };
        return new
        {
            prediction,
            candidates = detailed,
            evaluation,
            dayCheck = new
            {
                BeforeTarget = beforeSummary,
                WholeDay = wholeDaySummary,
                ImportedSchedules = dayResults.Select(x => x.Time).Distinct().Count()
            },
            actualResults = actual.Select(x => new
            {
                x.Position, x.Number, Centena = x.Number[^3..], Dezena = x.Number[^2..], Group = GroupOf(x.Number)
            }),
            dayResults = dayResults.Select(x => new { Time = $"{x.Time.Hours:00}:{x.Time.Minutes:00}",
                x.Position, x.Number, Centena = x.Number[^3..], Dezena = x.Number[^2..], Group = GroupOf(x.Number) })
        };
    }

    public async Task<object?> Evaluate(Guid id)
    {
        await using var connection = db.Open();
        var target = await connection.QuerySingleOrDefaultAsync<PredictionTarget>(
            @"select bank as Bank,target_date as TargetDate,target_time as TargetTime
              from predictions where id=@id", new { id });
        if (target is null) return null;
        await EvaluatePending(target.Bank, DateOnly.FromDateTime(target.TargetDate),
            $"{target.TargetTime.Hours:00}:{target.TargetTime.Minutes:00}");
        return await Detail(id);
    }

    public async Task<int> Delete(IEnumerable<Guid> ids)
    {
        var uniqueIds = ids.Distinct().ToArray();
        if (uniqueIds.Length == 0) return 0;
        await using var connection = db.Open();
        return await connection.ExecuteAsync("delete from predictions where id=any(@ids)", new { ids = uniqueIds });
    }

    public async Task<object> Statistics(string? bank, DateOnly? startDate, DateOnly? endDate, string? time)
    {
        await using var connection = db.Open();
        var where = new List<string>();
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(bank)) { where.Add("p.bank ilike @bank"); args.Add("bank", $"%{bank.Trim()}%"); }
        if (startDate is not null) { where.Add("p.target_date>=@startDate"); args.Add("startDate", startDate.Value.ToDateTime(TimeOnly.MinValue)); }
        if (endDate is not null) { where.Add("p.target_date<=@endDate"); args.Add("endDate", endDate.Value.ToDateTime(TimeOnly.MinValue)); }
        if (!string.IsNullOrWhiteSpace(time)) { where.Add("p.target_time=@time::time"); args.Add("time", time); }
        var filter = where.Count == 0 ? "" : "where " + string.Join(" and ", where);
        var evaluatedFilter = where.Count == 0 ? "where pe.prediction_id is not null" : filter + " and pe.prediction_id is not null";

        var totals = await connection.QuerySingleAsync($@"
            select count(*)::int as total_predictions,
                   count(*) filter(where pe.prediction_id is null)::int as pending_predictions,
                   count(pe.prediction_id)::int as evaluated_predictions,
                   count(*) filter(where pe.hit_milhar)::int as predictions_with_milhar_hit,
                   count(*) filter(where pe.hit_centena)::int as predictions_with_centena_hit,
                   count(*) filter(where pe.hit_dezena)::int as predictions_with_dezena_hit,
                   coalesce(sum(pe.milhar_hit_count),0)::int as milhar_hits,
                   coalesce(sum(pe.centena_hit_count),0)::int as centena_hits,
                   coalesce(sum(pe.dezena_hit_count),0)::int as dezena_hits,
                   coalesce(sum(p.quantity) filter(where pe.prediction_id is not null),0)::int as evaluated_candidates
            from predictions p left join prediction_evaluations pe on pe.prediction_id=p.id {filter}", args);
        var byTime = await connection.QueryAsync($@"
            select p.target_time, count(*)::int as evaluated_predictions,
                   count(*) filter(where pe.hit_milhar)::int as predictions_with_milhar_hit,
                   count(*) filter(where pe.hit_centena)::int as predictions_with_centena_hit,
                   count(*) filter(where pe.hit_dezena)::int as predictions_with_dezena_hit,
                   coalesce(sum(pe.milhar_hit_count),0)::int as milhar_hits,
                   coalesce(sum(pe.centena_hit_count),0)::int as centena_hits,
                   coalesce(sum(pe.dezena_hit_count),0)::int as dezena_hits
            from predictions p join prediction_evaluations pe on pe.prediction_id=p.id {evaluatedFilter}
            group by p.target_time order by p.target_time", args);
        var byDate = await connection.QueryAsync($@"
            select p.target_date, count(*)::int as evaluated_predictions,
                   coalesce(sum(pe.milhar_hit_count),0)::int as milhar_hits,
                   coalesce(sum(pe.centena_hit_count),0)::int as centena_hits,
                   coalesce(sum(pe.dezena_hit_count),0)::int as dezena_hits
            from predictions p join prediction_evaluations pe on pe.prediction_id=p.id {evaluatedFilter}
            group by p.target_date order by p.target_date desc limit 30", args);
        return new { totals, byTime, byDate };
    }

    public async Task EvaluatePending(string bank, DateOnly date, string time)
    {
        await using var connection = db.Open();
        var extraction = await connection.QuerySingleOrDefaultAsync<long?>(
            "select id from extractions where bank=@bank and extraction_date=@date and extraction_time=@time::time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue), time });
        if (extraction is null) return;
        var actual = (await connection.QueryAsync<(int Position, string Number)>(
            "select position,number from results where extraction_id=@id and position between 1 and 5", new { id = extraction })).ToList();
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
            var milharHitCount = candidates.Sum(c => actual.Count(a => c.Milhar == a.Number));
            var centenaHitCount = candidates.Sum(c => actual.Count(a => c.Centena == a.Number[^3..]));
            var dezenaHitCount = candidates.Sum(c => actual.Count(a => c.Dezena == a.Number[^2..]));
            var details = JsonSerializer.Serialize(new { actual = actual.Select(x => x.Number) }, JsonOptions);
            await connection.ExecuteAsync(
                @"insert into prediction_evaluations(prediction_id,extraction_id,hit_milhar,hit_centena,hit_dezena,
                    milhar_hit_count,centena_hit_count,dezena_hit_count,
                    best_milhar_position,best_centena_position,best_dezena_position,details)
                  values(@predictionId,@extraction,@hitMilhar,@hitCentena,@hitDezena,
                    @milharHitCount,@centenaHitCount,@dezenaHitCount,@milharPos,@centenaPos,@dezenaPos,@details::jsonb)
                  on conflict(prediction_id) do update set
                    extraction_id=excluded.extraction_id,evaluated_at=now(),
                    hit_milhar=excluded.hit_milhar,hit_centena=excluded.hit_centena,hit_dezena=excluded.hit_dezena,
                    milhar_hit_count=excluded.milhar_hit_count,centena_hit_count=excluded.centena_hit_count,
                    dezena_hit_count=excluded.dezena_hit_count,
                    best_milhar_position=excluded.best_milhar_position,best_centena_position=excluded.best_centena_position,
                    best_dezena_position=excluded.best_dezena_position,details=excluded.details;
                  update predictions set status='EVALUATED' where id=@predictionId",
                new { predictionId, extraction, hitMilhar = milharPos is not null, hitCentena = centenaPos is not null,
                    hitDezena = dezenaPos is not null, milharHitCount, centenaHitCount, dezenaHitCount,
                    milharPos, centenaPos, dezenaPos, details });
        }
    }

    public async Task<int> ReevaluateAll()
    {
        List<PredictionTarget> targets;
        await using (var connection = db.Open())
        {
            targets = (await connection.QueryAsync<PredictionTarget>(
                @"select distinct bank as Bank,target_date as TargetDate,target_time as TargetTime
                  from predictions order by target_date,target_time")).ToList();
        }

        foreach (var target in targets)
            await EvaluatePending(target.Bank, DateOnly.FromDateTime(target.TargetDate),
                $"{target.TargetTime.Hours:00}:{target.TargetTime.Minutes:00}");
        return targets.Count;
    }

    public async Task<object> CompareWindows(string bank, DateOnly date, int quantity, int[] requestedWindows,
        bool useSameDayResults)
    {
        quantity = Math.Clamp(quantity, 1, 100);
        var windows = requestedWindows.Where(x => x is >= 7 and <= 3650).Distinct().Order().ToArray();
        if (windows.Length == 0) windows = [30, 60, 90, 120, 180, 240];
        await using var connection = db.Open();
        var schedules = (await connection.QueryAsync<TimeSpan>(
            @"select distinct extraction_time from extractions
              where bank=@bank and extraction_date=@date order by extraction_time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue) })).ToList();
        var comparisons = new List<object>();

        foreach (var windowDays in windows)
        {
            var scheduleResults = new List<object>();
            var totalMilhar = 0; var totalCentena = 0; var totalDezena = 0;
            foreach (var schedule in schedules)
            {
                var targetTime = TimeOnly.FromTimeSpan(schedule);
                var target = date.ToDateTime(targetTime);
                var rows = (await connection.QueryAsync<Row>(
                    @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                             r.position as Position,r.number as Number
                      from results r join extractions e on e.id=r.extraction_id
                      where e.bank=@bank and e.extraction_date>=@start
                        and (e.extraction_date<@date or (@useSameDayResults and e.extraction_date=@date and e.extraction_time<@time::time))
                        and r.position between 1 and 5
                      order by e.extraction_date,e.extraction_time,r.position",
                    new { bank, start = target.AddDays(-windowDays).Date, date = target.Date,
                        time = targetTime.ToString("HH:mm"), useSameDayResults })).ToList();
                var priorAppearances = (await connection.QueryAsync<string>(
                    @"select pc.milhar from prediction_candidates pc join predictions p on p.id=pc.prediction_id
                      where p.bank=@bank and p.target_time=@time::time and p.target_date<@date
                      order by p.generated_at desc limit 500",
                    new { bank, time = targetTime.ToString("HH:mm"), date = target.Date }))
                    .GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
                var seed = StableSeed($"{Algorithm}:{Version}:{bank}:{date:yyyy-MM-dd}:{targetTime:HH:mm}:{windowDays}:{quantity}:ALL");
                var selected = Select(Score(rows, targetTime, priorAppearances), quantity, seed, false);
                var actual = (await connection.QueryAsync<string>(
                    @"select r.number from results r join extractions e on e.id=r.extraction_id
                      where e.bank=@bank and e.extraction_date=@date and e.extraction_time=@time::time
                        and r.position between 1 and 5 order by r.position",
                    new { bank, date = target.Date, time = targetTime.ToString("HH:mm") })).ToList();
                var milhar = selected.Sum(c => actual.Count(x => x == c.Milhar));
                var centena = selected.Sum(c => actual.Count(x => x.EndsWith(c.Centena)));
                var dezena = selected.Sum(c => actual.Count(x => x.EndsWith(c.Dezena)));
                totalMilhar += milhar; totalCentena += centena; totalDezena += dezena;
                scheduleResults.Add(new { time = targetTime.ToString("HH:mm"), sampleExtractions = rows.Select(x => x.ExtractionId).Distinct().Count(),
                    milharHits = milhar, centenaHits = centena, dezenaHits = dezena });
            }
            comparisons.Add(new { windowDays, evaluatedSchedules = schedules.Count, milharHits = totalMilhar,
                centenaHits = totalCentena, dezenaHits = totalDezena, schedules = scheduleResults });
        }
        return new { bank, date, quantity, prizeRange = "1-5", useSameDayResults, comparisons };
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

    private static List<PredictionCandidate> Select(List<Scored> ranked, int quantity, long seed, bool restrictedToGroups)
    {
        if (ranked.Count == 0) return [];
        var exploitationTarget = (int)Math.Round(quantity * .6, MidpointRounding.AwayFromZero);
        var emergingTarget = (int)Math.Round(quantity * .2, MidpointRounding.AwayFromZero);
        var explorationTarget = quantity - exploitationTarget - emergingTarget;
        var selected = new List<(Scored Candidate, string Type)>();
        var usedC = new HashSet<string>(); var usedD = new HashSet<string>(); var groups = new Dictionary<int, int>();
        bool Add(Scored x, string type)
        {
            if (usedC.Contains(x.Centena) || (!restrictedToGroups && usedD.Contains(x.Dezena)) ||
                (!restrictedToGroups && groups.GetValueOrDefault(x.Group) >= 2)) return false;
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
