namespace LotteryLab.Api.Services;

/// <summary>Uma extração com os números das posições 1 a 10 (null quando a posição não foi importada).</summary>
public sealed record LabExtraction(long Id, DateTime Date, TimeSpan Time, string?[] Numbers)
{
    /// <summary>Os números presentes já convertidos, para as contagens não repetirem o parse a cada alvo.</summary>
    public int[] Values { get; } = Numbers.Where(x => x is not null).Select(x => int.Parse(x!)).ToArray();

    public bool HasPositions(int count) => Numbers.Length >= count && Numbers.Take(count).All(x => x is not null);
}

public sealed record LabBichoKey(DateTime Date, TimeSpan Time);

public sealed record LabBichoOptions(string Bank, string? Time, int TestDays, int TrainDays, int Quantity, decimal BetValue, int PrizeRange);

public static class LabBicho
{
    public static readonly int[] FrequencyWindows = [7, 30, 90, 180, 365];
    public const int DelayLookbackDays = 365;
    // A previsão do app custa ~30 ms por extração; 400 mantém "todos os horários" perto de 1 minuto no Render.
    public const int MaxTestDraws = 400;
    public const int MaxQuantity = 100;
    /// <summary>Histórico carregado antes do treino: cobre a maior janela de frequência e o atraso de 365 dias.</summary>
    public const int HistoryMarginDays = 370;
    public static readonly LabTier[] Tiers =
        [new("grupo", "Grupo", false), new("dezena", "Dezena", true), new("centena", "Centena", true), new("milhar", "Milhar", true)];
    private static readonly int[] Sizes = [25, 100, 1_000, 10_000];
    // Cotações da PredictionService: dezena 90×, centena 900×, milhar 9000×, divididas pela faixa de prêmio.
    private static readonly decimal[] Multipliers = [0m, 90m, 900m, 9_000m];
    private const int MainTier = 1;
    private const string Group = "selecao";

    public static object Run(IReadOnlyList<LabExtraction> extractions, LabBichoOptions options, Action<int, int>? progress = null)
    {
        var p = options.PrizeRange;
        var time = options.Time is null ? (TimeSpan?)null : TimeOnly.Parse(options.Time).ToTimeSpan();
        var eligible = Enumerable.Range(0, extractions.Count)
            .Where(i => (time is null || extractions[i].Time == time) && extractions[i].HasPositions(p)).ToList();
        if (eligible.Count == 0) throw new ArgumentException("Não há extrações completas para a banca e o horário escolhidos.");
        var lastDate = extractions[eligible[^1]].Date.Date;
        var testStart = lastDate.AddDays(1 - options.TestDays);
        var test = eligible.Where(i => extractions[i].Date >= testStart).TakeLast(MaxTestDraws).ToList();
        // Com o limite de extrações, o treino encosta no início real do teste, sem deixar meses sem uso no meio.
        if (test.Count > 0) testStart = extractions[test[0]].Date.Date;
        var trainStart = testStart.AddDays(-options.TrainDays);
        var train = eligible.Where(i => extractions[i].Date >= trainStart && extractions[i].Date < testStart).ToList();
        if (test.Count < 10) throw new ArgumentException("O período de teste tem menos de 10 extrações completas. Aumente o período.");

        var strategies = 5;
        var total = train.Count * FrequencyWindows.Length + test.Count * strategies;
        var done = 0;
        void Step() => progress?.Invoke(Interlocked.Increment(ref done), total);

        // Treino: a janela de "mais frequentes" é escolhida antes e fora do período de teste.
        var candidates = FrequencyWindows.Select(window =>
        {
            var picks = LabEngine.WalkForward(extractions, train, KeyOf, (history, key) => MostFrequent(history, key, options.Quantity, window), Step);
            var hits = train.Select((index, i) => Evaluate(picks[i], extractions[index], options).TierHits[MainTier]).Sum();
            return new { value = window, score = hits };
        }).ToList();
        var bestWindow = train.Count == 0 ? 90 : candidates.OrderByDescending(x => x.score).ThenByDescending(x => x.value).First().value;

