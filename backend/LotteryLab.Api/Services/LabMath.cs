namespace LotteryLab.Api.Services;

/// <summary>Estatística do Laboratório: p-valores por aproximação normal e correção de Bonferroni.</summary>
public static class LabMath
{
    public const double Alpha = 0.05;

    public static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        var result = 1d;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }

    /// <summary>Função erro complementar (Numerical Recipes, erro relativo abaixo de 1,2e-7).</summary>
    public static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1 / (1 + 0.5 * z);
        var r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
            t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
            t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    public static double TwoSidedP(double z) => Math.Clamp(Erfc(Math.Abs(z) / Math.Sqrt(2)), 0, 1);

    /// <summary>Teste z bilateral de um total observado contra média e variância exatas do acaso.</summary>
    public static double PValue(double observed, double expected, double variance)
    {
        if (variance <= 0) return Math.Abs(observed - expected) < 1e-9 ? 1 : 0;
        return TwoSidedP((observed - expected) / Math.Sqrt(variance));
    }

    /// <summary>Teste z pareado: as duas estratégias são conferidas nos mesmos sorteios.</summary>
    public static double PairedPValue(IReadOnlyList<double> differences)
    {
        if (differences.Count < 2) return 1;
        var mean = differences.Average();
        var variance = differences.Sum(x => (x - mean) * (x - mean)) / (differences.Count - 1);
        if (variance <= 0) return Math.Abs(mean) < 1e-12 ? 1 : 0;
        return TwoSidedP(mean / Math.Sqrt(variance / differences.Count));
    }

    public static double Bonferroni(double p, int comparisons) => Math.Min(1, p * Math.Max(1, comparisons));

    /// <summary>
    /// Teste exato bilateral de "sorteios com prêmio": soma de Bernoullis com probabilidades diferentes por sorteio.
    /// Prêmios raros e agrupados (um fechamento dá 2–3 quadras de uma vez) quebram a aproximação normal; contar sorteios não.
    /// </summary>
    public static double PoissonBinomialPValue(int observed, IReadOnlyList<double> probabilities)
    {
        var pmf = new double[probabilities.Count + 1];
        pmf[0] = 1;
        for (var i = 0; i < probabilities.Count; i++)
        {
            var q = Math.Clamp(probabilities[i], 0, 1);
            for (var k = i + 1; k >= 1; k--) pmf[k] = pmf[k] * (1 - q) + pmf[k - 1] * q;
            pmf[0] *= 1 - q;
        }
        double lower = 0, upper = 0;
        for (var k = 0; k < pmf.Length; k++) { if (k <= observed) lower += pmf[k]; if (k >= observed) upper += pmf[k]; }
        return Math.Clamp(2 * Math.Min(lower, upper), 0, 1);
    }
}

/// <summary>
/// Probabilidades exatas de uma loteria "K dezenas sorteadas entre U" com aposta simples de B dezenas.
/// Um jogo de n dezenas vale C(n,B) apostas simples; com h acertos, as de exatamente t acertos são C(h,t)·C(n−h,B−t).
/// </summary>
public sealed class LabLotteryOdds
{
    public int Universe { get; }
    public int Drawn { get; }
    public int BetSize { get; }
    public int[] Tiers { get; }
    public int MainMinTier { get; }
    private readonly double totalDraws;
    public const int MaxGameSize = 20;
    // Vetor em vez de dicionário: com centenas de jogos por sorteio a variância consulta milhões de pares.
    private readonly double[,,] pairMoments = new double[MaxGameSize + 1, MaxGameSize + 1, MaxGameSize + 1];
    // Tabelas pequenas (até 20 dezenas por jogo) calculadas uma vez: a conferência consulta milhares de jogos por concurso.
    private readonly long[,,] tierCounts;
    private readonly double[,] expectedTiers;
    private readonly double[,] mainCounts;
    private readonly double[] expectedMain;

    public LabLotteryOdds(int universe, int drawn, int betSize, int[] tiers, int mainMinTier)
    {
        Universe = universe; Drawn = drawn; BetSize = betSize; Tiers = tiers; MainMinTier = mainMinTier;
        totalDraws = LabMath.Binomial(universe, drawn);
        for (var a = 0; a <= MaxGameSize; a++)
        for (var b = 0; b <= MaxGameSize; b++)
        for (var s = 0; s <= MaxGameSize; s++) pairMoments[a, b, s] = double.NaN;
        tierCounts = new long[MaxGameSize + 1, MaxGameSize + 1, tiers.Length];
        expectedTiers = new double[MaxGameSize + 1, tiers.Length];
        mainCounts = new double[MaxGameSize + 1, MaxGameSize + 1];
        expectedMain = new double[MaxGameSize + 1];
        for (var size = 0; size <= MaxGameSize; size++)
        for (var t = 0; t < tiers.Length; t++)
        {
            for (var h = 0; h <= size; h++)
            {
                tierCounts[size, h, t] = MegaSenaService.Combinations(h, tiers[t]) * MegaSenaService.Combinations(size - h, betSize - tiers[t]);
                if (tiers[t] >= mainMinTier) mainCounts[size, h] += tierCounts[size, h, t];
                if (h <= drawn) expectedTiers[size, t] += HitsProbability(size, h) * tierCounts[size, h, t];
            }
            if (tiers[t] >= mainMinTier) expectedMain[size] += expectedTiers[size, t];
        }
    }

    public long BetsIn(int gameSize) => MegaSenaService.Combinations(gameSize, BetSize);

    /// <summary>Apostas simples com exatamente a faixa de índice <paramref name="tierIndex"/> num jogo com <paramref name="hits"/> acertos.</summary>
    public long TierCount(int gameSize, int hits, int tierIndex) => tierCounts[gameSize, hits, tierIndex];

