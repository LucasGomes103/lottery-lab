using System.Collections;

namespace LotteryLab.Api.Services;

/// <summary>Os primeiros <c>count</c> sorteios de uma lista. Qualquer índice a partir do alvo lança exceção.</summary>
public sealed class LabHistory<T>(IReadOnlyList<T> source, int count) : IReadOnlyList<T>
{
    public int Count => count;

    public T this[int index] => (uint)index < (uint)count
        ? source[index]
        : throw new ArgumentOutOfRangeException(nameof(index), "O laboratório só enxerga sorteios anteriores ao alvo.");

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < count; i++) yield return source[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class LabEngine
{
    /// <summary>
    /// Walk-forward sem olhar o futuro: para cada alvo, a estratégia recebe só os sorteios anteriores
    /// (a lista está em ordem cronológica) e a chave do alvo (data/horário ou concurso), nunca o resultado.
    /// As estratégias derivam a semente da chave, então rodar em paralelo não muda as apostas.
    /// </summary>
    public static TBets[] WalkForward<TDraw, TKey, TBets>(IReadOnlyList<TDraw> draws, IReadOnlyList<int> targets,
        Func<TDraw, TKey> keyOf, Func<IReadOnlyList<TDraw>, TKey, TBets> strategy, Action? stepDone = null)
    {
        var bets = new TBets[targets.Count];
        Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
        {
            var index = targets[i];
            bets[i] = strategy(new LabHistory<TDraw>(draws, index), keyOf(draws[index]));
            stepDone?.Invoke();
        });
        return bets;
    }

    /// <summary>Calcula p-valores, Bonferroni e veredito de todas as linhas de um relatório.</summary>
    public static LabVerdict Finalize(IReadOnlyList<LabTally> rows)
    {
        var tested = rows.Where(x => !x.IsBaseline).ToList();
        var comparisons = Math.Max(1, tested.Count);
        foreach (var row in rows)
        {
            row.PTheory = row.UsesPrizeTest
                ? LabMath.PoissonBinomialPValue(row.DrawsWithPrize, row.PrizeProbabilities)
                : LabMath.PValue(row.MainHits, row.MainExpected, row.MainVariance);
            var baseline = rows.FirstOrDefault(x => x.IsBaseline && x.Group == row.Group);
            if (row.IsBaseline || baseline is null) continue;
            row.PTheoryAdjusted = LabMath.Bonferroni(row.PTheory.Value, comparisons);
            var differences = row.MainPerDraw.Zip(baseline.MainPerDraw, (a, b) => a - b).ToList();
            row.PVsBaseline = LabMath.PairedPValue(differences);
            row.PVsBaselineAdjusted = LabMath.Bonferroni(row.PVsBaseline.Value, comparisons);
            row.MainDiffVsBaseline = row.MainHits - baseline.MainHits;
            row.ReturnDiffVsBaseline = row.Return - baseline.Return;
            // Formato com dezenas sorteadas não tem como prever: só conta se diferir da referência nos mesmos sorteios.
            var significant = row.FormatOnly
                ? row.PVsBaselineAdjusted < LabMath.Alpha
                : row.PTheoryAdjusted < LabMath.Alpha || row.PVsBaselineAdjusted < LabMath.Alpha;
            var above = !row.FormatOnly && row.PTheoryAdjusted < LabMath.Alpha
                ? (row.UsesPrizeTest ? row.DrawsWithPrize > row.ExpectedDrawsWithPrize : row.MainHits > row.MainExpected)
                : row.MainDiffVsBaseline > 0;
            row.Significant = significant;
            row.Verdict = !significant ? NoEvidence : above ? "Diferença significativa (acima do acaso)" : "Diferença significativa (abaixo do acaso)";
        }
        foreach (var row in rows.Where(x => x.IsBaseline)) row.Verdict = "Referência (aleatório)";

        var better = tested.Where(x => x.Significant && x.Verdict!.Contains("acima")).ToList();
        var worse = tested.Where(x => x.Significant && x.Verdict!.Contains("abaixo")).ToList();
        if (better.Count > 0)
            return new LabVerdict(true, "Diferença significativa",
                $"{string.Join(", ", better.Select(x => x.Name))} ficou acima do esperado mesmo após a correção de Bonferroni para {comparisons} comparações. " +
                "Com sorteios independentes isso costuma ser acaso ou viés de dados: repita em outro período antes de confiar.", comparisons);
        return new LabVerdict(false, NoEvidence,
            (worse.Count > 0 ? $"{string.Join(", ", worse.Select(x => x.Name))} ficou abaixo do esperado (o que também não indica previsibilidade). " : "") +
            $"Nenhuma estratégia superou o acaso com p ajustado < 0,05 (Bonferroni, {comparisons} comparações).", comparisons);
    }

    public const string NoEvidence = "Sem evidência de vantagem sobre o aleatório";
}

public sealed record LabVerdict(bool Significant, string Title, string Detail, int Comparisons);

public sealed record LabTier(string Code, string Label, bool Paid);