        var rows = new List<(LabTally Tally, string[][] Picks)>
        {
            (new LabTally(Group, "app", "Previsão do app (V3)",
                "Mesma pontuação, seleção e semente da geração automática, com a janela recomendada do horário.", Tiers.Length),
                Play(test, (h, k) => AppPicks(h, k, options))),
            (new LabTally(Group, "frequentes", "Mais frequentes",
                "Dezenas mais sorteadas na janela; em cada uma, a centena e a milhar mais sorteadas.", Tiers.Length) { Parameter = $"janela de {bestWindow} dias ({(train.Count == 0 ? "padrão, sem treino" : "treino")})" },
                Play(test, (h, k) => MostFrequent(h, k, options.Quantity, bestWindow))),
            (new LabTally(Group, "atrasados", "Mais atrasados",
                $"Dezenas há mais tempo sem sair (últimos {DelayLookbackDays} dias); em cada uma, a centena e a milhar mais atrasadas.", Tiers.Length),
                Play(test, (h, k) => MostDelayed(h, k, options.Quantity))),
            (new LabTally(Group, "ultimo", "Repetir último",
                "Repete as milhares da extração imediatamente anterior (completa com sorteio se faltar).", Tiers.Length),
                Play(test, (h, k) => RepeatLast(h, k, options))),
            (new LabTally(Group, "aleatorio", "Aleatório", "Milhares sorteadas com semente fixa, dezenas sem repetição.", Tiers.Length, isBaseline: true),
                Play(test, (h, k) => RandomPicks(k, options))),
        };

        foreach (var (tally, picks) in rows)
            for (var i = 0; i < test.Count; i++)
            {
                var e = Evaluate(picks[i], extractions[test[i]], options);
                tally.Add(e.Units, e.Cost, e.Return, e.ExpectedReturn, e.TierHits, e.TierExpected,
                    e.TierHits[MainTier], e.TierExpected[MainTier], e.MainVariance, e.Prize, e.PrizeProbability);
            }
        var tallies = rows.Select(x => x.Tally).ToList();
        var verdict = LabEngine.Finalize(tallies);
        return new
        {
            game = "bicho", bank = options.Bank, time = options.Time ?? "TODOS",
            parameters = new
            {
                options.Quantity, options.BetValue, options.PrizeRange, options.TestDays, options.TrainDays,
                payouts = new { dezena = Payout(1, options), centena = Payout(2, options), milhar = Payout(3, options) },
                costPerDraw = options.Quantity * 3 * options.BetValue
            },
            training = new
            {
                from = train.Count == 0 ? (DateTime?)null : extractions[train[0]].Date, to = train.Count == 0 ? (DateTime?)null : extractions[train[^1]].Date,
                draws = train.Count,
                choices = new[] { new { strategy = "Mais frequentes", parameter = "janela (dias)", chosen = bestWindow, metric = "acertos de dezena", candidates } }
            },
            test = new { from = extractions[test[0]].Date, to = extractions[test[^1]].Date, draws = test.Count, capped = test.Count == MaxTestDraws },
            tiers = Tiers, mainTier = "dezena",
            groups = new[] { new { code = Group, title = "Seleção dos números", description = $"Todas as estratégias jogam {options.Quantity} milhares por extração, com o mesmo valor em dezena, centena e milhar." } },
            rows = tallies.Select(x => x.ToResult(Tiers)),
            verdict,
            methodology = "Walk-forward: cada extração do teste é prevista só com extrações de data/horário anteriores e depois conferida. " +
                "Cada número aposta o mesmo valor em dezena, centena e milhar, com as cotações da tela de previsões (90×, 900× e 9000×, divididas pela faixa de prêmio). " +
                "Grupo é só contagem: o app não define cotação de grupo. O p-valor teórico compara os acertos de dezena com a média e a variância exatas do acaso; " +
                "o p-valor contra o aleatório é um teste pareado nas mesmas extrações. Os dois usam correção de Bonferroni."
        };

