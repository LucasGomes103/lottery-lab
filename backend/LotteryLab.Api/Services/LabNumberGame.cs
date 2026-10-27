namespace LotteryLab.Api.Services;

/// <summary>Um concurso com os prêmios por apostador de cada faixa, na ordem de <see cref="LabNumberGameSpec.Tiers"/> (0 = sem valor).</summary>
public sealed record LabNumberDraw(int Contest, DateTime Date, int[] Numbers, decimal[] Prizes)
{
    public ulong Mask { get; } = LabLotteryOdds.Mask(Numbers);
}

public sealed record LabNumberOptions(int TestContests, int TrainContests, int Quantity, int WheelNumbers);

public sealed class LabNumberGameSpec
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required LabLotteryOdds Odds { get; init; }
    public required decimal BetPrice { get; init; }
    public required string[] TierLabels { get; init; }
    public required int HotPool { get; init; }
    public required int[] BigGameSizes { get; init; }
    public required bool HasWheel { get; init; }
    public required Func<IReadOnlyList<int[]>, int, long, List<int[]>> AppGames { get; init; }
    public Func<int, decimal>? FallbackPrize { get; init; }
    public int[] Tiers => Odds.Tiers;
}

public static class LabNumberGame
{
    public static readonly int[] Windows = [10, 25, 50, 100, 200];
    public const int AppWindowContests = 100;
    public const int MinHistory = 30;
    public const int MaxTestContests = 1000;
    public const int MaxQuantity = 50;
    public const string WheelGuarantee = "4se4";

    public static readonly LabNumberGameSpec MegaSena = new()
    {
        Code = "megasena", Name = "Mega-Sena", Odds = new LabLotteryOdds(60, 6, 6, [4, 5, 6], 4), BetPrice = MegaSenaService.BetPrice,
        TierLabels = ["Quadra", "Quina", "Sena"], HotPool = 12, BigGameSizes = [7, 8], HasWheel = true,
        AppGames = (history, quantity, seed) => MegaSenaService.BuildGames(MegaSenaService.ScoreNumbers(history, AppWindowContests), quantity, 6, seed)
            .Games.Select(x => x.Numbers).ToList()
    };

    public static readonly LabNumberGameSpec Lotofacil = new()
    {
        Code = "lotofacil", Name = "Lotofácil", Odds = new LabLotteryOdds(25, 15, 15, [11, 12, 13, 14, 15], 11), BetPrice = LotofacilService.BetPrice,
        TierLabels = ["11 acertos", "12 acertos", "13 acertos", "14 acertos", "15 acertos"], HotPool = 18, BigGameSizes = [16, 17, 18], HasWheel = false,
        AppGames = (history, quantity, seed) => LotofacilService.BuildGames(LotofacilService.ScoreNumbers(history, AppWindowContests), quantity, 15, seed)
            .Games.Select(x => x.Numbers).ToList(),
        // Mesmos valores fixos que a conferência da Lotofácil usa quando o rateio ainda não traz o prêmio.
        FallbackPrize = tier => tier switch { 11 => LotofacilService.FixedPrize11, 12 => LotofacilService.FixedPrize12, 13 => LotofacilService.FixedPrize13, _ => 0m }
    };

    public static object Run(LabNumberGameSpec spec, IReadOnlyList<LabNumberDraw> draws, LabNumberOptions options, Action<int, int>? progress = null)
    {
        var testStart = draws.Count - Math.Min(options.TestContests, MaxTestContests);
        if (testStart < MinHistory + 10)
            throw new ArgumentException($"São necessários ao menos {MinHistory + 10 + options.TestContests} concursos importados para esse período de teste.");
        var trainCount = Math.Clamp(options.TrainContests, 0, testStart - MinHistory);
        var train = Enumerable.Range(testStart - trainCount, trainCount).ToList();
        var test = Enumerable.Range(testStart, draws.Count - testStart).ToList();
        var n = options.Quantity;
        var b = spec.Odds.BetSize;
        var wheel = spec.HasWheel
            ? new MegaSenaWheelService().Build(Enumerable.Range(1, options.WheelNumbers), WheelGuarantee).Games
            : [];