    public double HitsProbability(int gameSize, int hits) =>
        LabMath.Binomial(gameSize, hits) * LabMath.Binomial(Universe - gameSize, Drawn - hits) / totalDraws;

    public double ExpectedTierCount(int gameSize, int tierIndex) => expectedTiers[gameSize, tierIndex];

    /// <summary>Apostas simples premiadas na faixa principal (a menor faixa paga e as acima dela).</summary>
    public double MainCount(int gameSize, int hits) => mainCounts[gameSize, hits];

    public double ExpectedMain(int gameSize) => expectedMain[gameSize];

    /// <summary>E[M_a·M_b] para dois jogos com a e b dezenas que compartilham s dezenas (hipergeométrica multivariada).</summary>
    public double PairMoment(int a, int b, int shared)
    {
        if (a > b) (a, b) = (b, a);
        var cached = pairMoments[a, b, shared];
        if (!double.IsNaN(cached)) return cached;
        var onlyA = a - shared; var onlyB = b - shared; var neither = Universe - a - b + shared;
        var sum = 0d;
        for (var k1 = 0; k1 <= Math.Min(shared, Drawn); k1++)
        for (var k2 = 0; k2 <= Math.Min(onlyA, Drawn - k1); k2++)
        for (var k3 = 0; k3 <= Math.Min(onlyB, Drawn - k1 - k2); k3++)
        {
            var k4 = Drawn - k1 - k2 - k3;
            if (k4 > neither) continue;
            var probability = LabMath.Binomial(shared, k1) * LabMath.Binomial(onlyA, k2) * LabMath.Binomial(onlyB, k3)
                * LabMath.Binomial(neither, k4) / totalDraws;
            sum += probability * MainCount(a, k1 + k2) * MainCount(b, k1 + k3);
        }
        return pairMoments[a, b, shared] = sum;
    }

    /// <summary>Média e variância exatas da faixa principal somada sobre um conjunto de jogos num sorteio uniforme.</summary>
    public (double Mean, double Variance) MainMoments(IReadOnlyList<ulong> games)
    {
        var sizes = games.Select(g => System.Numerics.BitOperations.PopCount(g)).ToArray();
        var mean = sizes.Sum(ExpectedMain);
        // Conta os pares por (tamanho, tamanho, dezenas em comum) e só depois multiplica pelo momento de cada tipo.
        var pairs = new long[MaxGameSize + 1, MaxGameSize + 1, MaxGameSize + 1];
        for (var i = 0; i < games.Count; i++)
        {
            pairs[sizes[i], sizes[i], sizes[i]]++;
            for (var j = i + 1; j < games.Count; j++)
                pairs[sizes[i], sizes[j], System.Numerics.BitOperations.PopCount(games[i] & games[j])] += 2;
        }
        var second = 0d;
        foreach (var a in sizes.Distinct())
        foreach (var b in sizes.Distinct())
        for (var s = 0; s <= Math.Min(a, b); s++)
            if (pairs[a, b, s] > 0) second += pairs[a, b, s] * PairMoment(a, b, s);
        return (mean, Math.Max(0, second - mean * mean));
    }

    /// <summary>
    /// Chance de pelo menos uma aposta premiada (faixa principal) num sorteio uniforme. Exata quando as dezenas
    /// usadas somam até <see cref="ExactUnionLimit"/> (jogo grande, fechamento); senão supõe jogos independentes.
    /// </summary>
    public double PrizeProbability(IReadOnlyList<ulong> games)
    {
        var union = games.Aggregate(0UL, (a, g) => a | g);
        var u = System.Numerics.BitOperations.PopCount(union);
        if (u > ExactUnionLimit)
            return 1 - games.Aggregate(1d, (miss, g) => miss * (1 - SinglePrizeProbability(System.Numerics.BitOperations.PopCount(g))));
        var bits = Enumerable.Range(0, 64).Where(i => (union & 1UL << i) != 0).ToArray();
        // Fechamentos e jogos grandes repetem a mesma forma a cada concurso, só com outras dezenas: guarda pela forma.
        var shape = string.Join(',', games.Select(g => Enumerable.Range(0, u).Where(i => (g & 1UL << bits[i]) != 0).Sum(i => 1L << i)).Order());
        if (prizeProbabilities.TryGetValue(shape, out var cached)) return cached;
        var probability = 0d;
        // Só importa quais dezenas do conjunto saíram: cada subconjunto S com |S| = k tem chance C(U−u, K−k)/C(U,K).
        for (var subset = 0; subset < 1 << u; subset++)
        {
            var k = System.Numerics.BitOperations.PopCount((uint)subset);
            if (k > Drawn) continue;
            var drawn = 0UL;
            for (var i = 0; i < u; i++) if ((subset & 1 << i) != 0) drawn |= 1UL << bits[i];
            if (games.Any(g => MainCount(System.Numerics.BitOperations.PopCount(g), System.Numerics.BitOperations.PopCount(g & drawn)) > 0))
                probability += LabMath.Binomial(Universe - u, Drawn - k) / totalDraws;
        }
        return prizeProbabilities[shape] = probability;
    }

    public const int ExactUnionLimit = 12;
    private readonly Dictionary<string, double> prizeProbabilities = [];

    private double SinglePrizeProbability(int gameSize)
    {
        var sum = 0d;
        for (var h = 0; h <= Math.Min(gameSize, Drawn); h++) if (MainCount(gameSize, h) > 0) sum += HitsProbability(gameSize, h);
        return sum;
    }

    public static ulong Mask(IEnumerable<int> numbers) => numbers.Aggregate(0UL, (mask, n) => mask | 1UL << n);
}