        string[][] Play(List<int> targets, Func<IReadOnlyList<LabExtraction>, LabBichoKey, List<string>> strategy) =>
            LabEngine.WalkForward(extractions, targets, KeyOf, (h, k) => strategy(h, k).ToArray(), Step);
    }

    public static LabBichoKey KeyOf(LabExtraction extraction) => new(extraction.Date.Date, extraction.Time);

    public sealed record Evaluation(double Units, decimal Cost, decimal Return, double ExpectedReturn, double[] TierHits,
        double[] TierExpected, double MainVariance, bool Prize, double PrizeProbability);

    public static Evaluation Evaluate(IReadOnlyList<string> picks, LabExtraction target, LabBichoOptions options)
    {
        var p = options.PrizeRange;
        var actual = target.Numbers.Take(p).Select(x => int.Parse(x!)).ToArray();
        var values = picks.Select(int.Parse).ToArray();
        var hits = new double[Tiers.Length]; var expected = new double[Tiers.Length];
        for (var t = 0; t < Tiers.Length; t++)
        {
            foreach (var pick in values) foreach (var number in actual) if (KeyOf(t, pick) == KeyOf(t, number)) hits[t]++;
            expected[t] = (double)values.Length * p / Sizes[t];
        }
        // Por posição, os acertos de dezena são quantos números apostados têm a dezena sorteada: E[Y²] = Σ m²/100.
        var squares = values.GroupBy(x => x % 100).Sum(g => (double)g.Count() * g.Count()) / 100;
        var mean = values.Length / 100d;
        decimal returned = 0; var expectedReturn = 0d;
        for (var t = 1; t < Tiers.Length; t++) { returned += (decimal)hits[t] * Payout(t, options); expectedReturn += expected[t] * (double)Payout(t, options); }
        return new Evaluation(values.Length * p, values.Length * 3 * options.BetValue, returned, expectedReturn, hits, expected,
            p * (squares - mean * mean), hits[1] + hits[2] + hits[3] > 0,
            // Centena e milhar terminam na dezena: há prêmio se alguma posição trouxer uma das dezenas apostadas.
            1 - Math.Pow(1 - values.Select(x => x % 100).Distinct().Count() / 100d, p));
    }

    public static decimal Payout(int tier, LabBichoOptions options) => Math.Round(options.BetValue * Multipliers[tier] / options.PrizeRange, 2);

    private static int KeyOf(int tier, int number) => tier == 0 ? GroupOf(number) : number % Sizes[tier];
    private static int GroupOf(int number) { var d = number % 100; return d == 0 ? 25 : (d + 3) / 4; }

    // ---------- Estratégias: só recebem o histórico anterior e a data/horário do alvo ----------

    public static List<string> AppPicks(IReadOnlyList<LabExtraction> history, LabBichoKey key, LabBichoOptions options)
    {
        var from = FirstOnOrAfter(history, key.Date.AddDays(-PredictionService.LabWindowDays(TimeOnly.FromTimeSpan(key.Time))));
        var rows = new List<(long, DateTime, TimeSpan, int, string)>();
        for (var i = from; i < history.Count; i++)
            for (var position = 0; position < history[i].Numbers.Length; position++)
                if (history[i].Numbers[position] is { } number) rows.Add((history[i].Id, history[i].Date, history[i].Time, position + 1, number));
        var picks = PredictionService.LabMilhares(rows, options.Bank, DateOnly.FromDateTime(key.Date), TimeOnly.FromTimeSpan(key.Time), options.Quantity);
        return Fill(picks, options.Quantity, Rng("app-fill", key, options.Quantity));
    }