/// <summary>Acumula o desempenho de uma estratégia sorteio a sorteio.</summary>
public sealed class LabTally(string group, string code, string name, string description, int tierCount, bool isBaseline = false)
{
    public string Group { get; } = group;
    public string Code { get; } = code;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsBaseline { get; } = isBaseline;
    public string? Parameter { get; set; }
    public int Draws { get; private set; }
    /// <summary>Oportunidades de acerto: apostas simples (loterias) ou número × prêmio (bicho).</summary>
    public double Units { get; private set; }
    public decimal Cost { get; private set; }
    public decimal Return { get; private set; }
    public double ExpectedReturn { get; private set; }
    public double[] TierHits { get; } = new double[tierCount];
    public double[] TierExpected { get; } = new double[tierCount];
    public double MainHits { get; private set; }
    public double MainExpected { get; private set; }
    public double MainVariance { get; private set; }
    public List<double> MainPerDraw { get; } = [];
    public int DrawsWithPrize { get; private set; }
    /// <summary>Chance exata (ou quase) de ter algum prêmio em cada sorteio, para o teste de sorteios premiados.</summary>
    public List<double> PrizeProbabilities { get; } = [];
    public double ExpectedDrawsWithPrize { get; private set; }
    /// <summary>Linha que só muda o formato da aposta (dezenas sorteadas): o veredito depende da comparação pareada.</summary>
    public bool FormatOnly { get; init; }
    /// <summary>Faixa principal rara: o p contra o acaso usa sorteios premiados, não a aproximação normal.</summary>
    public bool UsesPrizeTest => MainExpected < 10 && Draws > 0 && PrizeProbabilities.Count == Draws;
    public int HitsWithoutPrizeValue { get; set; }
    public double? PTheory { get; set; }
    public double? PTheoryAdjusted { get; set; }
    public double? PVsBaseline { get; set; }
    public double? PVsBaselineAdjusted { get; set; }
    public double? MainDiffVsBaseline { get; set; }
    public decimal? ReturnDiffVsBaseline { get; set; }
    public bool Significant { get; set; }
    public string? Verdict { get; set; }

    public void Add(double units, decimal cost, decimal returned, double expectedReturn, double[] tierHits, double[] tierExpected,
        double mainHits, double mainExpected, double mainVariance, bool prize, double prizeProbability = double.NaN)
    {
        Draws++; Units += units; Cost += cost; Return += returned; ExpectedReturn += expectedReturn;
        for (var t = 0; t < TierHits.Length; t++) { TierHits[t] += tierHits[t]; TierExpected[t] += tierExpected[t]; }
        MainHits += mainHits; MainExpected += mainExpected; MainVariance += mainVariance;
        MainPerDraw.Add(mainHits);
        if (prize) DrawsWithPrize++;
        if (!double.IsNaN(prizeProbability)) { PrizeProbabilities.Add(prizeProbability); ExpectedDrawsWithPrize += prizeProbability; }
    }

    public object ToResult(IReadOnlyList<LabTier> tiers) => new
    {
        group = Group, code = Code, name = Name, description = Description, isBaseline = IsBaseline, parameter = Parameter,
        draws = Draws, unitsPerDraw = Draws == 0 ? 0 : Math.Round(Units / Draws, 2),
        costPerDraw = Draws == 0 ? 0 : Math.Round(Cost / Draws, 2), totalCost = Cost,
        tiers = tiers.Select((tier, t) => new
        {
            code = tier.Code, label = tier.Label, hits = TierHits[t], expected = Math.Round(TierExpected[t], 3),
            rate = Units == 0 ? 0 : TierHits[t] / Units, theoreticalRate = Units == 0 ? 0 : TierExpected[t] / Units
        }),
        mainHits = MainHits, mainExpected = Math.Round(MainExpected, 3),
        mainRate = Units == 0 ? 0 : MainHits / Units, mainTheoreticalRate = Units == 0 ? 0 : MainExpected / Units,
        totalReturn = Return,
        returnPerReal = Cost == 0 ? 0 : Math.Round(Return / Cost, 4),
        expectedReturn = Math.Round(ExpectedReturn, 2),
        expectedReturnPerReal = Cost == 0 ? 0 : Math.Round(ExpectedReturn / (double)Cost, 4),
        drawsWithPrize = DrawsWithPrize, drawsWithPrizeRate = Draws == 0 ? 0 : Math.Round((double)DrawsWithPrize / Draws, 4),
        expectedDrawsWithPrize = PrizeProbabilities.Count == Draws ? Math.Round(ExpectedDrawsWithPrize, 2) : (double?)null,
        theoryTest = UsesPrizeTest ? "premiados" : "normal", formatOnly = FormatOnly,
        hitsWithoutPrizeValue = HitsWithoutPrizeValue,
        diffVsBaseline = MainDiffVsBaseline, returnDiffVsBaseline = ReturnDiffVsBaseline,
        pTheory = Round(PTheory), pTheoryAdjusted = Round(PTheoryAdjusted),
        pVsBaseline = Round(PVsBaseline), pVsBaselineAdjusted = Round(PVsBaselineAdjusted),
        significant = Significant, verdict = Verdict,
        smallSample = MainExpected < 10
    };

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 6);
}