        var structureRows = spec.BigGameSizes.Length * 2 + (spec.HasWheel ? 2 : 0);
        var total = train.Count * Windows.Length * 2 + test.Count * (4 + structureRows);
        var done = 0;
        void Step() => progress?.Invoke(Interlocked.Increment(ref done), total);

        // Treino: janelas de quentes e frias escolhidas pela média de dezenas acertadas, antes do teste.
        object Choose(string name, bool hot, out int best)
        {
            var candidates = Windows.Select(window =>
            {
                var games = Play(train, (h, k) => PoolGames(spec, h, k, n, window, hot));
                var hits = train.Select((index, i) => games[i].Sum(g => System.Numerics.BitOperations.PopCount(LabLotteryOdds.Mask(g) & draws[index].Mask))).Sum();
                return new { value = window, score = train.Count == 0 ? 0 : Math.Round((double)hits / (train.Count * n), 4) };
            }).ToList();
            best = train.Count == 0 ? 50 : candidates.OrderByDescending(x => x.score).ThenByDescending(x => x.value).First().value;
            return new { strategy = name, parameter = "janela (concursos)", chosen = best, metric = "dezenas acertadas por jogo", candidates };
        }
        var choices = new[] { Choose("Quentes", true, out var hotWindow), Choose("Frias", false, out var coldWindow) };

        var rows = new List<(LabTally Tally, int[][][] Games)>
        {
            (Tally("selecao", "app", "Gerador do app", $"{n} jogos do gerador da tela {spec.Name} (score com janela de {AppWindowContests} concursos, semente = concurso)."),
                Play(test, (h, k) => spec.AppGames(h.Select(x => x.Numbers).ToList(), n, k))),
            (Tally("selecao", "quentes", "Dezenas quentes", $"Jogo 1 com as {b} mais sorteadas na janela; os demais sorteados entre as {spec.HotPool} mais sorteadas.", $"janela de {hotWindow} concursos ({(train.Count == 0 ? "padrão, sem treino" : "treino")})"),
                Play(test, (h, k) => PoolGames(spec, h, k, n, hotWindow, true))),
            (Tally("selecao", "frias", "Dezenas frias", $"Jogo 1 com as {b} menos sorteadas na janela; os demais sorteados entre as {spec.HotPool} menos sorteadas.", $"janela de {coldWindow} concursos ({(train.Count == 0 ? "padrão, sem treino" : "treino")})"),
                Play(test, (h, k) => PoolGames(spec, h, k, n, coldWindow, false))),
            (Tally("selecao", "aleatorio", "Aleatório", $"{n} jogos simples sorteados com semente fixa.", baseline: true),
                Play(test, (_, k) => RandomGames(spec, k, "selecao", n, b))),
        };
        var groups = new List<object>
        {
            new { code = "selecao", title = "Seleção das dezenas", description = $"Mesmo orçamento: {n} apostas simples de {b} dezenas ({Money(n * spec.BetPrice)} por concurso)." }
        };
        foreach (var size in spec.BigGameSizes)
        {
            var bets = (int)spec.Odds.BetsIn(size);
            var group = $"estrutura-{size}";
            groups.Add(new { code = group, title = $"Estrutura: 1 jogo de {size} × {bets} simples", description = $"Mesmo orçamento de {bets} apostas ({Money(bets * spec.BetPrice)}), todas as dezenas sorteadas: muda só a forma de apostar." });
            rows.Add((Tally(group, $"jogo-{size}", $"1 jogo de {size} dezenas", $"Um único jogo de {size} dezenas sorteadas (equivale a {bets} apostas simples)."),
                Play(test, (_, k) => RandomGames(spec, k, group, 1, size))));
            rows.Add((Tally(group, $"simples-{size}", $"{bets} jogos simples", $"{bets} apostas simples sorteadas, sem repetir jogo.", baseline: true),
                Play(test, (_, k) => RandomGames(spec, k, group + "-simples", bets, b))));
        }
        if (spec.HasWheel)
        {
            var games = wheel.Count;
            groups.Add(new { code = "fechamento", title = $"Fechamento: quadra garantida × {games} simples", description = $"Mesmo orçamento de {games} apostas ({Money(games * spec.BetPrice)}): o fechamento do app com {options.WheelNumbers} dezenas sorteadas contra {games} jogos soltos." });
            rows.Add((Tally("fechamento", "fechamento", $"Fechamento {options.WheelNumbers} dezenas", $"Fechamento \"quadra se sair 4\" do app ({games} jogos) sobre {options.WheelNumbers} dezenas sorteadas."),
                Play(test, (_, k) => WheelGames(wheel, k, options.WheelNumbers))));
            rows.Add((Tally("fechamento", "simples-fechamento", $"{games} jogos simples", $"{games} apostas simples sorteadas.", baseline: true),
                Play(test, (_, k) => RandomGames(spec, k, "fechamento-simples", games, b))));
        }

