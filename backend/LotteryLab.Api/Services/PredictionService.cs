using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;
using Npgsql;

namespace LotteryLab.Api.Services;

public sealed partial class PredictionService(Db db)
{
    private const string Algorithm = "HYBRID_EXPLORATION";
    private const int Version = 3;
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
        int? BestMilharPosition, int? BestCentenaPosition, int? BestDezenaPosition, DateTime EvaluatedAt,
        decimal ReturnAmount, decimal ProfitAmount);
    private sealed record PredictionTarget(string Bank, DateTime TargetDate, TimeSpan TargetTime);
    private sealed record PredictionFinancial(decimal BetAmount, decimal DezenaPayout,
        decimal CentenaPayout, decimal MilharPayout, int PrizeRange);
    private sealed record DailyWindowChoice(int WindowDays, int EvaluatedDays, int MilharHits, int CentenaHits,
        int DezenaHits, string Methodology);
    // A janela diária só depende de resultados anteriores ao dia alvo, então pode ser reaproveitada entre cliques.
    private static readonly ConcurrentDictionary<(string Bank, DateOnly Date, int Quantity, TimeOnly BaseTime), DailyWindowChoice> DailyWindowCache = new();

    public Task<PredictionResponse> GenerateAndSave(PredictionRequest request) => GenerateAndSaveInternal(request);

    private async Task<PredictionResponse> GenerateAndSaveInternal(PredictionRequest request, int? resolvedWindowDays = null,
        DailyWindowChoice? dailyWindow = null)
    {
        var bank = request.Bank.Trim();
        var date = request.TargetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        if (!TimeOnly.TryParse(request.Time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        var requestedWindowDays = Math.Clamp(request.WindowDays, 7, 3650);
        var windowDays = resolvedWindowDays ?? (request.UseRecommendedWindow ? RecommendedWindow(targetTime) : requestedWindowDays);
        var requestedGroups = (request.Groups ?? []).Where(x => x is >= 1 and <= 25).Distinct().Order().ToArray();
        var maximumQuantity = requestedGroups.Length == 0 ? 10_000 : requestedGroups.Length * 400;
        if (request.Quantity < 1 || request.Quantity > maximumQuantity)
            throw new ArgumentException($"Informe uma quantidade entre 1 e {maximumQuantity} para os animais selecionados.");
        var quantity = request.Quantity;
        var prizeRange = Math.Clamp(request.PrizeRange, 1, 10);
        var dezenaStake = Math.Clamp(request.DezenaStake, 0m, 1_000_000m);
        var centenaStake = Math.Clamp(request.CentenaStake, 0m, 1_000_000m);
        var milharStake = Math.Clamp(request.MilharStake, 0m, 1_000_000m);
        var betAmount = dezenaStake + centenaStake + milharStake;
        // O valor informado é o total da modalidade. Cada número recebe a sua fração desse total.
        var dezenaPayout = Math.Round(dezenaStake / quantity * 90m / prizeRange, 2);
        var centenaPayout = Math.Round(centenaStake / quantity * 900m / prizeRange, 2);
        var milharPayout = Math.Round(milharStake / quantity * 9000m / prizeRange, 2);
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
                and r.position between 1 and 10
              order by e.extraction_date,e.extraction_time,r.position",
            new { bank, start = start.Date, date = target.Date, time = targetTime.ToString("HH:mm") })).ToList();

        var priorAppearances = (await connection.QueryAsync<string>(
            @"select pc.milhar from prediction_candidates pc join predictions p on p.id=pc.prediction_id
              where p.bank=@bank and p.target_time=@time::time order by p.generated_at desc limit 500",
            new { bank, time = targetTime.ToString("HH:mm") })).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());

        var ranked = Score(rows, targetTime, priorAppearances);
        if (requestedGroups.Length > 0) ranked = ranked.Where(x => requestedGroups.Contains(x.Group)).ToList();
        if (ranked.Count < quantity) throw new ArgumentException("Histórico insuficiente para gerar a quantidade solicitada.");
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
            restrictedGroups = requestedGroups,
            requestedWindowDays,
            usedRecommendedWindow = request.UseRecommendedWindow,
            dailyWindow,
            prizeRange,
            stakes = new { dezena = dezenaStake, centena = centenaStake, milhar = milharStake },
            stakePerNumber = new { dezena = dezenaStake / quantity, centena = centenaStake / quantity, milhar = milharStake / quantity }
        };

        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into predictions(id,bank,target_date,target_time,algorithm_code,algorithm_version,
                window_days,quantity,random_seed,sample_extractions,sample_results,robustness,config,
                bet_amount,dezena_payout,centena_payout,milhar_payout,prize_range,
                dezena_stake,centena_stake,milhar_stake)
              values(@id,@bank,@date,@time::time,@Algorithm,@Version,@windowDays,@quantity,@seed,
                @sampleExtractions,@sampleResults,@robustness,@config::jsonb,
                @betAmount,@dezenaPayout,@centenaPayout,@milharPayout,@prizeRange,
                @dezenaStake,@centenaStake,@milharStake)",
            new { id, bank, date = target.Date, time = targetTime.ToString("HH:mm"), Algorithm, Version, windowDays,
                quantity, seed, sampleExtractions, sampleResults = rows.Count, robustness,
                config = JsonSerializer.Serialize(composition, JsonOptions), betAmount, dezenaPayout, centenaPayout, milharPayout,
                prizeRange, dezenaStake, centenaStake, milharStake }, transaction);
        await InsertCandidates(connection, transaction, id, selected);
        await transaction.CommitAsync();
        await EvaluatePending(bank, date, targetTime.ToString("HH:mm"));

        return new PredictionResponse(id, Algorithm, Version, bank, targetTime.ToString("HH:mm"), date,
            windowDays, quantity, seed, sampleExtractions, rows.Count, robustness, composition, selected,
            "Os scores são rankings estatísticos, não probabilidades nem garantia de acerto. A vantagem deve ser confirmada por backtest fora da amostra.",
            betAmount, dezenaPayout, centenaPayout, milharPayout, request.UseRecommendedWindow,
            prizeRange, dezenaStake, centenaStake, milharStake);
    }

    public async Task<BankDayPredictionResponse> GenerateForBankDay(PredictionRequest request)
    {
        var bank = request.Bank.Trim();
        var schedules = BankSchedules(bank);
        var dailyRequest = request with { Bank = bank, Time = schedules[0] };
        DailyWindowChoice? dailyWindow = null;
        if (request.UseRecommendedWindow)
            dailyWindow = await RecommendDailyWindow(bank, request.TargetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)),
                request.Quantity, TimeOnly.Parse(schedules[0]));
        var source = await GenerateAndSaveInternal(dailyRequest, dailyWindow?.WindowDays, dailyWindow);
        var predictions = new List<PredictionResponse> { source };
        foreach (var targetTime in schedules.Where(x => x != source.Time))
            predictions.Add(await SaveCopiedPrediction(source, targetTime));

        return new BankDayPredictionResponse(source, predictions.OrderBy(x => x.Time).ToList(),
            predictions.Sum(x => x.BetAmount),
            $"A mesma lista de {source.Quantity} números foi registrada nos {predictions.Count} horários da banca. " +
            (dailyWindow is null
                ? "Foi usada a janela manual informada."
                : $"A janela diária automática escolhida foi de {dailyWindow.WindowDays} dias, validada em {dailyWindow.EvaluatedDays} dias anteriores.") +
            " Cada horário é uma aposta e conferência independente.");
    }

    // Equivale a gerar horário por horário: cada um usa a própria janela, semente e histórico até aquele horário.
    public async Task<BankDayPredictionResponse> GenerateForBankDayEach(PredictionRequest request)
    {
        var bank = request.Bank.Trim();
        var predictions = new List<PredictionResponse>();
        foreach (var targetTime in BankSchedules(bank))
            predictions.Add(await GenerateAndSaveInternal(request with { Bank = bank, Time = targetTime }));

        var windows = predictions.Select(x => x.WindowDays).Distinct().Order().ToList();
        return new BankDayPredictionResponse(predictions[0], predictions,
            predictions.Sum(x => x.BetAmount),
            $"Cada um dos {predictions.Count} horários da banca recebeu um cálculo próprio com {predictions[0].Quantity} números. " +
            (request.UseRecommendedWindow
                ? $"Foi usada a janela recomendada de cada horário ({string.Join(", ", windows)} dias)."
                : "Foi usada a janela manual informada em todos os horários.") +
            " Cada horário é uma aposta e conferência independente.");
    }

    internal static string[] BankSchedules(string bank) => bank.Trim().Equals("LOOK LOTERIAS", StringComparison.OrdinalIgnoreCase)
        ? ["07:00", "09:00", "11:00", "14:00", "16:00", "18:00", "21:00", "23:00"]
        : ["02:00", "08:00", "10:00", "12:00", "15:00", "17:00", "21:00", "23:00"];

    private async Task<DailyWindowChoice> RecommendDailyWindow(string bank, DateOnly targetDate, int quantity, TimeOnly baseTime)
    {
        quantity = Math.Clamp(quantity, 1, 10_000);
        var cacheKey = (bank.ToUpperInvariant(), targetDate, quantity, baseTime);
        if (DailyWindowCache.TryGetValue(cacheKey, out var cached)) return cached;
        var candidateWindows = new[] { 30, 60, 90, 120, 180, 240 };
        await using var connection = db.Open();
        // Os dias avaliados vêm do banco; assim o histórico carregado se limita ao que as janelas realmente usam.
        var dates = (await connection.QueryAsync<DateTime>(
            @"select e.extraction_date from extractions e
              where e.bank=@bank and e.extraction_date<@targetDate
                and exists(select 1 from results r where r.extraction_id=e.id and r.position between 1 and 10)
              group by e.extraction_date having count(*)>=4
              order by e.extraction_date desc limit 60",
            new { bank, targetDate = targetDate.ToDateTime(TimeOnly.MinValue) }))
            .Select(DateOnly.FromDateTime).Order().ToList();
        if (dates.Count == 0)
            return new DailyWindowChoice(180, 0, 0, 0, 0, "Base insuficiente; aplicada janela diária padrão de 180 dias.");
        var rows = (await connection.QueryAsync<Row>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                     r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_date>=@start and e.extraction_date<@targetDate and r.position between 1 and 10
              order by e.extraction_date,e.extraction_time,r.position",
            new
            {
                bank,
                start = dates[0].AddDays(-candidateWindows.Max()).ToDateTime(TimeOnly.MinValue),
                targetDate = targetDate.ToDateTime(TimeOnly.MinValue)
            })).ToList();

        // Cada par janela/dia é independente e tem semente própria; rodar em paralelo não altera o resultado.
        var hits = new (int Milhar, int Centena, int Dezena)[candidateWindows.Length, dates.Count];
        Parallel.For(0, candidateWindows.Length * dates.Count, index =>
        {
            var window = candidateWindows[index / dates.Count]; var date = dates[index % dates.Count];
            var historyStart = date.AddDays(-window).ToDateTime(TimeOnly.MinValue);
            var historyEnd = date.ToDateTime(TimeOnly.MinValue);
            var history = rows.Where(x => x.Date < historyEnd && x.Date >= historyStart).ToList();
            if (history.Count == 0) return;
            var seed = StableSeed($"daily-window:{bank}:{date:yyyy-MM-dd}:{window}:{quantity}");
            var picks = Select(Score(history, baseTime, [], true, explain: false), quantity, seed, false);
            var actual = rows.Where(x => x.Date.Date == historyEnd).Select(x => x.Number.PadLeft(4, '0')[^4..]).ToList();
            hits[index / dates.Count, index % dates.Count] = (
                picks.Sum(pick => actual.Count(number => number == pick.Milhar)),
                picks.Sum(pick => actual.Count(number => number.EndsWith(pick.Centena, StringComparison.Ordinal))),
                picks.Sum(pick => actual.Count(number => number.EndsWith(pick.Dezena, StringComparison.Ordinal))));
        });
        var comparisons = candidateWindows.Select((window, w) =>
        {
            var row = Enumerable.Range(0, dates.Count).Select(d => hits[w, d]).ToList();
            return new DailyWindowChoice(window, dates.Count, row.Sum(x => x.Milhar), row.Sum(x => x.Centena), row.Sum(x => x.Dezena),
                "Walk-forward diário: uma única lista é gerada antes do primeiro horário e confrontada com todos os resultados disponíveis do dia.");
        }).ToList();
        var choice = comparisons.OrderByDescending(x => x.MilharHits).ThenByDescending(x => x.CentenaHits)
            .ThenByDescending(x => x.DezenaHits).ThenBy(x => Math.Abs(x.WindowDays - 180)).First();
        if (DailyWindowCache.Count > 200) DailyWindowCache.Clear();
        DailyWindowCache[cacheKey] = choice;
        return choice;
    }

    private async Task<PredictionResponse> SaveCopiedPrediction(PredictionResponse source, string targetTime)
    {
        var id = Guid.NewGuid();
        var seed = StableSeed($"DAY_COPY:{source.Id}:{targetTime}");
        var composition = new
        {
            samePredictionForAllBankTimes = true,
            sourcePredictionId = source.Id,
            sourceTime = source.Time,
            sourceComposition = source.Composition
        };
        await using var connection = db.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into predictions(id,bank,target_date,target_time,algorithm_code,algorithm_version,
                window_days,quantity,random_seed,sample_extractions,sample_results,robustness,config,
                bet_amount,dezena_payout,centena_payout,milhar_payout,prize_range,
                dezena_stake,centena_stake,milhar_stake)
              values(@id,@bank,@date,@time::time,@algorithm,@version,@windowDays,@quantity,@seed,
                @sampleExtractions,@sampleResults,@robustness,@config::jsonb,
                @betAmount,@dezenaPayout,@centenaPayout,@milharPayout,@prizeRange,
                @dezenaStake,@centenaStake,@milharStake)",
            new { id, bank = source.Bank, date = source.TargetDate.ToDateTime(TimeOnly.MinValue), time = targetTime,
                algorithm = source.Algorithm, version = source.AlgorithmVersion, windowDays = source.WindowDays,
                quantity = source.Quantity, seed, sampleExtractions = source.SampleExtractions, sampleResults = source.SampleResults,
                robustness = source.Robustness, config = JsonSerializer.Serialize(composition, JsonOptions), source.BetAmount,
                source.DezenaPayout, source.CentenaPayout, source.MilharPayout, source.PrizeRange,
                source.DezenaStake, source.CentenaStake, source.MilharStake }, transaction);
        await InsertCandidates(connection, transaction, id, source.Numbers);
        await transaction.CommitAsync();
        await EvaluatePending(source.Bank, source.TargetDate, targetTime);
        return source with { Id = id, Time = targetTime, RandomSeed = seed, Composition = composition };
    }

    // Um único insert com arrays evita uma ida ao banco por número gerado.
    private static Task InsertCandidates(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id,
        IReadOnlyCollection<PredictionCandidate> candidates) =>
        connection.ExecuteAsync(
            @"insert into prediction_candidates(prediction_id,rank,milhar,centena,dezena,group_no,
                selection_type,statistical_score,final_score,features,reasons)
              select @id,x.rank,x.milhar,x.centena,x.dezena,x.group_no,x.selection_type,x.statistical_score,x.final_score,
                x.features::jsonb,x.reasons::jsonb
              from unnest(@ranks,@milhares,@centenas,@dezenas,@groups,@types,@statisticalScores,@finalScores,@features,@reasons)
                as x(rank,milhar,centena,dezena,group_no,selection_type,statistical_score,final_score,features,reasons)",
            new
            {
                id,
                ranks = candidates.Select(x => x.Rank).ToArray(),
                milhares = candidates.Select(x => x.Milhar).ToArray(),
                centenas = candidates.Select(x => x.Centena).ToArray(),
                dezenas = candidates.Select(x => x.Dezena).ToArray(),
                groups = candidates.Select(x => x.Group).ToArray(),
                types = candidates.Select(x => x.SelectionType).ToArray(),
                statisticalScores = candidates.Select(x => x.StatisticalScore).ToArray(),
                finalScores = candidates.Select(x => x.FinalScore).ToArray(),
                features = candidates.Select(x => JsonSerializer.Serialize(x.Features, JsonOptions)).ToArray(),
                reasons = candidates.Select(x => JsonSerializer.Serialize(x.Reasons, JsonOptions)).ToArray()
            }, transaction);

    public async Task<PredictionResponse> SaveManual(ManualPredictionRequest request)
    {
        var bank = request.Bank.Trim();
        if (!TimeOnly.TryParse(request.Time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        var cleaned = Regex.Replace(request.Numbers ?? "", @"\[[^\]]*\]", " ");
        var milhares = Regex.Matches(cleaned, @"(?<!\d)\d{4}(?!\d)").Select(x => x.Value).Distinct().Take(100).ToList();
        if (milhares.Count == 0) throw new ArgumentException("Cole ao menos uma milhar de quatro dígitos.");
        var prizeRange = Math.Clamp(request.PrizeRange, 1, 10);
        var dezenaStake = Math.Clamp(request.DezenaStake, 0m, 1_000_000m);
        var centenaStake = Math.Clamp(request.CentenaStake, 0m, 1_000_000m);
        var milharStake = Math.Clamp(request.MilharStake, 0m, 1_000_000m);
        var betAmount = dezenaStake + centenaStake + milharStake;
        var dezenaPayout = Math.Round(dezenaStake / milhares.Count * 90m / prizeRange, 2);
        var centenaPayout = Math.Round(centenaStake / milhares.Count * 900m / prizeRange, 2);
        var milharPayout = Math.Round(milharStake / milhares.Count * 9000m / prizeRange, 2);
        var id = Guid.NewGuid();
        var seed = StableSeed($"MANUAL:{bank}:{request.TargetDate:yyyy-MM-dd}:{targetTime:HH:mm}:{string.Join(',', milhares)}");
        var candidates = milhares.Select((milhar, index) => new PredictionCandidate(index + 1, milhar, milhar[^3..], milhar[^2..],
            GroupOf(milhar), "MANUAL", 0, 0, new PredictionFeatures(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            ["Aposta inserida manualmente."])).ToList();
        var composition = new { manual = true, prizeRange, stakes = new { dezena = dezenaStake, centena = centenaStake, milhar = milharStake },
            stakePerNumber = new { dezena = dezenaStake / milhares.Count, centena = centenaStake / milhares.Count, milhar = milharStake / milhares.Count } };
        await using var connection = db.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into predictions(id,bank,target_date,target_time,algorithm_code,algorithm_version,window_days,quantity,random_seed,sample_extractions,sample_results,robustness,config,
                bet_amount,dezena_payout,centena_payout,milhar_payout,prize_range,dezena_stake,centena_stake,milhar_stake)
              values(@id,@bank,@date,@time::time,'MANUAL_BET',1,0,@quantity,@seed,0,0,'MANUAL',@config::jsonb,
                @betAmount,@dezenaPayout,@centenaPayout,@milharPayout,@prizeRange,@dezenaStake,@centenaStake,@milharStake)",
            new { id, bank, date = request.TargetDate.ToDateTime(TimeOnly.MinValue), time = targetTime.ToString("HH:mm"), quantity = milhares.Count, seed,
                config = JsonSerializer.Serialize(composition, JsonOptions), betAmount, dezenaPayout, centenaPayout, milharPayout, prizeRange, dezenaStake, centenaStake, milharStake }, transaction);
        await InsertCandidates(connection, transaction, id, candidates);
        await transaction.CommitAsync();
        await EvaluatePending(bank, request.TargetDate, targetTime.ToString("HH:mm"));
        return new PredictionResponse(id, "MANUAL_BET", 1, bank, targetTime.ToString("HH:mm"), request.TargetDate, 0, milhares.Count, seed, 0, 0, "MANUAL",
            composition, candidates, "Aposta manual registrada para conferência posterior.", betAmount, dezenaPayout, centenaPayout, milharPayout,
            false, prizeRange, dezenaStake, centenaStake, milharStake);
    }

    public async Task<AnimalTrendResponse> AnimalTrends(string bank, string time, DateOnly targetDate, int windowDays)
    {
        if (!TimeOnly.TryParse(time, out var targetTime)) throw new ArgumentException("Horário inválido.");
        await using var connection = db.Open();
        var rows = (await connection.QueryAsync<(long ExtractionId, DateTime Date, string Number)>(
            @"select e.id as ExtractionId,e.extraction_date as Date,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_time=@time::time and e.extraction_date<@targetDate
                and e.extraction_date>=@startDate and r.position between 1 and 10
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
            p.algorithm_version,p.window_days,p.quantity,p.robustness,p.status,p.generated_at,p.bet_amount,p.prize_range,
            pe.hit_milhar,pe.hit_centena,pe.hit_dezena,
            pe.milhar_hit_count,pe.centena_hit_count,pe.dezena_hit_count,pe.return_amount,pe.profit_amount
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
                     evaluated_at as EvaluatedAt,return_amount as ReturnAmount,profit_amount as ProfitAmount
              from prediction_evaluations where prediction_id=@id", new { id });
        var financial = await connection.QuerySingleAsync<PredictionFinancial>(
            @"select bet_amount as BetAmount,dezena_payout as DezenaPayout,centena_payout as CentenaPayout,
                     milhar_payout as MilharPayout,prize_range as PrizeRange from predictions where id=@id", new { id });
        var actual = evaluation is null ? [] : (await connection.QueryAsync<(int Position, string Number)>(
            "select position,number from results where extraction_id=@id and position between 1 and 10 order by position",
            new { id = evaluation.ExtractionId })).Where(x => x.Position <= financial.PrizeRange).ToList();
        var dayResults = (await connection.QueryAsync<(TimeSpan Time, int Position, string Number)>(
            @"select e.extraction_time as Time,r.position as Position,r.number as Number
              from extractions e join results r on r.extraction_id=e.id
              where e.bank=@bank and e.extraction_date=@date and r.position between 1 and 10
              order by e.extraction_time,r.position",
            new { bank = target.Bank, date = target.TargetDate.Date })).ToList();
        var beforeTarget = dayResults.Where(x => x.Time < target.TargetTime).ToList();
        var detailed = candidates.Select(candidate =>
        {
            var milharMatches = actual.Where(x => x.Number == candidate.Milhar).Select(x => new { x.Position, x.Number }).ToList();
            var centenaMatches = actual.Where(x => x.Number.EndsWith(candidate.Centena, StringComparison.Ordinal)).Select(x => new { x.Position, x.Number }).ToList();
            var dezenaMatches = actual.Where(x => x.Number.EndsWith(candidate.Dezena, StringComparison.Ordinal)).Select(x => new { x.Position, x.Number }).ToList();
            object Match(TimeSpan matchTime, int position, string number) => new
            {
                Time = $"{matchTime.Hours:00}:{matchTime.Minutes:00}", Position = position, Number = number
            };
            var beforeMilhar = beforeTarget.Where(x => x.Number == candidate.Milhar).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var beforeCentena = beforeTarget.Where(x => x.Number.EndsWith(candidate.Centena, StringComparison.Ordinal)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var beforeDezena = beforeTarget.Where(x => x.Number.EndsWith(candidate.Dezena, StringComparison.Ordinal)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayMilhar = dayResults.Where(x => x.Number == candidate.Milhar).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayCentena = dayResults.Where(x => x.Number.EndsWith(candidate.Centena, StringComparison.Ordinal)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
            var dayDezena = dayResults.Where(x => x.Number.EndsWith(candidate.Dezena, StringComparison.Ordinal)).Select(x => Match(x.Time, x.Position, x.Number)).ToList();
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

    public async Task<int> ReevaluateHistoricalPredictions()
    {
        await using var connection = db.Open();
        var targets = (await connection.QueryAsync<PredictionTarget>(
            @"select distinct bank as Bank,target_date as TargetDate,target_time as TargetTime
              from predictions where bank in ('LT NACIONAL','LOOK LOTERIAS')
              order by target_date,target_time")).ToList();
        foreach (var target in targets)
            await EvaluatePending(target.Bank, DateOnly.FromDateTime(target.TargetDate),
                $"{target.TargetTime.Hours:00}:{target.TargetTime.Minutes:00}");
        return targets.Count;
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
                   coalesce(sum(p.quantity) filter(where pe.prediction_id is not null),0)::int as evaluated_candidates,
                   coalesce(sum(p.bet_amount) filter(where pe.prediction_id is not null),0)::numeric as bet_amount,
                   coalesce(sum(pe.return_amount),0)::numeric as return_amount,
                   coalesce(sum(pe.profit_amount),0)::numeric as profit_amount,
                   case when coalesce(sum(p.bet_amount) filter(where pe.prediction_id is not null),0)=0 then 0
                     else round(100*sum(pe.profit_amount)/sum(p.bet_amount) filter(where pe.prediction_id is not null),2) end as roi_percent
            from predictions p left join prediction_evaluations pe on pe.prediction_id=p.id {filter}", args);
        var byTime = await connection.QueryAsync($@"
            select p.target_time, count(*)::int as evaluated_predictions,
                   count(*) filter(where pe.hit_milhar)::int as predictions_with_milhar_hit,
                   count(*) filter(where pe.hit_centena)::int as predictions_with_centena_hit,
                   count(*) filter(where pe.hit_dezena)::int as predictions_with_dezena_hit,
                   coalesce(sum(pe.milhar_hit_count),0)::int as milhar_hits,
                   coalesce(sum(pe.centena_hit_count),0)::int as centena_hits,
                   coalesce(sum(pe.dezena_hit_count),0)::int as dezena_hits,
                   coalesce(sum(p.bet_amount),0)::numeric as bet_amount,
                   coalesce(sum(pe.return_amount),0)::numeric as return_amount,
                   coalesce(sum(pe.profit_amount),0)::numeric as profit_amount
            from predictions p join prediction_evaluations pe on pe.prediction_id=p.id {evaluatedFilter}
            group by p.target_time order by p.target_time", args);
        var byDate = await connection.QueryAsync($@"
            select p.target_date, count(*)::int as evaluated_predictions,
                   coalesce(sum(pe.milhar_hit_count),0)::int as milhar_hits,
                   coalesce(sum(pe.centena_hit_count),0)::int as centena_hits,
                   coalesce(sum(pe.dezena_hit_count),0)::int as dezena_hits,
                   coalesce(sum(p.bet_amount),0)::numeric as bet_amount,
                   coalesce(sum(pe.return_amount),0)::numeric as return_amount,
                   coalesce(sum(pe.profit_amount),0)::numeric as profit_amount
            from predictions p join prediction_evaluations pe on pe.prediction_id=p.id {evaluatedFilter}
            group by p.target_date order by p.target_date desc limit 30", args);
        var byPrizeRange = await connection.QueryAsync($@"
            select p.prize_range, count(*)::int as evaluated_predictions,
                   coalesce(sum(p.bet_amount),0)::numeric as bet_amount,
                   coalesce(sum(pe.return_amount),0)::numeric as return_amount,
                   coalesce(sum(pe.profit_amount),0)::numeric as profit_amount,
                   case when coalesce(sum(p.bet_amount),0)=0 then 0
                     else round(100*sum(pe.profit_amount)/sum(p.bet_amount),2) end as roi_percent
            from predictions p join prediction_evaluations pe on pe.prediction_id=p.id {evaluatedFilter}
            group by p.prize_range order by p.prize_range", args);
        var byPrizePosition = await connection.QueryAsync($@"
            with filtered_predictions as (
                select p.* from predictions p {filter}
            )
            select positions.position,
                   count(distinct fp.id) filter (where pe.prediction_id is not null and positions.position <= fp.prize_range)::int as evaluated_predictions,
                   coalesce(sum(case when positions.position <= fp.prize_range and pc.milhar = r.number then 1 else 0 end), 0)::int as milhar_hits,
                   coalesce(sum(case when positions.position <= fp.prize_range and pc.centena = right(r.number, 3) then 1 else 0 end), 0)::int as centena_hits,
                   coalesce(sum(case when positions.position <= fp.prize_range and pc.dezena = right(r.number, 2) then 1 else 0 end), 0)::int as dezena_hits
            from generate_series(1, 10) as positions(position)
            left join filtered_predictions fp on true
            left join prediction_evaluations pe on pe.prediction_id = fp.id
            left join results r on r.extraction_id = pe.extraction_id and r.position = positions.position
            left join prediction_candidates pc on pc.prediction_id = fp.id
            group by positions.position order by positions.position", args);
        return new { totals, byTime, byDate, byPrizeRange, byPrizePosition };
    }

    public async Task EvaluatePending(string bank, DateOnly date, string time)
    {
        await using var connection = db.Open();
        var extraction = await connection.QuerySingleOrDefaultAsync<long?>(
            "select id from extractions where bank=@bank and extraction_date=@date and extraction_time=@time::time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue), time });
        if (extraction is null) return;
        var actual = (await connection.QueryAsync<(int Position, string Number)>(
            "select position,number from results where extraction_id=@id and position between 1 and 10", new { id = extraction })).ToList();
        var predictions = await connection.QueryAsync<Guid>(
            @"select id from predictions where bank=@bank and target_date=@date and target_time=@time::time",
            new { bank, date = date.ToDateTime(TimeOnly.MinValue), time });
        foreach (var predictionId in predictions)
        {
            var financial = await connection.QuerySingleAsync<PredictionFinancial>(
                @"select bet_amount as BetAmount,dezena_payout as DezenaPayout,
                         centena_payout as CentenaPayout,milhar_payout as MilharPayout,prize_range as PrizeRange
                  from predictions where id=@predictionId", new { predictionId });
            var eligibleActual = actual.Where(x => x.Position <= financial.PrizeRange).ToList();
            var candidates = (await connection.QueryAsync<(string Milhar, string Centena, string Dezena)>(
                "select milhar,centena,dezena from prediction_candidates where prediction_id=@predictionId", new { predictionId })).ToList();
            int? milharPos = eligibleActual.Where(a => candidates.Any(c => c.Milhar == a.Number)).Select(a => (int?)a.Position).Min();
            int? centenaPos = eligibleActual.Where(a => candidates.Any(c => c.Centena == a.Number[^3..])).Select(a => (int?)a.Position).Min();
            int? dezenaPos = eligibleActual.Where(a => candidates.Any(c => c.Dezena == a.Number[^2..])).Select(a => (int?)a.Position).Min();
            var milharHitCount = candidates.Sum(c => eligibleActual.Count(a => c.Milhar == a.Number));
            var centenaHitCount = candidates.Sum(c => eligibleActual.Count(a => c.Centena == a.Number[^3..]));
            var dezenaHitCount = candidates.Sum(c => eligibleActual.Count(a => c.Dezena == a.Number[^2..]));
            var returnAmount = Math.Round(milharHitCount * financial.MilharPayout +
                centenaHitCount * financial.CentenaPayout + dezenaHitCount * financial.DezenaPayout, 2);
            var profitAmount = returnAmount - financial.BetAmount;
            var details = JsonSerializer.Serialize(new { prizeRange = financial.PrizeRange, actual = eligibleActual.Select(x => x.Number) }, JsonOptions);
            await connection.ExecuteAsync(
                @"insert into prediction_evaluations(prediction_id,extraction_id,hit_milhar,hit_centena,hit_dezena,
                    milhar_hit_count,centena_hit_count,dezena_hit_count,
                    best_milhar_position,best_centena_position,best_dezena_position,return_amount,profit_amount,details)
                  values(@predictionId,@extraction,@hitMilhar,@hitCentena,@hitDezena,
                    @milharHitCount,@centenaHitCount,@dezenaHitCount,@milharPos,@centenaPos,@dezenaPos,
                    @returnAmount,@profitAmount,@details::jsonb)
                  on conflict(prediction_id) do update set
                    extraction_id=excluded.extraction_id,evaluated_at=now(),
                    hit_milhar=excluded.hit_milhar,hit_centena=excluded.hit_centena,hit_dezena=excluded.hit_dezena,
                    milhar_hit_count=excluded.milhar_hit_count,centena_hit_count=excluded.centena_hit_count,
                    dezena_hit_count=excluded.dezena_hit_count,
                    best_milhar_position=excluded.best_milhar_position,best_centena_position=excluded.best_centena_position,
                    best_dezena_position=excluded.best_dezena_position,return_amount=excluded.return_amount,
                    profit_amount=excluded.profit_amount,details=excluded.details;
                  update predictions set status='EVALUATED' where id=@predictionId",
                new { predictionId, extraction, hitMilhar = milharPos is not null, hitCentena = centenaPos is not null,
                    hitDezena = dezenaPos is not null, milharHitCount, centenaHitCount, dezenaHitCount,
                    milharPos, centenaPos, dezenaPos, returnAmount, profitAmount, details });
        }
    }

    private static int RecommendedWindow(TimeOnly time) => time.Hour switch
    {
        2 => 120,
        8 => 180,
        10 or 12 or 15 or 21 => 240,
        17 => 30,
        23 => 60,
        _ => 180
    };

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
        quantity = Math.Clamp(quantity, 1, 10_000);
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
                        and r.position between 1 and 10
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
                var selected = Select(Score(rows, targetTime, priorAppearances, explain: false), quantity, seed, false);
                var actual = (await connection.QueryAsync<string>(
                    @"select r.number from results r join extractions e on e.id=r.extraction_id
                      where e.bank=@bank and e.extraction_date=@date and e.extraction_time=@time::time
                        and r.position between 1 and 10 order by r.position",
                    new { bank, date = target.Date, time = targetTime.ToString("HH:mm") })).ToList();
                var milhar = selected.Sum(c => actual.Count(x => x == c.Milhar));
                var centena = selected.Sum(c => actual.Count(x => x.EndsWith(c.Centena, StringComparison.Ordinal)));
                var dezena = selected.Sum(c => actual.Count(x => x.EndsWith(c.Dezena, StringComparison.Ordinal)));
                totalMilhar += milhar; totalCentena += centena; totalDezena += dezena;
                scheduleResults.Add(new { time = targetTime.ToString("HH:mm"), sampleExtractions = rows.Select(x => x.ExtractionId).Distinct().Count(),
                    milharHits = milhar, centenaHits = centena, dezenaHits = dezena });
            }
            comparisons.Add(new { windowDays, evaluatedSchedules = schedules.Count, milharHits = totalMilhar,
                centenaHits = totalCentena, dezenaHits = totalDezena, schedules = scheduleResults });
        }
        return new { bank, date, quantity, prizeRange = "1-5", useSameDayResults, comparisons };
    }

    // This is deliberately an evaluation tool, not a number generator: every trial is made
    // before the result being checked and the final fifth is kept as an untouched test set.
    public async Task<object> DecisionBattery(string bank, int quantity, decimal betAmount,
        decimal dezenaPayout, decimal centenaPayout, decimal milharPayout, int prizeRange = 5, int maxEvaluations = 500,
        Action<int, int>? reportProgress = null, DateOnly? testFrom = null)
    {
        quantity = Math.Clamp(quantity, 1, 10_000);
        prizeRange = Math.Clamp(prizeRange, 1, 10);
        maxEvaluations = Math.Clamp(maxEvaluations, 100, MaxBatteryEvaluations);
        await using var connection = db.Open();
        var rows = (await connection.QueryAsync<Row>(@"select e.id as ExtractionId,e.extraction_date as Date,
                    e.extraction_time as Time,r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and r.position between 1 and 10
              order by e.extraction_date,e.extraction_time,r.position", new { bank })).ToList();
        var extractions = rows.GroupBy(x => x.ExtractionId).Select(g => new
        {
            Id = g.Key, Date = g.First().Date.Date, Time = g.First().Time,
            Numbers = g.OrderBy(x => x.Position).Where(x => x.Position <= prizeRange).Select(x => x.Number.PadLeft(4, '0')[^4..]).ToList()
        }).OrderBy(x => x.Date).ThenBy(x => x.Time).ToList();
        var firstTestable = extractions.FindIndex(x => extractions.Count(y => y.Date < x.Date || (y.Date == x.Date && y.Time < x.Time)) >= 30);
        if (firstTestable < 0) return new { bank, decision = "BASE INSUFICIENTE", message = "São necessárias ao menos 30 extrações anteriores para iniciar a bateria.", availableExtractions = extractions.Count };

        var start = Math.Max(firstTestable, extractions.Count - maxEvaluations);
        var planned = extractions.Count - start;
        var trials = new List<(string Model, decimal Net, decimal Return, int M, int C, int D, DateTime Date, decimal Expected)>();
        for (var i = start; i < extractions.Count; i++)
        {
            var target = extractions[i];
            var timestamp = target.Date.Add(target.Time);
            foreach (var model in new[] { "V2", "V3" })
            {
                var window = model == "V3" ? RecommendedWindow(TimeOnly.FromTimeSpan(target.Time)) : 90;
                var history = rows.Where(x => x.Date.Add(x.Time) < timestamp && x.Date >= target.Date.AddDays(-window)).ToList();
                if (history.Count == 0) continue;
                var ranked = Score(history, TimeOnly.FromTimeSpan(target.Time), [], model == "V3", explain: false);
                var seed = StableSeed($"battery:{model}:{bank}:{target.Date:yyyy-MM-dd}:{target.Time}:{window}:{quantity}");
                var picks = Select(ranked, quantity, seed, false).Select(x => x.Milhar).ToList();
                AddTrial(model, picks, target.Numbers, target.Date);
            }
            var random = new Random(unchecked((int)StableSeed($"battery:random:{bank}:{target.Date:yyyy-MM-dd}:{target.Time}:{quantity}")));
            AddTrial("ALEATÓRIO", Enumerable.Range(0, 10_000).OrderBy(_ => random.Next()).Take(quantity).Select(x => x.ToString("0000")).ToList(), target.Numbers, target.Date);
            reportProgress?.Invoke(i - start + 1, planned);
        }

        void AddTrial(string model, List<string> picks, List<string> actual, DateTime date)
        {
            var m = picks.Sum(p => actual.Count(a => a == p));
            var c = picks.Sum(p => actual.Count(a => a.EndsWith(p[^3..], StringComparison.Ordinal)));
            var d = picks.Sum(p => actual.Count(a => a.EndsWith(p[^2..], StringComparison.Ordinal)));
            var returned = d * dezenaPayout + c * centenaPayout + m * milharPayout;
            // Cada par (número apostado, prêmio) acerta dezena, centena e milhar com 1/100, 1/1000 e 1/10000.
            var expected = picks.Count * actual.Count * (dezenaPayout / 100m + centenaPayout / 1000m + milharPayout / 10000m);
            trials.Add((model, returned - betAmount, returned, m, c, d, date, expected));
        }

        var testStart = testFrom?.ToDateTime(TimeOnly.MinValue);
        List<T> TestSlice<T>(List<T> all, Func<T, DateTime> date) =>
            testStart is null ? all.Skip((int)Math.Floor(all.Count * .8)).ToList() : all.Where(x => date(x) >= testStart).ToList();
        // Dois modelos comparados ao mesmo tempo: Bonferroni com alfa 0,05/2 bilateral.
        const double bonferroniZ = 2.241;
        var modelRows = new List<object>();
        var passing = new List<string>();
        var smallestTest = int.MaxValue;
        foreach (var model in new[] { "V2", "V3", "ALEATÓRIO" })
        {
            var all = trials.Where(x => x.Model == model).ToList();
            var test = TestSlice(all, x => x.Date);
            var randomTest = testStart is null
                ? trials.Where(x => x.Model == "ALEATÓRIO").Skip((int)Math.Floor(all.Count * .8)).ToList()
                : trials.Where(x => x.Model == "ALEATÓRIO" && x.Date >= testStart).ToList();
            var differences = test.Zip(randomTest, (a, b) => (double)(a.Net - b.Net)).ToList();
            var mean = differences.DefaultIfEmpty().Average();
            var sd = differences.Count < 2 ? 0 : Math.Sqrt(differences.Sum(x => Math.Pow(x - mean, 2)) / (differences.Count - 1));
            var margin = differences.Count < 2 ? 0 : 1.96 * sd / Math.Sqrt(differences.Count);
            var excess = test.Select(x => (double)(x.Return - x.Expected)).ToList();
            var excessMean = excess.DefaultIfEmpty().Average();
            var excessSd = excess.Count < 2 ? 0 : Math.Sqrt(excess.Sum(x => Math.Pow(x - excessMean, 2)) / (excess.Count - 1));
            var excessMargin = excess.Count < 2 ? 0 : bonferroniZ * excessSd / Math.Sqrt(excess.Count);
            decimal Cost<T>(ICollection<T> data) => data.Count * betAmount;
            var testRoi = Cost(test) == 0 ? 0 : Math.Round(100 * test.Sum(x => x.Net) / Cost(test), 2);
            if (model != "ALEATÓRIO")
            {
                smallestTest = Math.Min(smallestTest, test.Count);
                if (testRoi > 0 && excessMean - excessMargin > 0) passing.Add(model);
            }
            modelRows.Add(new {
                model, trials = all.Count, testTrials = test.Count,
                milharHits = all.Sum(x => x.M), centenaHits = all.Sum(x => x.C), dezenaHits = all.Sum(x => x.D),
                totalBet = Cost(all), totalReturn = all.Sum(x => x.Return), net = all.Sum(x => x.Net),
                roiPercent = Cost(all) == 0 ? 0 : Math.Round(100 * all.Sum(x => x.Net) / Cost(all), 2),
                expectedRoiPercent = Cost(all) == 0 ? 0 : Math.Round(100 * (all.Sum(x => x.Expected) - Cost(all)) / Cost(all), 2),
                testNet = test.Sum(x => x.Net),
                testRoiPercent = testRoi,
                testExpectedRoiPercent = Cost(test) == 0 ? 0 : Math.Round(100 * (test.Sum(x => x.Expected) - Cost(test)) / Cost(test), 2),
                excessVsExpectedPerTrial = Math.Round(excessMean, 2),
                excessConfidenceLow = Math.Round(excessMean - excessMargin, 2), excessConfidenceHigh = Math.Round(excessMean + excessMargin, 2),
                deltaVsRandomPerTrial = Math.Round(mean, 2), confidence95Low = Math.Round(mean - margin, 2), confidence95High = Math.Round(mean + margin, 2)
            });
        }
        var decision = smallestTest < 100 ? "AMOSTRA INSUFICIENTE" : passing.Count > 0
            ? $"SINAL POSITIVO ({string.Join(", ", passing)}) — MANTER SOMENTE EM TESTE CONTROLADO" : "SEM EVIDÊNCIA DE VANTAGEM — REDUZIR OU PAUSAR EXPOSIÇÃO";
        return new { bank, availableExtractions = extractions.Count, evaluatedExtractions = trials.Count / 3,
            period = new { from = extractions[start].Date, to = extractions[^1].Date }, quantity, prizeRange, betAmount, dezenaPayout, centenaPayout, milharPayout,
            testFrom, testPeriodFrom = testStart ?? trials.Where(x => x.Model == "V3").Select(x => x.Date).Skip((int)Math.Floor(trials.Count(x => x.Model == "V3") * .8)).DefaultIfEmpty().First(),
            confidenceZ = bonferroniZ, models = modelRows, decision,
            methodology = "Walk-forward cronológico. Cada previsão usa somente resultados anteriores; V2 usa janela fixa de 90 dias, V3 usa a janela recomendada por horário. " +
                (testStart is null ? "O último 20% é teste final." : $"O teste final são as extrações a partir de {testFrom:dd/MM/yyyy}.") +
                " Aleatório recebe a mesma quantidade de números e cotações. O retorno esperado vem da probabilidade teórica de cada modalidade em sorteio uniforme.",
            interpretation = "O intervalo do excesso sobre o esperado usa z=2,241 (Bonferroni para V2 e V3). Se ele inclui zero, o modelo não se distingue de apostar ao acaso. O intervalo contra o aleatório (95%) é mantido para comparação com execuções anteriores.",
            warning = "Esta bateria avalia desempenho histórico e não torna sorteios independentes previsíveis nem garante retorno futuro." };
    }

    public const int MaxBatteryEvaluations = 3000;

    // Laboratório: a mesma pontuação, seleção e semente da geração com janela recomendada. Fica de fora só o
    // histórico de previsões salvas (penalidade de repetição), que não existia nas datas passadas.
    // O chamador entrega apenas resultados anteriores ao alvo; o corte por data/horário é refeito aqui por segurança.
    internal static List<string> LabMilhares(IEnumerable<(long ExtractionId, DateTime Date, TimeSpan Time, int Position, string Number)> priorRows,
        string bank, DateOnly date, TimeOnly targetTime, int quantity)
    {
        var windowDays = RecommendedWindow(targetTime);
        var target = date.ToDateTime(targetTime);
        var start = target.AddDays(-windowDays).Date;
        var rows = priorRows.Where(x => x.Date >= start && x.Date.Add(x.Time) < target)
            .Select(x => new Row(x.ExtractionId, x.Date, x.Time, x.Position, x.Number)).ToList();
        var seed = StableSeed($"{Algorithm}:{Version}:{bank}:{date:yyyy-MM-dd}:{targetTime:HH:mm}:{windowDays}:{quantity}:ALL");
        return Select(Score(rows, targetTime, [], explain: false), quantity, seed, false).Select(x => x.Milhar).ToList();
    }

    internal static int LabWindowDays(TimeOnly targetTime) => RecommendedWindow(targetTime);
    internal static long LabSeed(string value) => StableSeed(value);

    private static List<Scored> Score(List<Row> rows, TimeOnly targetTime, Dictionary<string, int> priorAppearances, bool v3 = true,
        bool explain = true)
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
        var targetTimeSpan = targetTime.ToTimeSpan();
        var currentDate = extractions.Max(e => e.Date);
        var priorGroupsByDate = extractions.GroupBy(e => e.Date).ToDictionary(group => group.Key,
            group => group.Where(e => e.Time < targetTimeSpan).SelectMany(e => e.Numbers).Select(GroupOf).ToHashSet());
        var currentGroups = priorGroupsByDate.GetValueOrDefault(currentDate, []);
        var transitionTargets = targetRows.Where(t => priorGroupsByDate.GetValueOrDefault(t.Date, []).Overlaps(currentGroups))
            .SelectMany(x => x.Numbers).ToList();

        // Contagens em vetores indexados pelo próprio número: o laço dos 10.000 candidatos fica sem buscas por texto.
        var freqM = Tally.Of(all, 10_000); var freqC = Tally.Of(all, 1_000); var freqD = Tally.Of(all, 100);
        var timeM = Tally.Of(longTerm, 10_000); var timeC = Tally.Of(longTerm, 1_000); var timeD = Tally.Of(longTerm, 100);
        var recentD = Tally.Of(recent, 100); var longD = Tally.Of(longTerm, 100);
        var transitionD = Tally.Of(transitionTargets, 100);
        var digitCounts = new int[4, 10]; foreach (var n in longTerm) for (var p = 0; p < 4; p++) digitCounts[p, n[p] - '0']++;
        var digitMax = Enumerable.Range(0, 4).Select(p => (double)Math.Max(1, Enumerable.Range(0, 10).Max(digit => digitCounts[p, digit]))).ToArray();
        var maxPrior = Math.Max(1, priorAppearances.Values.DefaultIfEmpty().Max());
        // Quem termina com a milhar ou a centena também termina com a dezena, então basta a última ocorrência da dezena.
        var lastIndexByDezena = new int[100]; Array.Fill(lastIndexByDezena, -1);
        for (var i = 0; i < longTerm.Count; i++) lastIndexByDezena[NumberValue(longTerm[i]) % 100] = i;

        var scored = new List<Scored>(10_000);
        for (var value = 0; value < 10_000; value++)
        {
            var m = MilharTexts[value]; var cv = value % 1_000; var dv = value % 100;
            var frequency = v3 ? .10 * freqM.Norm(value) + .45 * freqC.Norm(cv) + .45 * freqD.Norm(dv) : .15 * freqM.Norm(value) + .35 * freqC.Norm(cv) + .50 * freqD.Norm(dv);
            var timeFrequency = v3 ? .10 * timeM.Norm(value) + .45 * timeC.Norm(cv) + .45 * timeD.Norm(dv) : .15 * timeM.Norm(value) + .35 * timeC.Norm(cv) + .50 * timeD.Norm(dv);
            var continuity = recentD.Norm(dv);
            var momentum = Math.Clamp(recentD.Norm(dv) - longD.Norm(dv), -1, 1);
            var transition = transitionD.Norm(dv);
            var reversal = recentD.Norm(dv % 10 * 10 + dv / 10);
            var digitSum = 0d;
            for (var p = 0; p < 4; p++) digitSum += digitCounts[p, m[p] - '0'] / digitMax[p];
            var digitAffinity = digitSum / 4;
            var lastIndex = lastIndexByDezena[dv];
            var delay = lastIndex < 0 ? 1 : Math.Clamp((longTerm.Count - 1 - lastIndex) / 50d, 0, 1);
            var repetitionPenalty = priorAppearances.GetValueOrDefault(m) / (double)maxPrior;
            var novelty = 1 - repetitionPenalty;
            var features = new PredictionFeatures(frequency, timeFrequency, delay, continuity, transition,
                momentum, reversal, digitAffinity, novelty, repetitionPenalty);
            var statistical = 100 * (.22 * frequency + .12 * timeFrequency + .08 * delay + .12 * continuity +
                .12 * transition + .10 * Math.Max(0, momentum) + .05 * reversal + .09 * digitAffinity + .10 * novelty);
            var final = Math.Clamp(statistical - 8 * repetitionPenalty, 0, 100);
            var reasons = explain ? Explain(features) : [];
            scored.Add(new Scored(m, CentenaTexts[cv], DezenaTexts[dv], dv == 0 ? 25 : (dv + 3) / 4,
                Math.Round(statistical, 4), Math.Round(final, 4), features, reasons));
        }
        // Maior score primeiro; empate pela milhar, que segue a ordem numérica.
        scored.Sort((a, b) => a.FinalScore != b.FinalScore ? b.FinalScore.CompareTo(a.FinalScore) : string.CompareOrdinal(a.Milhar, b.Milhar));
        return scored;
    }

    private static List<PredictionCandidate> Select(List<Scored> ranked, int quantity, long seed, bool restrictedToGroups)
    {
        if (ranked.Count == 0) return [];
        var groupIds = ranked.Select(x => x.Group).Distinct().Order().ToArray();
        var quotas = groupIds.Select((group, index) => new { group, count = quantity / groupIds.Length + (index < quantity % groupIds.Length ? 1 : 0) })
            .ToDictionary(x => x.group, x => x.count);
        var exploitationTarget = (int)Math.Round(quantity * .6, MidpointRounding.AwayFromZero);
        var emergingTarget = (int)Math.Round(quantity * .2, MidpointRounding.AwayFromZero);
        var explorationTarget = quantity - exploitationTarget - emergingTarget;
        var selected = new List<(Scored Candidate, string Type)>();
        var usedC = new HashSet<string>(); var usedD = new HashSet<string>(); var groups = new Dictionary<int, int>();
        var usedM = new HashSet<string>();
        bool Add(Scored x, string type, bool relaxDiversity = false)
        {
            if (usedM.Contains(x.Milhar) ||
                (restrictedToGroups && groups.GetValueOrDefault(x.Group) >= quotas[x.Group]) ||
                (!relaxDiversity && (usedC.Contains(x.Centena) || (!restrictedToGroups && usedD.Contains(x.Dezena)) ||
                (!restrictedToGroups && groups.GetValueOrDefault(x.Group) >= Math.Max(2, (int)Math.Ceiling(quantity / 25d)))))) return false;
            usedM.Add(x.Milhar);
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
        foreach (var x in ranked) { if (selected.Count >= quantity) break; Add(x, "EXPLORATION", relaxDiversity: true); }
        return selected.Select((x, i) => new PredictionCandidate(i + 1, x.Candidate.Milhar, x.Candidate.Centena,
            x.Candidate.Dezena, x.Candidate.Group, x.Type, x.Candidate.StatisticalScore, x.Candidate.FinalScore,
            x.Candidate.Features, x.Candidate.Reasons)).ToList();
    }

    private static readonly string[] MilharTexts = Enumerable.Range(0, 10_000).Select(x => x.ToString("0000")).ToArray();
    private static readonly string[] CentenaTexts = Enumerable.Range(0, 1_000).Select(x => x.ToString("000")).ToArray();
    private static readonly string[] DezenaTexts = Enumerable.Range(0, 100).Select(x => x.ToString("00")).ToArray();
    private static int NumberValue(string number) =>
        (number[0] - '0') * 1000 + (number[1] - '0') * 100 + (number[2] - '0') * 10 + (number[3] - '0');

    // O máximo é calculado uma vez por contagem; recalculá-lo a cada candidato tornava o Score quadrático.
    private sealed class Tally(int[] counts)
    {
        private readonly double max = Math.Max(1, counts.Max());
        public static Tally Of(List<string> numbers, int modulo)
        {
            var counts = new int[modulo];
            foreach (var number in numbers) counts[NumberValue(number) % modulo]++;
            return new Tally(counts);
        }
        public double Norm(int key) => counts[key] / max;
    }
    private static int GroupOf(string number) { var value = int.Parse(number[^2..]); return value == 0 ? 25 : (value + 3) / 4; }
    private static long StableSeed(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0) & long.MaxValue;
    private static readonly string[] ReasonTexts =
    [
        "boa frequência combinada no histórico", "aderência ao horário escolhido",
        "continuidade nas extrações recentes", "sinal nas transições entre horários",
        "momento recente acima da média", "atraso moderado como sinal secundário",
        "reversão de dezena presente", "dígitos compatíveis com o padrão histórico",
        "diversificação em relação às previsões anteriores"
    ];
    private static List<string> Explain(PredictionFeatures f)
    {
        Span<double> values = [f.Frequency, f.TimeFrequency, f.Continuity, f.Transition, Math.Max(0, f.Momentum),
            f.Delay, f.Reversal, f.DigitAffinity, f.Novelty];
        // Os três maiores valores; em empate vence o que vem antes na lista, como numa ordenação estável.
        Span<int> top = [-1, -1, -1];
        for (var i = 0; i < values.Length; i++)
            for (var slot = 0; slot < 3; slot++)
            {
                if (top[slot] >= 0 && values[i] <= values[top[slot]]) continue;
                for (var move = 2; move > slot; move--) top[move] = top[move - 1];
                top[slot] = i; break;
            }
        var reasons = new List<string>(3);
        foreach (var index in top) if (index >= 0 && values[index] > 0) reasons.Add(ReasonTexts[index]);
        return reasons;
    }
}
