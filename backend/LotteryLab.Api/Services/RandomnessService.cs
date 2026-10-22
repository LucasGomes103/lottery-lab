using Dapper;
using LotteryLab.Api.Data;

namespace LotteryLab.Api.Services;

// Testes clássicos de aleatoriedade sobre o histórico de uma banca. Se nenhum teste rejeitar a hipótese de
// sorteio uniforme e independente, nenhuma fórmula baseada no histórico pode melhorar a taxa de acerto.
public sealed class RandomnessService(Db db)
{
    private sealed record Row(long ExtractionId, DateTime Date, TimeSpan Time, int Position, string Number);
    private sealed record Extraction(long Id, DateTime Date, TimeSpan Time, List<string> Numbers);
    public sealed record TestResult(string Name, string Kind, double Statistic, double? DegreesOfFreedom, double PValue,
        int Sample, string Description)
    {
        public bool Significant { get; set; }
    }

    public async Task<object> Run(string bank, int prizeRange)
    {
        prizeRange = Math.Clamp(prizeRange, 1, 10);
        await using var connection = db.Open();
        var rows = (await connection.QueryAsync<Row>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,
                     r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and r.position between 1 and @prizeRange
              order by e.extraction_date,e.extraction_time,r.position", new { bank, prizeRange })).ToList();
        var extractions = rows.GroupBy(x => x.ExtractionId).Select(g => new Extraction(g.Key, g.First().Date.Date, g.First().Time,
            g.OrderBy(x => x.Position).Select(x => x.Number.PadLeft(4, '0')[^4..]).ToList()))
            .OrderBy(x => x.Date).ThenBy(x => x.Time).ToList();
        if (extractions.Count < 30)
            return new { bank, prizeRange, extractions = extractions.Count, verdict = "BASE INSUFICIENTE",
                message = "São necessárias ao menos 30 extrações para os testes." };

        var tests = new List<TestResult>();
        var skipped = new List<string>();
        var numbers = extractions.SelectMany(x => x.Numbers).ToList();

        var groupCounts = CountsOf(numbers.Select(GroupOf), 1, 25);
        tests.Add(ChiSquareUniform("Frequência de grupos (1º–" + prizeRange + "º)", groupCounts,
            "Cada um dos 25 grupos deve sair com a mesma frequência."));
        tests.Add(ChiSquareUniform("Frequência de dezenas", CountsOf(numbers.Select(x => int.Parse(x[^2..])), 0, 100),
            "Cada uma das 100 dezenas deve sair com a mesma frequência."));
        string[] digitNames = ["milhar", "centena", "dezena", "unidade"];
        for (var p = 0; p < 4; p++)
        {
            var position = p;
            tests.Add(ChiSquareUniform($"Dígito da {digitNames[p]}", CountsOf(numbers.Select(x => x[position] - '0'), 0, 10),
                $"O dígito na posição {p + 1} da milhar deve ser uniforme entre 0 e 9."));
        }
        foreach (var slot in extractions.GroupBy(x => x.Time).OrderBy(x => x.Key))
        {
            var slotNumbers = slot.SelectMany(x => x.Numbers).ToList();
            var label = $"{slot.Key.Hours:00}:{slot.Key.Minutes:00}";
            if (slotNumbers.Count / 25d < 5) { skipped.Add($"Grupos às {label}: {slotNumbers.Count} números, esperado por grupo abaixo de 5."); continue; }
            tests.Add(ChiSquareUniform($"Grupos às {label}", CountsOf(slotNumbers.Select(GroupOf), 1, 25),
                $"Frequência dos grupos somente no horário {label}."));
        }
        tests.Add(ConsecutiveDependence(extractions));
        tests.Add(WithinExtractionRepeat(extractions));
        var firstPrize = extractions.Select(x => int.Parse(x.Numbers[0][^2..])).ToList();
        tests.Add(RunsTest("Sequência par/ímpar da dezena do 1º prêmio", firstPrize.Select(x => x % 2 == 0).ToList(),
            "Alternância entre par e ímpar deve ser compatível com sorteios independentes."));
        tests.Add(RunsTest("Sequência alta/baixa da dezena do 1º prêmio", firstPrize.Select(x => x >= 50).ToList(),
            "Alternância entre dezenas 00–49 e 50–99 deve ser compatível com sorteios independentes."));

        var alpha = 0.05 / tests.Count;
        foreach (var test in tests) test.Significant = test.PValue < alpha;
        var anySignificant = tests.Any(x => x.Significant);
        var expectedGroup = groupCounts.Sum() / 25d;
        return new
        {
            bank, prizeRange, extractions = extractions.Count, numbers = numbers.Count,
            period = new { from = extractions[0].Date, to = extractions[^1].Date },
            testsRun = tests.Count, alpha = Math.Round(alpha, 6),
            tests = tests.Select(x => new { name = x.Name, kind = x.Kind, statistic = Math.Round(x.Statistic, 4),
                degreesOfFreedom = x.DegreesOfFreedom, pValue = Math.Round(x.PValue, 6), significant = x.Significant,
                sample = x.Sample, description = x.Description }),
            skipped,
            groupFrequency = groupCounts.Select((observed, index) => new { group = index + 1, observed,
                expected = Math.Round(expectedGroup, 2), deviation = Math.Round(observed - expectedGroup, 2) }),
            verdict = anySignificant ? "DESVIO DETECTADO — CONFIRMAR NA VALIDAÇÃO" : "SEM EVIDÊNCIA CONTRA ALEATORIEDADE",
            methodology = $"Qui-quadrado de aderência, testes z de dependência e testes de sequências (Wald–Wolfowitz). Correção de Bonferroni: cada teste é significativo somente se p < 0,05/{tests.Count}.",
            interpretation = anySignificant
                ? "Algum teste rejeitou a uniformidade. Isso precisa ser confirmado em dados novos antes de virar regra de previsão."
                : "Nenhum teste encontrou desvio da aleatoriedade. Nesse caso nenhuma mudança na fórmula de previsão pode aumentar a taxa de acerto esperada."
        };
    }

    private static TestResult ConsecutiveDependence(List<Extraction> extractions)
    {
        double observed = 0, expected = 0, variance = 0; var sample = 0;
        for (var i = 1; i < extractions.Count; i++)
        {
            var previous = extractions[i - 1].Numbers.Select(GroupOf).ToHashSet();
            var p = previous.Count / 25d;
            foreach (var number in extractions[i].Numbers)
            {
                if (previous.Contains(GroupOf(number))) observed++;
                expected += p; variance += p * (1 - p); sample++;
            }
        }
        var z = variance == 0 ? 0 : (observed - expected) / Math.Sqrt(variance);
        return new TestResult("Repetição de grupos entre extrações consecutivas", "z", z, null, TwoSidedP(z), sample,
            $"Prêmios cujo grupo já saiu na extração anterior: observado {observed:0}, esperado {expected:0.0}.");
    }

    private static TestResult WithinExtractionRepeat(List<Extraction> extractions)
    {
        const double p = 1 / 25d;
        double observed = 0, expected = 0, variance = 0; var sample = 0;
        foreach (var extraction in extractions.Where(x => x.Numbers.Count > 1))
        {
            var first = GroupOf(extraction.Numbers[0]);
            foreach (var number in extraction.Numbers.Skip(1))
            {
                if (GroupOf(number) == first) observed++;
                expected += p; variance += p * (1 - p); sample++;
            }
        }
        var z = variance == 0 ? 0 : (observed - expected) / Math.Sqrt(variance);
        return new TestResult("Grupo do 1º prêmio repetido nos demais prêmios", "z", z, null, TwoSidedP(z), sample,
            $"Prêmios 2º em diante com o mesmo grupo do 1º: observado {observed:0}, esperado {expected:0.0}.");
    }

    private static TestResult RunsTest(string name, List<bool> sequence, string description)
    {
        double n1 = sequence.Count(x => x), n2 = sequence.Count - n1, n = sequence.Count;
        var runs = sequence.Count == 0 ? 0 : 1 + Enumerable.Range(1, sequence.Count - 1).Count(i => sequence[i] != sequence[i - 1]);
        var mean = 2 * n1 * n2 / n + 1;
        var variance = 2 * n1 * n2 * (2 * n1 * n2 - n) / (n * n * (n - 1));
        var z = variance <= 0 ? 0 : (runs - mean) / Math.Sqrt(variance);
        return new TestResult(name, "z", z, null, TwoSidedP(z), sequence.Count, $"{description} Sequências: {runs}, esperado {mean:0.0}.");
    }

    private static TestResult ChiSquareUniform(string name, int[] counts, string description)
    {
        var total = counts.Sum();
        var expected = total / (double)counts.Length;
        var statistic = expected == 0 ? 0 : counts.Sum(x => Math.Pow(x - expected, 2) / expected);
        var df = counts.Length - 1;
        return new TestResult(name, "qui-quadrado", statistic, df, ChiSquarePValue(statistic, df), total, description);
    }

    private static int[] CountsOf(IEnumerable<int> values, int first, int count)
    {
        var counts = new int[count];
        foreach (var value in values) counts[value - first]++;
        return counts;
    }

    private static int GroupOf(string number) { var value = int.Parse(number[^2..]); return value == 0 ? 25 : (value + 3) / 4; }

    public static double TwoSidedP(double z) => Math.Clamp(2 * (1 - NormalCdf(Math.Abs(z))), 0, 1);

    public static double ChiSquarePValue(double statistic, int degreesOfFreedom) =>
        statistic <= 0 ? 1 : GammaQ(degreesOfFreedom / 2d, statistic / 2);

    // Abramowitz & Stegun 7.1.26 (erro absoluto < 1,5e-7).
    public static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + .3275911 * x);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - .284496736) * t + .254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? .5 * (1 + erf) : .5 * (1 - erf);
    }

    // Gama incompleta superior regularizada Q(a,x), como em Numerical Recipes (série ou fração contínua).
    public static double GammaQ(double a, double x)
    {
        if (x <= 0) return 1;
        if (x < a + 1)
        {
            double sum = 1 / a, term = sum, ap = a;
            for (var n = 0; n < 1000 && Math.Abs(term) > Math.Abs(sum) * 1e-15; n++) { ap++; term *= x / ap; sum += term; }
            return Math.Clamp(1 - sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a)), 0, 1);
        }
        const double tiny = 1e-300;
        double b = x + 1 - a, c = 1 / tiny, d = 1 / b, h = d;
        for (var i = 1; i < 1000; i++)
        {
            var an = -i * (i - a); b += 2;
            d = an * d + b; if (Math.Abs(d) < tiny) d = tiny;
            c = b + an / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d; var delta = d * c; h *= delta;
            if (Math.Abs(delta - 1) < 1e-15) break;
        }
        return Math.Clamp(Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h, 0, 1);
    }

    // Aproximação de Lanczos (g=7, n=9).
    public static double LogGamma(double x)
    {
        double[] c = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (x < .5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        var sum = c[0];
        for (var i = 1; i < 9; i++) sum += c[i] / (x + i);
        var t = x + 7.5;
        return .5 * Math.Log(2 * Math.PI) + (x + .5) * Math.Log(t) - t + Math.Log(sum);
    }
}
