using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed partial class MegaSenaService(Db db)
{
    public const decimal BetPrice = 6.00m;
    public const long SimpleBetOdds = 50_063_860;
    private const int MomentumContests = 10;
    private const int TopPoolSize = 20;
    private const int EmergingPoolSize = 15;
    private const int AffinityTopNumbers = 15;
    private const int BaseMaxShared = 3;
    private const int AttemptsPerLevel = 300;
    private const string Warning = "Cada aposta simples de 6 dezenas tem 1 chance em 50.063.860 de acertar a sena. " +
        "O score ordena as dezenas pelo histórico e não altera essa probabilidade: todos os sorteios são independentes.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        ["windowFrequency"] = 0.35, ["historyFrequency"] = 0.20, ["delay"] = 0.10, ["momentum"] = 0.20, ["pairAffinity"] = 0.15
    };

    // Linhas lidas pelo Dapper são classes com setters: o Npgsql informa colunas smallint[] como System.Array,
    // o que impede o Dapper de casar construtores posicionais (records/tuplas) e falha na materialização.
    public sealed class DrawRow
    {
        public int Contest { get; set; }
        public DateTime DrawDate { get; set; }
        public short[] Numbers { get; set; } = [];
        public int SenaWinners { get; set; }
        public decimal SenaPrize { get; set; }
        public int QuinaWinners { get; set; }
        public decimal QuinaPrize { get; set; }
        public int QuadraWinners { get; set; }
        public decimal QuadraPrize { get; set; }
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

    public sealed record GenerationResult(List<MegaSenaGame> Games, int MaxSharedUsed, bool BalanceRelaxed);

    // ---------- Funções puras (testadas sem banco) ----------

    /// <summary>Pontua as dezenas 1–60 usando somente os concursos informados, em ordem cronológica.</summary>
    public static List<MegaSenaNumberScore> ScoreNumbers(IReadOnlyList<int[]> history, int windowContests)
    {
        if (history.Count == 0) throw new ArgumentException("Não há concursos anteriores para pontuar as dezenas.");
        var window = history.Skip(Math.Max(0, history.Count - windowContests)).ToList();
        var recent = history.Skip(Math.Max(0, history.Count - MomentumContests)).ToList();
        var windowHits = new int[61]; var historyHits = new int[61]; var recentHits = new int[61]; var delay = new int[61];
        for (var n = 1; n <= 60; n++) delay[n] = history.Count;
        for (var i = 0; i < history.Count; i++)
            foreach (var n in history[i]) { historyHits[n]++; delay[n] = history.Count - 1 - i; }
        foreach (var draw in window) foreach (var n in draw) windowHits[n]++;
        foreach (var draw in recent) foreach (var n in draw) recentHits[n]++;

        var momentum = new double[61];
        for (var n = 1; n <= 60; n++)
            momentum[n] = (double)recentHits[n] / recent.Count - (double)windowHits[n] / window.Count;

        // A afinidade mede quantas vezes a dezena saiu junto das dezenas mais frequentes da janela.
        var leaders = Enumerable.Range(1, 60).OrderByDescending(n => windowHits[n] * 1000 + historyHits[n]).ThenBy(n => n)
            .Take(AffinityTopNumbers).ToHashSet();
        var affinity = new double[61];
        foreach (var draw in window)
            foreach (var n in draw)
                affinity[n] += draw.Count(other => other != n && leaders.Contains(other));

        var wf = Normalize(windowHits.Select(x => (double)x).ToArray());
        var hf = Normalize(historyHits.Select(x => (double)x).ToArray());
        var dl = Normalize(delay.Select(x => (double)x).ToArray());
        var mo = Normalize(momentum);
        var pa = Normalize(affinity);
        return Enumerable.Range(1, 60).Select(n =>
        {
            var features = new MegaSenaNumberFeatures(Math.Round(wf[n], 4), Math.Round(hf[n], 4), Math.Round(dl[n], 4),
                Math.Round(mo[n], 4), Math.Round(pa[n], 4));
            var score = Weights["windowFrequency"] * wf[n] + Weights["historyFrequency"] * hf[n] + Weights["delay"] * dl[n]
                + Weights["momentum"] * mo[n] + Weights["pairAffinity"] * pa[n];
            return new MegaSenaNumberScore(n, Math.Round(score, 4), features, windowHits[n], historyHits[n], delay[n]);
        }).OrderByDescending(x => x.Score).ThenBy(x => x.Number).ToList();
    }

    /// <summary>~60% das dezenas vêm do topo do score, ~20% das emergentes (momentum) e ~20% do restante, ponderado.</summary>
    public static GenerationResult BuildGames(IReadOnlyList<MegaSenaNumberScore> scores, int quantity, int numbersPerGame, long seed)
    {
        if (quantity is < 1 or > 50) throw new ArgumentException("Informe entre 1 e 50 jogos.");
        if (numbersPerGame is < 6 or > 15) throw new ArgumentException("Cada jogo deve ter de 6 a 15 dezenas.");
        if (scores.Count != 60) throw new ArgumentException("O score precisa cobrir as 60 dezenas.");
        var ordered = scores.OrderByDescending(x => x.Score).ThenBy(x => x.Number).ToList();
        var top = ordered.Take(TopPoolSize).ToList();
        var emerging = ordered.Skip(TopPoolSize).OrderByDescending(x => x.Features.Momentum).ThenByDescending(x => x.Score)
            .ThenBy(x => x.Number).Take(EmergingPoolSize).ToList();
        var rest = ordered.Except(top).Except(emerging).ToList();
        var fromTop = (int)Math.Round(numbersPerGame * 0.6, MidpointRounding.AwayFromZero);
        var fromEmerging = (int)Math.Round(numbersPerGame * 0.2, MidpointRounding.AwayFromZero);
        var fromRest = numbersPerGame - fromTop - fromEmerging;
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));

        var games = new List<int[]>();
        var keys = new HashSet<string>();
        var maxSharedUsed = BaseMaxShared;
        var balanceRelaxed = false;
        while (games.Count < quantity)
        {
            int[]? accepted = null;
            for (var maxShared = BaseMaxShared; maxShared < numbersPerGame && accepted is null; maxShared++)
            for (var balanced = 1; balanced >= 0 && accepted is null; balanced--)
            for (var attempt = 0; attempt < AttemptsPerLevel; attempt++)
            {
                var picked = new HashSet<int>();
                PickWeighted(top, fromTop, picked, random);
                PickWeighted(emerging, fromEmerging, picked, random);
                PickWeighted(rest, fromRest, picked, random);
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
        return new GenerationResult(games.Select((x, i) => new MegaSenaGame(i + 1, x)).ToList(), maxSharedUsed, balanceRelaxed);
    }

    public static bool IsBalanced(int[] game)
    {
        var tolerance = Math.Max(1, (int)Math.Ceiling(game.Length / 6d));
        var evens = game.Count(x => x % 2 == 0);
        var low = game.Count(x => x <= 30);
        return Math.Abs(2 * evens - game.Length) <= 2 * tolerance && Math.Abs(2 * low - game.Length) <= 2 * tolerance;
    }

    public static long Combinations(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        long result = 1;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }

    public static decimal GameCost(int numbersPerGame) => Combinations(numbersPerGame, 6) * BetPrice;

    /// <summary>Um jogo de n dezenas equivale a C(n,6) apostas simples; conta quantas delas fazem 6, 5 e 4 acertos.</summary>
    public static MegaSenaPrizeSummary Evaluate(int[] game, int[] drawn)
    {
        var hits = game.Count(drawn.Contains);
        var misses = game.Length - hits;
        return new MegaSenaPrizeSummary(hits,
            Combinations(hits, 6) * Combinations(misses, 0),
            Combinations(hits, 5) * Combinations(misses, 1),
            Combinations(hits, 4) * Combinations(misses, 2));
    }

    public static decimal ReturnAmount(MegaSenaPrizeSummary prize, decimal senaPrize, decimal quinaPrize, decimal quadraPrize) =>
        prize.SenaCount * senaPrize + prize.QuinaCount * quinaPrize + prize.QuadraCount * quadraPrize;

    /// <summary>Acertos esperados de um jogo de n dezenas em um sorteio uniforme de 6 entre 60 (hipergeométrica).</summary>
    public static double ExpectedHits(int numbersPerGame) => numbersPerGame * 6d / 60d;

    public static double HitsVariance(int numbersPerGame) =>
        numbersPerGame * (6d / 60d) * (54d / 60d) * ((60d - numbersPerGame) / 59d);

    private static void PickWeighted(List<MegaSenaNumberScore> pool, int count, HashSet<int> picked, Random random)
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

    public async Task<object> Generate(MegaSenaGenerateRequest request)
    {
        if (request.WindowContests is < 10 or > 5000) throw new ArgumentException("A janela deve ter de 10 a 5000 concursos.");
        if (request.Quantity is < 1 or > 50) throw new ArgumentException("Informe entre 1 e 50 jogos.");
        if (request.NumbersPerGame is < 6 or > 15) throw new ArgumentException("Cada jogo deve ter de 6 a 15 dezenas.");
        await using var connection = db.Open();
        var latest = await connection.ExecuteScalarAsync<int?>("select max(contest) from megasena_draws")
            ?? throw new ArgumentException("Não há resultados da Mega-Sena. Faça a carga histórica primeiro.");
        var target = request.TargetContest ?? latest + 1;
        if (target < 2 || target > latest + 1) throw new ArgumentException($"O concurso alvo deve estar entre 2 e {latest + 1}.");
        var history = (await connection.QueryAsync<DrawNumbersRow>(
            "select contest,numbers from megasena_draws where contest<@target order by contest", new { target }))
            .Select(x => x.Numbers.Select(n => (int)n).ToArray()).ToList();
        if (history.Count < 10) throw new ArgumentException("São necessários pelo menos 10 concursos anteriores ao alvo.");

        var seed = request.Seed ?? Random.Shared.NextInt64(1, 1_000_000_000);
        var scores = ScoreNumbers(history, request.WindowContests);
        var generation = BuildGames(scores, request.Quantity, request.NumbersPerGame, seed);
        var config = new
        {
            weights = Weights, composition = new { top = 0.6, emerging = 0.2, random = 0.2, topPool = TopPoolSize, emergingPool = EmergingPoolSize },
            momentumContests = MomentumContests, baseMaxShared = BaseMaxShared, maxSharedUsed = generation.MaxSharedUsed,
            balanceRelaxed = generation.BalanceRelaxed, betPrice = BetPrice, historyContests = history.Count
        };
        var id = Guid.NewGuid();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into megasena_predictions(id,target_contest,seed,window_contests,numbers_per_game,quantity,total_cost,config,scores)
              values(@id,@target,@seed,@WindowContests,@NumbersPerGame,@Quantity,@totalCost,@config::jsonb,@scores::jsonb)",
            new { id, target, seed, request.WindowContests, request.NumbersPerGame, request.Quantity,
                totalCost = GameCost(request.NumbersPerGame) * request.Quantity,
                config = JsonSerializer.Serialize(config, JsonOptions), scores = JsonSerializer.Serialize(scores, JsonOptions) }, transaction);
        foreach (var game in generation.Games)
            await connection.ExecuteAsync("insert into megasena_prediction_games(prediction_id,rank,numbers) values(@id,@Rank,@numbers)",
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
        var total = await connection.ExecuteScalarAsync<int>("select count(*) from megasena_predictions");
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var ids = (await connection.QueryAsync<Guid>(
            "select id from megasena_predictions order by generated_at desc,id offset @offset limit @pageSize",
            new { offset = (page - 1) * pageSize, pageSize })).ToArray();
        return new { items = await Load(connection, ids, includeScores: false), total, page, totalPages };
    }

    public async Task<int> Delete(List<Guid> ids)
    {
        await using var connection = db.Open();
        return await connection.ExecuteAsync("delete from megasena_predictions where id=any(@ids)", new { ids = ids.Distinct().ToArray() });
    }

    public async Task<object> Draws(int page = 1, int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = db.Open();
        var total = await connection.ExecuteScalarAsync<int>("select count(*) from megasena_draws");
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var rows = await connection.QueryAsync<DrawRow>(DrawSelect + " order by contest desc offset @offset limit @pageSize",
            new { offset = (page - 1) * pageSize, pageSize });
        var latest = await connection.ExecuteScalarAsync<int?>("select max(contest) from megasena_draws");
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
        var predictions = (await connection.QueryAsync<Guid>("select id from megasena_predictions")).ToArray();
        var details = await LoadEvaluations(connection, predictions);
        var evaluated = details.Where(x => x.Draw is not null).ToList();
        var games = evaluated.SelectMany(x => x.Games).ToList();
        var totalBet = evaluated.Sum(x => x.Prediction.TotalCost);
        var returned = games.Sum(x => x.ReturnAmount);
        var draws = (await connection.QueryAsync<DrawNumbersRow>("select contest,numbers from megasena_draws order by contest")).ToList();
        var counts = new int[61]; var lastSeen = new int[61];
        foreach (var draw in draws) foreach (var n in draw.Numbers) { counts[n]++; lastSeen[n] = draw.Contest; }
        var latest = draws.Count == 0 ? 0 : draws[^1].Contest;
        var numbers = Enumerable.Range(1, 60).Select(n => new { number = n, count = counts[n],
            delay = lastSeen[n] == 0 ? latest : latest - lastSeen[n] }).ToList();
        return new
        {
            predictions = details.Count, evaluatedPredictions = evaluated.Count, pendingPredictions = details.Count - evaluated.Count,
            pendingCost = details.Where(x => x.Draw is null).Sum(x => x.Prediction.TotalCost),
            evaluatedGames = games.Count, totalBet, returnAmount = returned, balance = returned - totalBet,
            roiPercent = totalBet == 0 ? (decimal?)null : Math.Round((returned - totalBet) / totalBet * 100m, 2),
            senaCount = games.Sum(x => x.SenaCount), quinaCount = games.Sum(x => x.QuinaCount), quadraCount = games.Sum(x => x.QuadraCount),
            hitDistribution = Enumerable.Range(0, 7).Select(h => new { hits = h, games = games.Count(x => x.Hits == h) }),
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

    /// <summary>Walk-forward: cada concurso é previsto só com os anteriores e comparado a jogos aleatórios e à expectativa exata.</summary>
    public async Task<object> Backtest(int contests = 200, int numbersPerGame = 6, int quantity = 5, int windowContests = 100)
    {
        contests = Math.Clamp(contests, 10, 1000);
        quantity = Math.Clamp(quantity, 1, 20);
        numbersPerGame = Math.Clamp(numbersPerGame, 6, 15);
        windowContests = Math.Clamp(windowContests, 10, 5000);
        await using var connection = db.Open();
        var draws = (await connection.QueryAsync<DrawNumbersRow>("select contest,numbers from megasena_draws order by contest"))
            .Select(x => (x.Contest, Numbers: x.Numbers.Select(n => (int)n).ToArray())).ToList();
        if (draws.Count < 60) throw new ArgumentException("São necessários pelo menos 60 concursos importados para o backtest.");
        contests = Math.Min(contests, draws.Count - 50);
        var modelHits = new List<int>(); var randomHits = new List<int>();
        int modelQuadraPlus = 0, randomQuadraPlus = 0;
        for (var index = draws.Count - contests; index < draws.Count; index++)
        {
            var (contest, drawn) = draws[index];
            var history = draws.Take(index).Select(x => x.Numbers).ToList();
            var generation = BuildGames(ScoreNumbers(history, windowContests), quantity, numbersPerGame, contest);
            var random = new Random(contest * 7919);
            foreach (var game in generation.Games)
            {
                var hits = game.Numbers.Count(drawn.Contains);
                modelHits.Add(hits); if (hits >= 4) modelQuadraPlus++;
                var randomGame = Enumerable.Range(1, 60).OrderBy(_ => random.Next()).Take(numbersPerGame).ToArray();
                var randomHit = randomGame.Count(drawn.Contains);
                randomHits.Add(randomHit); if (randomHit >= 4) randomQuadraPlus++;
            }
        }
        var expected = ExpectedHits(numbersPerGame);
        var modelAverage = modelHits.Average();
        var standardError = Math.Sqrt(HitsVariance(numbersPerGame) / modelHits.Count);
        var z = (modelAverage - expected) / standardError;
        var conclusion = Math.Abs(z) < 2
            ? "A média de acertos do modelo é compatível com o acaso (|z| < 2): não há evidência de vantagem sobre jogos aleatórios."
            : z > 0
                ? "A média ficou acima do esperado (z ≥ 2) nesta amostra. Com muitos testes, isso ainda pode ser acaso; confirme em outro período antes de tirar conclusões."
                : "A média ficou abaixo do esperado (z ≤ -2) nesta amostra, o que também é compatível com flutuação aleatória em testes repetidos.";
        return new
        {
            contests, firstContest = draws[^contests].Contest, lastContest = draws[^1].Contest, quantity, numbersPerGame, windowContests,
            games = modelHits.Count,
            model = new { averageHits = Math.Round(modelAverage, 4), quadraOrBetter = modelQuadraPlus,
                distribution = Enumerable.Range(0, 7).Select(h => new { hits = h, games = modelHits.Count(x => x == h) }) },
            random = new { averageHits = Math.Round(randomHits.Average(), 4), quadraOrBetter = randomQuadraPlus,
                distribution = Enumerable.Range(0, 7).Select(h => new { hits = h, games = randomHits.Count(x => x == h) }) },
            expectedHits = Math.Round(expected, 4), standardError = Math.Round(standardError, 4), zScore = Math.Round(z, 2),
            conclusion,
            methodology = "Para cada concurso, o score usa apenas concursos anteriores. Os jogos aleatórios têm a mesma quantidade de dezenas. " +
                "A expectativa exata vem da distribuição hipergeométrica: n × 6 / 60 acertos por jogo. O z-score trata os jogos como independentes, uma aproximação.",
            warning = Warning
        };
    }

    // ---------- Carregamento e avaliação ----------

    private sealed record Evaluated(PredictionRow Prediction, List<GameRow> RawGames, DrawRow? Draw, List<MegaSenaGameResult> Games);

    private async Task<List<Evaluated>> LoadEvaluations(Npgsql.NpgsqlConnection connection, Guid[] ids)
    {
        if (ids.Length == 0) return [];
        var predictions = (await connection.QueryAsync<PredictionRow>(
            @"select id,target_contest as TargetContest,generated_at as GeneratedAt,seed,window_contests as WindowContests,
                numbers_per_game as NumbersPerGame,quantity,total_cost as TotalCost,config::text as ConfigJson,
                scores::text as ScoresJson,status from megasena_predictions where id=any(@ids)", new { ids })).ToDictionary(x => x.Id);
        var games = (await connection.QueryAsync<GameRow>(
            "select prediction_id as PredictionId,rank,numbers from megasena_prediction_games where prediction_id=any(@ids) order by rank",
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
            var evaluatedGames = new List<MegaSenaGameResult>();
            if (draw is not null)
            {
                var drawn = draw.Numbers.Select(x => (int)x).ToArray();
                foreach (var game in raw)
                {
                    var numbers = game.Numbers.Select(x => (int)x).ToArray();
                    var prize = Evaluate(numbers, drawn);
                    evaluatedGames.Add(new MegaSenaGameResult(game.Rank, numbers, prize.Hits, numbers.Where(drawn.Contains).ToArray(),
                        prize.SenaCount, prize.QuinaCount, prize.QuadraCount,
                        ReturnAmount(prize, draw.SenaPrize, draw.QuinaPrize, draw.QuadraPrize)));
                }
                if (prediction.Status != "EVALUATED") newlyEvaluated.Add(id);
            }
            result.Add(new Evaluated(prediction, raw, draw, evaluatedGames));
        }
        if (newlyEvaluated.Count > 0)
            await connection.ExecuteAsync("update megasena_predictions set status='EVALUATED' where id=any(@ids)", new { ids = newlyEvaluated.ToArray() });
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
                costPerGame = GameCost(p.NumbersPerGame), betsPerGame = Combinations(p.NumbersPerGame, 6),
                status = evaluated ? "EVALUATED" : "PENDING",
                games = evaluated ? item.Games.Cast<object>().ToList()
                    : item.RawGames.Select(g => (object)new { g.Rank, numbers = g.Numbers.Select(x => (int)x).ToArray() }).ToList(),
                draw = item.Draw is null ? null : ToDto(item.Draw),
                bestHits = evaluated ? item.Games.Max(x => (int?)x.Hits) : null,
                senaCount = evaluated ? item.Games.Sum(x => x.SenaCount) : (long?)null,
                quinaCount = evaluated ? item.Games.Sum(x => x.QuinaCount) : (long?)null,
                quadraCount = evaluated ? item.Games.Sum(x => x.QuadraCount) : (long?)null,
                returnAmount = returned, profitAmount = returned - p.TotalCost,
                expectedHitsPerGame = ExpectedHits(p.NumbersPerGame),
                config = JsonSerializer.Deserialize<JsonElement>(p.ConfigJson),
                scores = includeScores ? JsonSerializer.Deserialize<JsonElement>(p.ScoresJson) : (JsonElement?)null,
                warning = Warning
            });
        }
        return items;
    }

    private const string DrawSelect = @"select contest,draw_date as DrawDate,numbers,sena_winners as SenaWinners,sena_prize as SenaPrize,
        quina_winners as QuinaWinners,quina_prize as QuinaPrize,quadra_winners as QuadraWinners,quadra_prize as QuadraPrize,
        accumulated,next_estimate as NextEstimate,source,imported_at as ImportedAt from megasena_draws";

    private static object ToDto(DrawRow row) => new
    {
        row.Contest, drawDate = row.DrawDate.ToString("yyyy-MM-dd"), numbers = row.Numbers.Select(x => (int)x).ToArray(),
        row.SenaWinners, row.SenaPrize, row.QuinaWinners, row.QuinaPrize, row.QuadraWinners, row.QuadraPrize,
        row.Accumulated, row.NextEstimate, row.Source, row.ImportedAt
    };
}