        foreach (var (tally, games) in rows)
            for (var i = 0; i < test.Count; i++) Add(spec, tally, games[i], draws[test[i]]);
        var tallies = rows.Select(x => x.Tally).ToList();
        var tiers = spec.Tiers.Select((t, i) => new LabTier(t.ToString(), spec.TierLabels[i], true)).ToList();
        return new
        {
            game = spec.Code,
            parameters = new { options.Quantity, options.TestContests, options.TrainContests, betPrice = spec.BetPrice, options.WheelNumbers, wheelGames = wheel.Count },
            training = new
            {
                from = train.Count == 0 ? (int?)null : draws[train[0]].Contest, to = train.Count == 0 ? (int?)null : draws[train[^1]].Contest,
                draws = train.Count, choices
            },
            test = new { from = draws[test[0]].Contest, to = draws[test[^1]].Contest, fromDate = draws[test[0]].Date, toDate = draws[test[^1]].Date, draws = test.Count },
            tiers, mainTier = $"{spec.Odds.MainMinTier}+",
            groups, rows = tallies.Select(x => x.ToResult(tiers)), verdict = LabEngine.Finalize(tallies),
            methodology = "Walk-forward: os jogos de cada concurso do teste usam só concursos anteriores (pelo número do concurso) e depois são conferidos. " +
                "Jogos maiores contam como as apostas simples que contêm. A faixa principal soma as apostas premiadas " +
                $"({spec.TierLabels[0].ToLowerInvariant()} ou melhor); média e variância do acaso são exatas (hipergeométrica, incluindo a sobreposição entre jogos). " +
                "O retorno usa o rateio real de cada concurso; o esperado usa os mesmos rateios com as probabilidades exatas. " +
                "O p-valor contra o aleatório é um teste pareado nos mesmos concursos. Todos com correção de Bonferroni. " +
                "Quando a faixa principal é rara (menos de 10 prêmios esperados), o p contra o acaso conta sorteios premiados com a chance exata de cada um, " +
                "porque um fechamento ou jogo grande entrega várias quadras de uma vez. Nas linhas de estrutura as dezenas são sorteadas, então o veredito depende só da comparação com a referência."
        };

        int[][][] Play(List<int> targets, Func<IReadOnlyList<LabNumberDraw>, int, List<int[]>> strategy) =>
            LabEngine.WalkForward(draws, targets, d => d.Contest, (h, k) => strategy(h, k).ToArray(), Step);

        LabTally Tally(string group, string code, string name, string description, string? parameter = null, bool baseline = false) =>
            new(group, code, name, description, spec.Tiers.Length, baseline) { Parameter = parameter, FormatOnly = group != "selecao" };
    }

    public static void Add(LabNumberGameSpec spec, LabTally tally, IReadOnlyList<int[]> games, LabNumberDraw draw)
    {
        var odds = spec.Odds;
        var hits = new double[spec.Tiers.Length]; var expected = new double[spec.Tiers.Length];
        var masks = games.Select(LabLotteryOdds.Mask).ToList();
        double units = 0;
        foreach (var mask in masks)
        {
            var size = System.Numerics.BitOperations.PopCount(mask);
            var matched = System.Numerics.BitOperations.PopCount(mask & draw.Mask);
            units += odds.BetsIn(size);
            for (var t = 0; t < spec.Tiers.Length; t++)
            {
                hits[t] += odds.TierCount(size, matched, t);
                expected[t] += odds.ExpectedTierCount(size, t);
            }
        }
        decimal returned = 0; var expectedReturn = 0d;
        for (var t = 0; t < spec.Tiers.Length; t++)
        {
            var prize = Prize(spec, draw, t);
            if (prize == 0) tally.HitsWithoutPrizeValue += (int)hits[t];
            returned += (decimal)hits[t] * prize;
            expectedReturn += expected[t] * (double)prize;
        }
        var (mean, variance) = odds.MainMoments(masks);
        var main = spec.Tiers.Select((tier, t) => tier >= odds.MainMinTier ? hits[t] : 0).Sum();
        tally.Add(units, (decimal)units * spec.BetPrice, returned, expectedReturn, hits, expected, main, mean, variance, hits.Any(x => x > 0), odds.PrizeProbability(masks));
    }

