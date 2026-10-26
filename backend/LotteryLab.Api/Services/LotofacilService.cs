using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed class LotofacilService(Db db)
{
    public const decimal BetPrice = 3.50m;
    public const long SimpleBetOdds = 3_268_760;
    public const int MinNumbersPerGame = 15;
    public const int MaxNumbersPerGame = 20;
    // Prêmios fixos de 11, 12 e 13 acertos, usados só quando o rateio do concurso ainda não traz o valor.
    public const decimal FixedPrize11 = 7m;
    public const decimal FixedPrize12 = 14m;
    public const decimal FixedPrize13 = 35m;
    private const int MomentumContests = 10;
    private const int TopPoolSize = 12;
    private const int EmergingPoolSize = 7;
    private const int AffinityTopNumbers = 8;
    private const int AttemptsPerLevel = 300;
    private const double ParityTolerance = 1.5;
    private const double FrameTolerance = 1.5;
    private const double SumDeviations = 1.5;
    private const string Warning = "Cada aposta simples de 15 dezenas tem 1 chance em 3.268.760 de acertar os 15 números. " +
        "O score ordena as dezenas pelo histórico e não altera essa probabilidade: todos os sorteios são independentes.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        ["windowFrequency"] = 0.35, ["historyFrequency"] = 0.20, ["delay"] = 0.10, ["momentum"] = 0.20, ["pairAffinity"] = 0.15
    };

    /// <summary>As 16 dezenas da borda do volante 5×5; as 9 restantes formam o miolo.</summary>
    public static readonly IReadOnlySet<int> Frame = new HashSet<int> { 1, 2, 3, 4, 5, 6, 10, 11, 15, 16, 20, 21, 22, 23, 24, 25 };

    // Linhas lidas pelo Dapper são classes com setters: o Npgsql informa colunas smallint[] como System.Array,
    // o que impede o Dapper de casar construtores posicionais (records/tuplas) e falha na materialização.
    public sealed class DrawRow
    {
        public int Contest { get; set; }
        public DateTime DrawDate { get; set; }
        public short[] Numbers { get; set; } = [];
        public int Winners15 { get; set; }
        public decimal Prize15 { get; set; }
        public int Winners14 { get; set; }
        public decimal Prize14 { get; set; }
        public int Winners13 { get; set; }
        public decimal Prize13 { get; set; }
        public int Winners12 { get; set; }
        public decimal Prize12 { get; set; }
        public int Winners11 { get; set; }
        public decimal Prize11 { get; set; }
        public bool Accumulated { get; set; }
        public decimal? NextEstimate { get; set; }
        public string Source { get; set; } = "";
        public DateTime ImportedAt { get; set; }
    }
    public sealed class DrawNumbersRow
    {
        public int Contest { get; set; }
        public short[] Numbers { get; set; } = [];
    }
    public sealed class PredictionRow
    {
        public Guid Id { get; set; }
        public int TargetContest { get; set; }
        public DateTime GeneratedAt { get; set; }
        public long Seed { get; set; }
        public int WindowContests { get; set; }
        public int NumbersPerGame { get; set; }
        public int Quantity { get; set; }
        public decimal TotalCost { get; set; }
        public string ConfigJson { get; set; } = "{}";
        public string ScoresJson { get; set; } = "[]";
        public string Status { get; set; } = "PENDING";
    }
    public sealed class GameRow
    {
        public Guid PredictionId { get; set; }
        public int Rank { get; set; }
        public short[] Numbers { get; set; } = [];
    }

    public sealed record GenerationResult(List<LotofacilGame> Games, int BaseMaxShared, int MaxSharedUsed, bool BalanceRelaxed);

    // ---------- Funções puras (testadas sem banco) ----------

    /// <summary>Pontua as dezenas 1–25 usando somente os concursos informados, em ordem cronológica.</summary>
    public static List<LotofacilNumberScore> ScoreNumbers(IReadOnlyList<int[]> history, int windowContests)
    {
        if (history.Count == 0) throw new ArgumentException("Não há concursos anteriores para pontuar as dezenas.");
        var window = history.Skip(Math.Max(0, history.Count - windowContests)).ToList();
        var recent = history.Skip(Math.Max(0, history.Count - MomentumContests)).ToList();
        var windowHits = new int[26]; var historyHits = new int[26]; var recentHits = new int[26]; var delay = new int[26];
        for (var n = 1; n <= 25; n++) delay[n] = history.Count;
        for (var i = 0; i < history.Count; i++)
            foreach (var n in history[i]) { historyHits[n]++; delay[n] = history.Count - 1 - i; }
        foreach (var draw in window) foreach (var n in draw) windowHits[n]++;
        foreach (var draw in recent) foreach (var n in draw) recentHits[n]++;

        var momentum = new double[26];
        for (var n = 1; n <= 25; n++)
            momentum[n] = (double)recentHits[n] / recent.Count - (double)windowHits[n] / window.Count;

        // A afinidade mede quantas vezes a dezena saiu junto das dezenas mais frequentes da janela.
        var leaders = Enumerable.Range(1, 25).OrderByDescending(n => windowHits[n] * 1000 + historyHits[n]).ThenBy(n => n)
            .Take(AffinityTopNumbers).ToHashSet();
        var affinity = new double[26];
        foreach (var draw in window)
            foreach (var n in draw)
                affinity[n] += draw.Count(other => other != n && leaders.Contains(other));

        var wf = Normalize(windowHits.Select(x => (double)x).ToArray());
        var hf = Normalize(historyHits.Select(x => (double)x).ToArray());
        var dl = Normalize(delay.Select(x => (double)x).ToArray());
        var mo = Normalize(momentum);
        var pa = Normalize(affinity);
        return Enumerable.Range(1, 25).Select(n =>
        {
            var features = new LotofacilNumberFeatures(Math.Round(wf[n], 4), Math.Round(hf[n], 4), Math.Round(dl[n], 4),
                Math.Round(mo[n], 4), Math.Round(pa[n], 4));
            var score = Weights["windowFrequency"] * wf[n] + Weights["historyFrequency"] * hf[n] + Weights["delay"] * dl[n]
                + Weights["momentum"] * mo[n] + Weights["pairAffinity"] * pa[n];
            return new LotofacilNumberScore(n, Math.Round(score, 4), features, windowHits[n], historyHits[n], delay[n]);
        }).OrderByDescending(x => x.Score).ThenBy(x => x.Number).ToList();
    }

    /// <summary>
    /// Limite inicial de dezenas em comum entre dois jogos. Com 25 dezenas a sobreposição é inevitável:
    /// dois jogos de n dezenas dividem em média n²/25 e no mínimo 2n − 25, então o limite parte da média + 1.
    /// </summary>
    public static int BaseMaxShared(int numbersPerGame) =>
        Math.Min(numbersPerGame - 1, Math.Max(2 * numbersPerGame - 25, (int)Math.Round(numbersPerGame * numbersPerGame / 25d) + 1));

    /// <summary>~60% das dezenas vêm do topo do score, ~20% das emergentes (momentum) e ~20% do restante, ponderado.</summary>
    public static GenerationResult BuildGames(IReadOnlyList<LotofacilNumberScore> scores, int quantity, int numbersPerGame, long seed)
    {
        if (quantity is < 1 or > 50) throw new ArgumentException("Informe entre 1 e 50 jogos.");
        if (numbersPerGame is < MinNumbersPerGame or > MaxNumbersPerGame) throw new ArgumentException("Cada jogo deve ter de 15 a 20 dezenas.");
        if (scores.Count != 25) throw new ArgumentException("O score precisa cobrir as 25 dezenas.");
        var ordered = scores.OrderByDescending(x => x.Score).ThenBy(x => x.Number).ToList();
        var top = ordered.Take(TopPoolSize).ToList();
        var emerging = ordered.Skip(TopPoolSize).OrderByDescending(x => x.Features.Momentum).ThenByDescending(x => x.Score)
            .ThenBy(x => x.Number).Take(EmergingPoolSize).ToList();
        var rest = ordered.Except(top).Except(emerging).ToList();
        var fromTop = (int)Math.Round(numbersPerGame * 0.6, MidpointRounding.AwayFromZero);
        var fromEmerging = (int)Math.Round(numbersPerGame * 0.2, MidpointRounding.AwayFromZero);
        var fromRest = numbersPerGame - fromTop - fromEmerging;
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));

        var baseMaxShared = BaseMaxShared(numbersPerGame);
        var games = new List<int[]>();
        var keys = new HashSet<string>();
        var maxSharedUsed = baseMaxShared;
        var balanceRelaxed = false;
        while (games.Count < quantity)
        {
            int[]? accepted = null;
            for (var maxShared = baseMaxShared; maxShared < numbersPerGame && accepted is null; maxShared++)
            for (var balanced = 1; balanced >= 0 && accepted is null; balanced--)
            for (var attempt = 0; attempt < AttemptsPerLevel; attempt++)
            {
                var picked = new HashSet<int>();
                PickWeighted(top, fromTop, picked, random);
                PickWeighted(emerging, fromEmerging, picked, random);
                PickWeighted(rest, fromRest, picked, random);
                // Os grupos podem ficar curtos quando o jogo pede mais dezenas do que um deles oferece: completa pelo score.
                if (picked.Count < numbersPerGame) PickWeighted(ordered, numbersPerGame - picked.Count, picked, random);
                var game = picked.Order().ToArray();
                if (keys.Contains(Key(game))) continue;
                if (games.Any(other => other.Intersect(game).Count() > maxShared)) continue;
                if (balanced == 1 && !IsBalanced(game)) continue;
                accepted = game;
                maxSharedUsed = Math.Max(maxSharedUsed, maxShared);
                balanceRelaxed |= balanced == 0;
                break;
            }
            // Último recurso: qualquer jogo inédito, sorteado de todas as dezenas pelo score.
            while (accepted is null)
            {
                var picked = new HashSet<int>();
                PickWeighted(ordered, numbersPerGame, picked, random);
                var game = picked.Order().ToArray();
                if (keys.Contains(Key(game))) continue;
                accepted = game; maxSharedUsed = numbersPerGame - 1; balanceRelaxed = true;
            }
            games.Add(accepted);
            keys.Add(Key(accepted));
        }
        return new GenerationResult(games.Select((x, i) => new LotofacilGame(i + 1, x)).ToList(), baseMaxShared, maxSharedUsed, balanceRelaxed);
    }

    /// <summary>
    /// Pares/ímpares, moldura/miolo e soma perto do esperado para um jogo de n dezenas sorteado ao acaso entre 1 e 25
    /// (12 pares, 16 na moldura, soma média 13n). Em 15 dezenas: 6–8 pares, 9–11 na moldura e soma ~168–222.
    /// </summary>
    public static bool IsBalanced(int[] game)
    {
        var n = game.Length;
        var evens = game.Count(x => x % 2 == 0);
        var frame = game.Count(Frame.Contains);
        var sum = game.Sum();
        var sumDeviation = Math.Sqrt(n * 52d * (25 - n) / 24d);
        return Math.Abs(evens - n * 12d / 25d) <= ParityTolerance
            && Math.Abs(frame - n * 16d / 25d) <= FrameTolerance
            && Math.Abs(sum - 13d * n) <= SumDeviations * sumDeviation;
    }

    public static long Combinations(int n, int k) => MegaSenaService.Combinations(n, k);

    public static decimal GameCost(int numbersPerGame) => Combinations(numbersPerGame, 15) * BetPrice;

    /// <summary>
    /// Um jogo de n dezenas equivale a C(n,15) apostas simples. Com k acertos, as apostas com exatamente j acertos
    /// são C(k,j) × C(n−k,15−j): escolhe j dezenas sorteadas e completa com as não sorteadas do jogo.
    /// </summary>
    public static LotofacilPrizeSummary Evaluate(int[] game, int[] drawn)
    {
        var hits = game.Count(drawn.Contains);
        var misses = game.Length - hits;
        long Count(int j) => Combinations(hits, j) * Combinations(misses, 15 - j);
        return new LotofacilPrizeSummary(hits, Count(15), Count(14), Count(13), Count(12), Count(11));
    }

    public static decimal ReturnAmount(LotofacilPrizeSummary prize, decimal prize15, decimal prize14, decimal prize13,
        decimal prize12, decimal prize11) =>
        prize.Count15 * prize15 + prize.Count14 * prize14
        + prize.Count13 * (prize13 > 0 ? prize13 : FixedPrize13)
        + prize.Count12 * (prize12 > 0 ? prize12 : FixedPrize12)
        + prize.Count11 * (prize11 > 0 ? prize11 : FixedPrize11);

    /// <summary>Acertos esperados de um jogo de n dezenas em um sorteio uniforme de 15 entre 25 (hipergeométrica).</summary>
    public static double ExpectedHits(int numbersPerGame) => numbersPerGame * 15d / 25d;

    private static void PickWeighted(List<LotofacilNumberScore> pool, int count, HashSet<int> picked, Random random)
    {
        var available = pool.Where(x => !picked.Contains(x.Number)).ToList();
        for (var i = 0; i < count && available.Count > 0; i++)
        {
            var total = available.Sum(x => x.Score + 0.05);
            var target = random.NextDouble() * total;
            var index = 0;
            for (; index < available.Count - 1; index++)
            {
                target -= available[index].Score + 0.05;
                if (target <= 0) break;
            }
            picked.Add(available[index].Number);
            available.RemoveAt(index);
        }
    }

    private static double[] Normalize(double[] values)
    {
        var slice = values.Skip(1).ToArray();
        var min = slice.Min(); var max = slice.Max();
        return values.Select((x, i) => i == 0 ? 0 : max - min < 1e-12 ? 0.5 : (x - min) / (max - min)).ToArray();
    }

    private static string Key(int[] game) => string.Join('-', game);

    // ---------- Persistência e consultas ----------

    public async Task<object> Generate(LotofacilGenerateRequest request)
    {
        if (request.WindowContests is < 10 or > 5000) throw new ArgumentException("A janela deve ter de 10 a 5000 concursos.");
        if (request.Quantity is < 1 or > 50) throw new ArgumentException("Informe entre 1 e 50 jogos.");
        if (request.NumbersPerGame is < MinNumbersPerGame or > MaxNumbersPerGame) throw new ArgumentException("Cada jogo deve ter de 15 a 20 dezenas.");
        await using var connection = db.Open();
        var latest = await connection.ExecuteScalarAsync<int?>("select max(contest) from lotofacil_draws")
            ?? throw new ArgumentException("Não há resultados da Lotofácil. Faça a carga histórica primeiro.");
        var target = request.TargetContest ?? latest + 1;
        if (target < 2 || target > latest + 1) throw new ArgumentException($"O concurso alvo deve estar entre 2 e {latest + 1}.");
        var history = (await connection.QueryAsync<DrawNumbersRow>(
            "select contest,numbers from lotofacil_draws where contest<@target order by contest", new { target }))
            .Select(x => x.Numbers.Select(n => (int)n).ToArray()).ToList();
        if (history.Count < 10) throw new ArgumentException("São necessários pelo menos 10 concursos anteriores ao alvo.");

        var seed = request.Seed ?? Random.Shared.NextInt64(1, 1_000_000_000);
        var scores = ScoreNumbers(history, request.WindowContests);
        var generation = BuildGames(scores, request.Quantity, request.NumbersPerGame, seed);
        var config = new
        {
            weights = Weights, composition = new { top = 0.6, emerging = 0.2, random = 0.2, topPool = TopPoolSize, emergingPool = EmergingPoolSize },
            momentumContests = MomentumContests, baseMaxShared = generation.BaseMaxShared, maxSharedUsed = generation.MaxSharedUsed,
            balanceRelaxed = generation.BalanceRelaxed, betPrice = BetPrice, historyContests = history.Count
        };
        var id = Guid.NewGuid();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into lotofacil_predictions(id,target_contest,seed,window_contests,numbers_per_game,quantity,total_cost,config,scores)
              values(@id,@target,@seed,@WindowContests,@NumbersPerGame,@Quantity,@totalCost,@config::jsonb,@scores::jsonb)",
            new { id, target, seed, request.WindowContests, request.NumbersPerGame, request.Quantity,
                totalCost = GameCost(request.NumbersPerGame) * request.Quantity,
                config = JsonSerializer.Serialize(config, JsonOptions), scores = JsonSerializer.Serialize(scores, JsonOptions) }, transaction);
        foreach (var game in generation.Games)
            await connection.ExecuteAsync("insert into lotofacil_prediction_games(prediction_id,rank,numbers) values(@id,@Rank,@numbers)",
                new { id, game.Rank, numbers = game.Numbers.Select(x => (short)x).ToArray() }, transaction);
        await transaction.CommitAsync();
        return (await Load(connection, [id], includeScores: true)).Single();
    }

    public async Task<object?> Get(Guid id)
    {
        await using var connection = db.Open();
        return (await Load(connection, [id], includeScores: true)).SingleOrDefault();
    }

    public async Task<object> List(int page = 1, int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = db.Open();
        var total = await connection.ExecuteScalarAsync<int>("select count(*) from lotofacil_predictions");
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var ids = (await connection.QueryAsync<Guid>(
            "select id from lotofacil_predictions order by generated_at desc,id offset @offset limit @pageSize",
            new { offset = (page - 1) * pageSize, pageSize })).ToArray();
        return new { items = await Load(connection, ids, includeScores: false), total, page, totalPages };
    }

    public async Task<int> Delete(List<Guid> ids)
    {
        await using var connection = db.Open();
        return await connection.ExecuteAsync("delete from lotofacil_predictions where id=any(@ids)", new { ids = ids.Distinct().ToArray() });
    }

    public async Task<object> Draws(int page = 1, int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = db.Open();
        var total = await connection.ExecuteScalarAsync<int>("select count(*) from lotofacil_draws");
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var rows = await connection.QueryAsync<DrawRow>(DrawSelect + " order by contest desc offset @offset limit @pageSize",
            new { offset = (page - 1) * pageSize, pageSize });
        var latest = await connection.ExecuteScalarAsync<int?>("select max(contest) from lotofacil_draws");
        return new { items = rows.Select(ToDto), total, page, totalPages, latestContest = latest,
            missing = latest is null ? 0 : latest.Value - total };
    }

    public async Task<object?> Draw(int contest)
    {
        await using var connection = db.Open();
        var row = await connection.QuerySingleOrDefaultAsync<DrawRow>(DrawSelect + " where contest=@contest", new { contest });
        return row is null ? null : ToDto(row);
    }

    public async Task<object> Statistics()
    {
        await using var connection = db.Open();
        var predictions = (await connection.QueryAsync<Guid>("select id from lotofacil_predictions")).ToArray();
        var details = await LoadEvaluations(connection, predictions);
        var evaluated = details.Where(x => x.Draw is not null).ToList();
        var games = evaluated.SelectMany(x => x.Games).ToList();
        var totalBet = evaluated.Sum(x => x.Prediction.TotalCost);
        var returned = games.Sum(x => x.ReturnAmount);
        var draws = (await connection.QueryAsync<DrawNumbersRow>("select contest,numbers from lotofacil_draws order by contest")).ToList();
        var counts = new int[26]; var lastSeen = new int[26];
        foreach (var draw in draws) foreach (var n in draw.Numbers) { counts[n]++; lastSeen[n] = draw.Contest; }
        var latest = draws.Count == 0 ? 0 : draws[^1].Contest;
        var numbers = Enumerable.Range(1, 25).Select(n => new { number = n, count = counts[n],
            delay = lastSeen[n] == 0 ? latest : latest - lastSeen[n] }).ToList();
        return new
        {
            predictions = details.Count, evaluatedPredictions = evaluated.Count, pendingPredictions = details.Count - evaluated.Count,
            pendingCost = details.Where(x => x.Draw is null).Sum(x => x.Prediction.TotalCost),
            evaluatedGames = games.Count, totalBet, returnAmount = returned, balance = returned - totalBet,
            roiPercent = totalBet == 0 ? (decimal?)null : Math.Round((returned - totalBet) / totalBet * 100m, 2),
            count15 = games.Sum(x => x.Count15), count14 = games.Sum(x => x.Count14), count13 = games.Sum(x => x.Count13),
            count12 = games.Sum(x => x.Count12), count11 = games.Sum(x => x.Count11),
            hitDistribution = Enumerable.Range(0, 16).Select(h => new { hits = h, games = games.Count(x => x.Hits == h) }),
            averageHits = games.Count == 0 ? (double?)null : Math.Round(games.Average(x => x.Hits), 4),
            expectedHits = games.Count == 0 ? (double?)null
                : Math.Round(evaluated.SelectMany(x => x.Games.Select(_ => ExpectedHits(x.Prediction.NumbersPerGame))).Average(), 4),
            draws = draws.Count, latestContest = latest == 0 ? (int?)null : latest,
            mostDrawn = numbers.OrderByDescending(x => x.count).ThenBy(x => x.number).Take(10),
            leastDrawn = numbers.OrderBy(x => x.count).ThenBy(x => x.number).Take(10),
            mostDelayed = numbers.OrderByDescending(x => x.delay).ThenBy(x => x.number).Take(10),
            warning = Warning
        };
    }

    // ---------- Carregamento e avaliação ----------

    private sealed record Evaluated(PredictionRow Prediction, List<GameRow> RawGames, DrawRow? Draw, List<LotofacilGameResult> Games);

    private async Task<List<Evaluated>> LoadEvaluations(Npgsql.NpgsqlConnection connection, Guid[] ids)
    {
        if (ids.Length == 0) return [];
        var predictions = (await connection.QueryAsync<PredictionRow>(
            @"select id,target_contest as TargetContest,generated_at as GeneratedAt,seed,window_contests as WindowContests,
                numbers_per_game as NumbersPerGame,quantity,total_cost as TotalCost,config::text as ConfigJson,
                scores::text as ScoresJson,status from lotofacil_predictions where id=any(@ids)", new { ids })).ToDictionary(x => x.Id);
        var games = (await connection.QueryAsync<GameRow>(
            "select prediction_id as PredictionId,rank,numbers from lotofacil_prediction_games where prediction_id=any(@ids) order by rank",
            new { ids })).GroupBy(x => x.PredictionId).ToDictionary(x => x.Key, x => x.ToList());
        var contests = predictions.Values.Select(x => x.TargetContest).Distinct().ToArray();
        var draws = (await connection.QueryAsync<DrawRow>(DrawSelect + " where contest=any(@contests)", new { contests }))
            .ToDictionary(x => x.Contest);
        var result = new List<Evaluated>();
        var newlyEvaluated = new List<Guid>();
        foreach (var id in ids)
        {
            if (!predictions.TryGetValue(id, out var prediction)) continue;
            var raw = games.GetValueOrDefault(id) ?? [];
            draws.TryGetValue(prediction.TargetContest, out var draw);
            var evaluatedGames = new List<LotofacilGameResult>();
            if (draw is not null)
            {
                var drawn = draw.Numbers.Select(x => (int)x).ToArray();
                foreach (var game in raw)
                {
                    var numbers = game.Numbers.Select(x => (int)x).ToArray();
                    var prize = Evaluate(numbers, drawn);
                    evaluatedGames.Add(new LotofacilGameResult(game.Rank, numbers, prize.Hits, numbers.Where(drawn.Contains).ToArray(),
                        prize.Count15, prize.Count14, prize.Count13, prize.Count12, prize.Count11,
                        ReturnAmount(prize, draw.Prize15, draw.Prize14, draw.Prize13, draw.Prize12, draw.Prize11)));
                }
                if (prediction.Status != "EVALUATED") newlyEvaluated.Add(id);
            }
            result.Add(new Evaluated(prediction, raw, draw, evaluatedGames));
        }
        if (newlyEvaluated.Count > 0)
            await connection.ExecuteAsync("update lotofacil_predictions set status='EVALUATED' where id=any(@ids)", new { ids = newlyEvaluated.ToArray() });
        return result;
    }

    private async Task<List<object>> Load(Npgsql.NpgsqlConnection connection, Guid[] ids, bool includeScores)
    {
        var items = new List<object>();
        foreach (var item in await LoadEvaluations(connection, ids))
        {
            var p = item.Prediction;
            var evaluated = item.Draw is not null;
            decimal? returned = evaluated ? item.Games.Sum(x => x.ReturnAmount) : null;
            items.Add(new
            {
                p.Id, p.TargetContest, p.GeneratedAt, p.Seed, p.WindowContests, p.NumbersPerGame, p.Quantity, p.TotalCost,
                costPerGame = GameCost(p.NumbersPerGame), betsPerGame = Combinations(p.NumbersPerGame, 15),
                status = evaluated ? "EVALUATED" : "PENDING",
                games = evaluated ? item.Games.Cast<object>().ToList()
                    : item.RawGames.Select(g => (object)new { g.Rank, numbers = g.Numbers.Select(x => (int)x).ToArray() }).ToList(),
                draw = item.Draw is null ? null : ToDto(item.Draw),
                bestHits = evaluated ? item.Games.Max(x => (int?)x.Hits) : null,
                count15 = evaluated ? item.Games.Sum(x => x.Count15) : (long?)null,
                count14 = evaluated ? item.Games.Sum(x => x.Count14) : (long?)null,
                count13 = evaluated ? item.Games.Sum(x => x.Count13) : (long?)null,
                count12 = evaluated ? item.Games.Sum(x => x.Count12) : (long?)null,
                count11 = evaluated ? item.Games.Sum(x => x.Count11) : (long?)null,
                returnAmount = returned, profitAmount = returned - p.TotalCost,
                expectedHitsPerGame = ExpectedHits(p.NumbersPerGame),
                config = JsonSerializer.Deserialize<JsonElement>(p.ConfigJson),
                scores = includeScores ? JsonSerializer.Deserialize<JsonElement>(p.ScoresJson) : (JsonElement?)null,
                warning = Warning
            });
        }
        return items;
    }

    private const string DrawSelect = @"select contest,draw_date as DrawDate,numbers,winners15,prize15,winners14,prize14,
        winners13,prize13,winners12,prize12,winners11,prize11,accumulated,next_estimate as NextEstimate,source,
        imported_at as ImportedAt from lotofacil_draws";

    private static object ToDto(DrawRow row) => new
    {
        row.Contest, drawDate = row.DrawDate.ToString("yyyy-MM-dd"), numbers = row.Numbers.Select(x => (int)x).ToArray(),
        row.Winners15, row.Prize15, row.Winners14, row.Prize14, row.Winners13, row.Prize13,
        row.Winners12, row.Prize12, row.Winners11, row.Prize11, row.Accumulated, row.NextEstimate, row.Source, row.ImportedAt
    };
}
