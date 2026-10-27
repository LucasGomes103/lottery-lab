using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class LabTests
{
    private static readonly string[] Times = ["02:00", "08:00", "10:00", "12:00", "15:00", "17:00", "21:00", "23:00"];

    private static List<LabExtraction> SyntheticBicho(int days, int seed = 7)
    {
        var random = new Random(seed);
        var start = new DateTime(2025, 1, 1);
        var id = 0L;
        return Enumerable.Range(0, days).SelectMany(day => Times.Select(time => new LabExtraction(++id, start.AddDays(day),
            TimeSpan.Parse(time), Enumerable.Range(0, 10).Select(_ => (string?)random.Next(10_000).ToString("0000")).ToArray()))).ToList();
    }

    private static List<LabNumberDraw> SyntheticLottery(LabNumberGameSpec spec, int count, int seed = 11)
    {
        var random = new Random(seed);
        return Enumerable.Range(1, count).Select(contest => new LabNumberDraw(contest, new DateTime(2020, 1, 1).AddDays(contest * 3),
            Enumerable.Range(1, spec.Odds.Universe).OrderBy(_ => random.Next()).Take(spec.Odds.Drawn).Order().ToArray(),
            spec.Tiers.Select(t => 10m * t).ToArray())).ToList();
    }

    // ---------- Sem olhar o futuro ----------

    [Fact]
    public void WalkForwardOnlyShowsDrawsBeforeTheTarget()
    {
        var draws = Enumerable.Range(0, 50).ToList();
        var targets = new[] { 10, 20, 49 };
        var seen = LabEngine.WalkForward(draws, targets, d => d, (history, key) =>
        {
            Assert.Equal(key, history.Count);
            Assert.DoesNotContain(key, history);
            Assert.True(history.All(x => x < key));
            Assert.Throws<ArgumentOutOfRangeException>(() => history[key]);
            return history.Count;
        });
        Assert.Equal(targets, seen);
    }

    [Fact]
    public void BichoStrategiesIgnoreTheTargetAndLaterDraws()
    {
        var original = SyntheticBicho(420);
        var target = original.Count - 9;
        // Mesmo histórico, mas o alvo e tudo depois dele trocados: as apostas não podem mudar.
        var tampered = original.Take(target).Concat(SyntheticBicho(420, seed: 99).Skip(target)).ToList();
        var options = new LabBichoOptions("LT NACIONAL", "21:00", 30, 30, 10, 1m, 5);
        Func<IReadOnlyList<LabExtraction>, LabBichoKey, List<string>>[] strategies =
        [
            (h, k) => LabBicho.AppPicks(h, k, options), (h, k) => LabBicho.MostFrequent(h, k, 10, 90),
            (h, k) => LabBicho.MostDelayed(h, k, 10), (h, k) => LabBicho.RepeatLast(h, k, options), (_, k) => LabBicho.RandomPicks(k, options)
        ];
        foreach (var strategy in strategies)
        {
            var a = LabEngine.WalkForward(original, [target], LabBicho.KeyOf, strategy)[0];
            var b = LabEngine.WalkForward(tampered, [target], LabBicho.KeyOf, strategy)[0];
            Assert.Equal(a, b);
            Assert.Equal(10, a.Count);
            Assert.Equal(10, a.Select(x => x[^2..]).Distinct().Count());
        }
    }

    [Fact]
    public void BichoAppStrategyMatchesThePredictionScoring()
    {
        var history = SyntheticBicho(300);
        var key = new LabBichoKey(history[^1].Date.AddDays(1), TimeSpan.Parse("21:00"));
        var rows = history.SelectMany(e => e.Numbers.Select((n, i) => (e.Id, e.Date, e.Time, i + 1, n!)));
        var direct = PredictionService.LabMilhares(rows, "LT NACIONAL", DateOnly.FromDateTime(key.Date), TimeOnly.Parse("21:00"), 10);
        Assert.Equal(direct, LabBicho.AppPicks(history, key, new LabBichoOptions("LT NACIONAL", "21:00", 30, 30, 10, 1m, 5)));
    }

    [Theory]
    [InlineData("megasena")]
    [InlineData("lotofacil")]
    public void LotteryStrategiesIgnoreTheTargetAndLaterDraws(string game)
    {
        var spec = game == "megasena" ? LabNumberGame.MegaSena : LabNumberGame.Lotofacil;
        var original = SyntheticLottery(spec, 200);
        var tampered = original.Take(150).Concat(SyntheticLottery(spec, 200, seed: 5).Skip(150)).ToList();
        Func<IReadOnlyList<LabNumberDraw>, int, List<int[]>>[] strategies =
        [
            (h, k) => spec.AppGames(h.Select(x => x.Numbers).ToList(), 5, k),
            (h, k) => LabNumberGame.PoolGames(spec, h, k, 5, 50, true), (h, k) => LabNumberGame.PoolGames(spec, h, k, 5, 50, false)
        ];
        foreach (var strategy in strategies)
        {
            var a = LabEngine.WalkForward(original, [150], d => d.Contest, strategy)[0];
            var b = LabEngine.WalkForward(tampered, [150], d => d.Contest, strategy)[0];
            Assert.Equal(a.Select(x => string.Join(',', x)), b.Select(x => string.Join(',', x)));
        }
    }

    // ---------- Matemática das métricas ----------

    [Fact]
    public void LotteryOddsMatchKnownProbabilities()
    {
        var mega = LabNumberGame.MegaSena.Odds;
        Assert.Equal(1, Enumerable.Range(0, 7).Sum(h => mega.HitsProbability(6, h)), 10);
        Assert.Equal(15d * 1431 / 50_063_860, mega.ExpectedTierCount(6, 0), 12);
        Assert.Equal(1d / 50_063_860, mega.ExpectedTierCount(6, 2), 15);
        // Linearidade: um jogo de 7 dezenas vale 7 apostas simples, então espera 7× cada faixa.
        for (var t = 0; t < mega.Tiers.Length; t++) Assert.Equal(7 * mega.ExpectedTierCount(6, t), mega.ExpectedTierCount(7, t), 12);

        var loto = LabNumberGame.Lotofacil.Odds;
        Assert.Equal(1365d * 210 / 3_268_760, loto.ExpectedTierCount(15, 0), 12);
        Assert.Equal(16 * loto.ExpectedMain(15), loto.ExpectedMain(16), 10);
    }

    [Fact]
    public void MainVarianceMatchesFullEnumeration()
    {
        // Lotofácil tem só 3.268.760 sorteios possíveis: dá para conferir média e variância exatas por força bruta.
        var odds = LabNumberGame.Lotofacil.Odds;
        int[][] games = [Enumerable.Range(1, 15).ToArray(), Enumerable.Range(6, 15).ToArray(), Enumerable.Range(3, 16).ToArray()];
        var masks = games.Select(LabLotteryOdds.Mask).ToList();
        double sum = 0, squares = 0, total = 0;
        foreach (var drawn in Subsets(25, 15))
        {
            var main = masks.Sum(m => odds.MainCount(System.Numerics.BitOperations.PopCount(m), System.Numerics.BitOperations.PopCount(m & drawn)));
            sum += main; squares += main * main; total++;
        }
        var (mean, variance) = odds.MainMoments(masks);
        Assert.Equal(sum / total, mean, 9);
        Assert.Equal(squares / total - sum / total * (sum / total), variance, 7);
    }

    private static IEnumerable<ulong> Subsets(int n, int k)
    {
        var index = Enumerable.Range(1, k).ToArray();
        while (true)
        {
            yield return LabLotteryOdds.Mask(index);
            var p = k - 1;
            while (p >= 0 && index[p] == n - k + p + 1) p--;
            if (p < 0) yield break;
            index[p]++;
            for (var j = p + 1; j < k; j++) index[j] = index[j - 1] + 1;
        }
    }

    [Fact]
    public void RandomBichoReturnMatchesTheTheoreticalValue()
    {
        var options = new LabBichoOptions("LT NACIONAL", null, 30, 0, 10, 1m, 5);
        var draws = SyntheticBicho(400, seed: 3);
        var tally = new LabTally("g", "aleatorio", "Aleatório", "", LabBicho.Tiers.Length, isBaseline: true);
        foreach (var draw in draws)
        {
            var e = LabBicho.Evaluate(LabBicho.RandomPicks(LabBicho.KeyOf(draw), options), draw, options);
            tally.Add(e.Units, e.Cost, e.Return, e.ExpectedReturn, e.TierHits, e.TierExpected, e.TierHits[1], e.TierExpected[1], e.MainVariance, e.Prize);
        }
        // Cotações 90×/900×/9000× divididas por 5 prêmios: o acaso devolve exatamente 90% do apostado.
        Assert.Equal(0.9, tally.ExpectedReturn / (double)tally.Cost, 12);
        Assert.Equal(tally.MainExpected, draws.Count * 10 * 5 / 100d, 9);
        // Com 3.200 sorteios, a dezena do aleatório fica dentro de 4 desvios da média exata.
        Assert.InRange((tally.MainHits - tally.MainExpected) / Math.Sqrt(tally.MainVariance), -4, 4);
    }

    [Fact]
    public void RandomLotteryHitsMatchTheTheoreticalValue()
    {
        var spec = LabNumberGame.Lotofacil;
        var draws = SyntheticLottery(spec, 600);
        var tally = new LabTally("g", "aleatorio", "Aleatório", "", spec.Tiers.Length, isBaseline: true);
        foreach (var draw in draws) LabNumberGame.Add(spec, tally, LabNumberGame.RandomGames(spec, draw.Contest, "t", 5, 15), draw);
        Assert.Equal(600 * 5 * spec.Odds.ExpectedMain(15), tally.MainExpected, 6);
        Assert.InRange((tally.MainHits - tally.MainExpected) / Math.Sqrt(tally.MainVariance), -4, 4);
        Assert.Equal(600 * 5 * 3.5m, tally.Cost);
    }

    [Fact]
    public void StatisticsHelpers()
    {
        Assert.Equal(1, LabMath.Erfc(0), 6);
        Assert.Equal(0.05, LabMath.TwoSidedP(1.959964), 5);
        Assert.Equal(1, LabMath.PValue(10, 10, 4));
        Assert.Equal(0.3, LabMath.Bonferroni(0.1, 3), 12);
        Assert.Equal(1, LabMath.Bonferroni(0.5, 4));
        Assert.Equal(1, LabMath.PairedPValue([0, 0, 0]));
    }

    [Fact]
    public void VerdictUsesBonferroniAcrossStrategies()
    {
        // Oscilações de ±1 com ritmos diferentes: o teste pareado contra o aleatório também tem variância.
        LabTally Row(string code, bool baseline, double hits) => Tally(code, baseline,
            Enumerable.Range(0, 100).Select(i => hits + (baseline ? (i % 4 < 2 ? 1 : -1) : (i % 2 == 0 ? 1 : -1))).ToArray(), expected: 1, variance: 1);
        // 100 sorteios com média 1 e variância 1: 1,2 por sorteio dá z = 2 (p ≈ 0,0455), que não resiste a 3 comparações.
        var rows = new[] { Row("a", false, 1.2), Row("b", false, 1), Row("c", false, 1), Row("r", true, 1) };
        var verdict = LabEngine.Finalize(rows);
        Assert.Equal(LabEngine.NoEvidence, verdict.Title);
        Assert.True(rows[0].PTheory < 0.05);
        Assert.True(rows[0].PTheoryAdjusted > 0.05);

        var strong = new[] { Row("a", false, 1.5), Row("r", true, 1) };
        Assert.Equal("Diferença significativa", LabEngine.Finalize(strong).Title);
    }

    private static LabTally Tally(string code, bool baseline, double[] perDraw, double expected, double variance)
    {
        var tally = new LabTally("g", code, code, "", 1, baseline);
        foreach (var hits in perDraw) tally.Add(1, 1, 0, 0, [hits], [expected], hits, expected, variance, hits > 0);
        return tally;
    }

    [Fact]
    public void DeleteSkipsRunsStillRunning()
    {
        var done = Guid.NewGuid(); var running = Guid.NewGuid();
        Assert.Equal([done], LabJobService.DeletableIds([done, running, done], new HashSet<Guid> { running }));
    }

    // ---------- Execução completa ----------

    [Fact]
    public void BichoRunProducesAllStrategies()
    {
        var report = LabBicho.Run(SyntheticBicho(600), new LabBichoOptions("LT NACIONAL", "21:00", 60, 60, 10, 1m, 5));
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        foreach (var code in new[] { "app", "frequentes", "atrasados", "ultimo", "aleatorio" }) Assert.Contains($"\"code\":\"{code}\"", json);
        Assert.Contains("verdict", json);
    }

    [Theory]
    [InlineData("megasena")]
    [InlineData("lotofacil")]
    public void LotteryRunProducesAllGroups(string game)
    {
        var spec = game == "megasena" ? LabNumberGame.MegaSena : LabNumberGame.Lotofacil;
        var report = LabNumberGame.Run(spec, SyntheticLottery(spec, 300), new LabNumberOptions(40, 40, 5, 9));
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Contains("\"code\":\"selecao\"", json);
        foreach (var size in spec.BigGameSizes) Assert.Contains($"\"code\":\"estrutura-{size}\"", json);
        Assert.Equal(spec.HasWheel, json.Contains("\"code\":\"fechamento\""));
    }
}