    public static decimal Prize(LabNumberGameSpec spec, LabNumberDraw draw, int tierIndex) =>
        draw.Prizes[tierIndex] > 0 ? draw.Prizes[tierIndex] : spec.FallbackPrize?.Invoke(spec.Tiers[tierIndex]) ?? 0m;

    // ---------- Estratégias: só recebem os concursos anteriores e o número do concurso alvo ----------

    /// <summary>Quentes (mais sorteadas na janela) ou frias (menos sorteadas). Empates por sorteio fixo.</summary>
    public static List<int[]> PoolGames(LabNumberGameSpec spec, IReadOnlyList<LabNumberDraw> history, int contest, int quantity, int window, bool hot)
    {
        var size = spec.Odds.Universe; var bet = spec.Odds.BetSize;
        var counts = new int[size + 1];
        for (var i = Math.Max(0, history.Count - window); i < history.Count; i++) foreach (var number in history[i].Numbers) counts[number]++;
        var random = Rng($"{spec.Code}:{(hot ? "quentes" : "frias")}-{window}", contest);
        var jitter = Enumerable.Range(0, size + 1).Select(_ => random.NextDouble()).ToArray();
        var ordered = Enumerable.Range(1, size).OrderBy(x => hot ? -counts[x] : counts[x]).ThenBy(x => jitter[x]).ToList();
        var pool = ordered.Take(spec.HotPool).ToList();
        var games = new List<int[]> { ordered.Take(bet).Order().ToArray() };
        var keys = new HashSet<ulong> { LabLotteryOdds.Mask(games[0]) };
        while (games.Count < quantity)
        {
            var game = pool.OrderBy(_ => random.Next()).Take(bet).Order().ToArray();
            if (keys.Add(LabLotteryOdds.Mask(game))) games.Add(game);
        }
        return games;
    }

    public static List<int[]> RandomGames(LabNumberGameSpec spec, int contest, string stream, int quantity, int size)
    {
        var random = Rng($"{spec.Code}:{stream}:{quantity}x{size}", contest);
        var games = new List<int[]>(quantity);
        var keys = new HashSet<ulong>();
        while (games.Count < quantity)
        {
            var game = Draw(random, spec.Odds.Universe, size);
            if (keys.Add(LabLotteryOdds.Mask(game))) games.Add(game);
        }
        return games;
    }

    /// <summary>O fechamento é montado uma vez sobre 1..v e reaplicado a v dezenas sorteadas em cada concurso.</summary>
    public static List<int[]> WheelGames(IReadOnlyList<int[]> wheel, int contest, int wheelNumbers)
    {
        var chosen = Draw(Rng("megasena:fechamento", contest), 60, wheelNumbers);
        return wheel.Select(game => game.Select(i => chosen[i - 1]).Order().ToArray()).ToList();
    }

    private static int[] Draw(Random random, int universe, int size)
    {
        var numbers = Enumerable.Range(1, universe).ToArray();
        for (var i = 0; i < size; i++) { var j = random.Next(i, universe); (numbers[i], numbers[j]) = (numbers[j], numbers[i]); }
        return numbers.Take(size).Order().ToArray();
    }

    private static Random Rng(string stream, int contest) => new(unchecked((int)PredictionService.LabSeed($"lab:{stream}:{contest}")));

    private static string Money(decimal value) => value.ToString("C", new System.Globalization.CultureInfo("pt-BR"));
}