    public static List<string> MostFrequent(IReadOnlyList<LabExtraction> history, LabBichoKey key, int quantity, int windowDays)
    {
        var counts = new[] { new double[100], new double[1_000], new double[10_000] };
        for (var i = FirstOnOrAfter(history, key.Date.AddDays(-windowDays)); i < history.Count; i++)
            foreach (var v in history[i].Values)
                { counts[0][v % 100]++; counts[1][v % 1_000]++; counts[2][v]++; }
        return Hierarchical(counts, quantity, Rng($"frequentes-{windowDays}", key, quantity));
    }

    public static List<string> MostDelayed(IReadOnlyList<LabExtraction> history, LabBichoKey key, int quantity)
    {
        // Score = -(último índice em que saiu); quem não saiu na janela fica com o maior score.
        var scores = new[] { new double[100], new double[1_000], new double[10_000] };
        var index = 0d;
        for (var i = FirstOnOrAfter(history, key.Date.AddDays(-DelayLookbackDays)); i < history.Count; i++)
            foreach (var v in history[i].Values)
                { index++; scores[0][v % 100] = -index; scores[1][v % 1_000] = -index; scores[2][v] = -index; }
        foreach (var level in scores) for (var v = 0; v < level.Length; v++) if (level[v] == 0) level[v] = 1;
        return Hierarchical(scores, quantity, Rng("atrasados", key, quantity));
    }

    public static List<string> RepeatLast(IReadOnlyList<LabExtraction> history, LabBichoKey key, LabBichoOptions options)
    {
        var last = history.Count == 0 ? [] : history[^1].Numbers.Where(x => x is not null).Select(x => x!).ToList();
        var picks = last.GroupBy(x => x[^2..]).Select(g => g.First()).Take(options.Quantity).ToList();
        return Fill(picks, options.Quantity, Rng("ultimo-fill", key, options.Quantity));
    }

    public static List<string> RandomPicks(LabBichoKey key, LabBichoOptions options) =>
        Fill([], options.Quantity, Rng("aleatorio", key, options.Quantity));

    /// <summary>As melhores dezenas pelo score; em cada uma, a melhor centena e, nela, a melhor milhar. Empates por sorteio fixo.</summary>
    private static List<string> Hierarchical(double[][] scores, int quantity, Random random)
    {
        var jitter = Enumerable.Range(0, 10_000).Select(_ => random.NextDouble()).ToArray();
        int Best(IEnumerable<int> options, double[] score) => options.OrderByDescending(x => score[x]).ThenBy(x => jitter[x]).First();
        return Enumerable.Range(0, 100).OrderByDescending(d => scores[0][d]).ThenBy(d => jitter[d]).Take(quantity).Select(d =>
        {
            var c = Best(Enumerable.Range(0, 10).Select(k => k * 100 + d), scores[1]);
            return Best(Enumerable.Range(0, 10).Select(k => k * 1_000 + c), scores[2]).ToString("0000");
        }).ToList();
    }

    /// <summary>Completa até a quantidade com milhares sorteadas de dezenas ainda não usadas.</summary>
    private static List<string> Fill(List<string> picks, int quantity, Random random)
    {
        var result = picks.Take(quantity).ToList();
        var used = result.Select(x => int.Parse(x[^2..])).ToHashSet();
        foreach (var d in Enumerable.Range(0, 100).OrderBy(_ => random.Next()))
        {
            if (result.Count >= quantity) break;
            if (used.Add(d)) result.Add((random.Next(100) * 100 + d).ToString("0000"));
        }
        return result;
    }

    private static Random Rng(string strategy, LabBichoKey key, int quantity) =>
        new(unchecked((int)PredictionService.LabSeed($"lab:{strategy}:{key.Date:yyyy-MM-dd}:{key.Time}:{quantity}")));

    private static int FirstOnOrAfter(IReadOnlyList<LabExtraction> history, DateTime date)
    {
        int low = 0, high = history.Count;
        while (low < high) { var mid = (low + high) / 2; if (history[mid].Date < date) low = mid + 1; else high = mid; }
        return low;
    }
}
