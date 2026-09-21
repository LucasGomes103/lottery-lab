using System.Diagnostics;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

/// <summary>Pure, deterministic engine. No clock, database, random selection or learned future state.</summary>
public static class MilharStatisticsEngine
{
    public const string Version = "MILHAR_STATISTICS_1";
    public static readonly string[] Components = ["dezena", "centena", "digitos", "recencia", "horario", "pares", "transicao"];
    public static readonly int[] Tops = [10, 20, 50, 100];
    private static readonly (int A, int B)[] PairPositions = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)];
    private static readonly string[] PairNames = ["AB", "AC", "AD", "BC", "BD", "CD"];
    private static readonly Parts[] Candidates = Enumerable.Range(0, 10000).Select(Extract).ToArray();
    public record Parts(string Number, int Dezena, int Centena, int[] Digits, int[] Pairs);
    public static Parts Extract(int value)
    {
        if (value is < 0 or > 9999) throw new ArgumentException("Milhar inválida.");
        int[] digits = [value / 1000, value / 100 % 10, value / 10 % 10, value % 10];
        return new(value.ToString("D4"), value % 100, value % 1000, digits,
            PairPositions.Select(p => digits[p.A] * 10 + digits[p.B]).ToArray());
    }
    public static double Decay(int distance, double halfLife) => Math.Exp(-Math.Log(2) * distance / halfLife);
    public static void Normalize(double[] values)
    {
        var min = values.Min(); var range = values.Max() - min;
        for (var i = 0; i < values.Length; i++) values[i] = range <= 1e-15 ? 0 : (values[i] - min) / range;
    }
    public static void Validate(string bank, string time, int? prize, MilharConfiguration config, string model)
    {
        if (string.IsNullOrWhiteSpace(bank) || bank.Length > 80 || !TimeOnly.TryParseExact(time, "HH:mm", out _) || prize is < 1 or > 10)
            throw new ArgumentException("Informe banca, horário HH:mm e prêmio entre 1 e 10 (ou todos).");
        if (model is not ("A" or "B" or "C") && !Components.Any(c => model == "without-" + c))
            throw new ArgumentException("Modelo inválido.");
        if (config.Weights is null || config.Weights.Length != 7 || !ValidWeights(config.Weights) ||
            config.Windows is null || config.WindowWeights is null || config.Windows.Length is < 1 or > 10 ||
            config.Windows.Length != config.WindowWeights.Length || config.Windows.Any(x => x < 0) || !ValidWeights(config.WindowWeights) ||
            !double.IsFinite(config.HalfLife) || config.HalfLife <= 0 || !double.IsFinite(config.Alpha) || config.Alpha <= 0 ||
            !double.IsFinite(config.SchedulePrior) || config.SchedulePrior <= 0)
            throw new ArgumentException("Pesos devem ser finitos, não negativos e somar 1; half-life, alpha e prior devem ser positivos.");
        _ = Weights(config, model);
    }
    private static bool ValidWeights(double[] w) => w.All(x => double.IsFinite(x) && x >= 0) && Math.Abs(w.Sum() - 1) < 1e-8;
    public static double[] Weights(MilharConfiguration config, string model)
    {
        var weights = (double[])config.Weights.Clone();
        for (var i = 0; i < 7; i++)
            if ((model == "A" && i is not (0 or 1 or 3)) || (model == "B" && i is not (4 or 5 or 6)) || model == "without-" + Components[i]) weights[i] = 0;
        var sum = weights.Sum();
        if (sum <= 0) throw new ArgumentException("O modelo selecionado precisa de ao menos um peso positivo.");
        return weights.Select(w => w / sum).ToArray();
    }
    public static bool ValidNumber(string? number) => number is { Length: 4 } && number.All(c => c is >= '0' and <= '9');
    public static List<MilharObservation> History(IEnumerable<MilharObservation> source, MilharRankingRequest request) =>
        source.Where(x => x.Bank == request.Bank && x.Timestamp < request.Cutoff &&
            (request.Prize == null || x.Position == request.Prize) && x.Position is >= 1 and <= 10 && ValidNumber(x.Number))
        .OrderBy(x => x.Timestamp).ThenBy(x => x.Position).ThenBy(x => x.ExtractionId).ToList();

    public static MilharRankingResponse Rank(IReadOnlyList<MilharObservation> source, MilharRankingRequest request,
        CancellationToken cancellationToken = default, bool evidence = true)
    {
        var timer = Stopwatch.StartNew(); var config = request.Configuration ?? new();
        Validate(request.Bank, request.Time, request.Prize, config, request.Model);
        if (request.Top is < 1 or > 10000 || request.Cutoff == default || request.Cutoff.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("Informe top de 1 a 10000 e corte local da extração sem offset de fuso.");
        var history = History(source, request);
        if (history.Count == 0) throw new ArgumentException("Não há histórico válido anterior ao corte.");
        var global = new Frequencies(history, config);
        var scheduleRows = history.Where(x => x.Time == request.Time).ToList();
        var schedule = new Frequencies(scheduleRows, config);
        var reliability = scheduleRows.Count / (scheduleRows.Count + config.SchedulePrior);
        var transitions = new TransitionFeatures(scheduleRows, config.Alpha);
        var raw = Enumerable.Range(0, 7).Select(_ => new double[10000]).ToArray();
        for (var n = 0; n < 10000; n++)
        {
            if (n % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            var p = Candidates[n];
            raw[0][n] = global.Dezena[p.Dezena]; raw[1][n] = global.Centena[p.Centena];
            raw[2][n] = global.DigitsScore(p); raw[3][n] = global.Recency[p.Dezena];
            raw[4][n] = (schedule.Dezena[p.Dezena] + schedule.Centena[p.Centena] + schedule.DigitsScore(p)) / 3;
            raw[5][n] = global.PairScore(p); raw[6][n] = transitions.Score(p);
        }
        foreach (var component in raw) Normalize(component);
        // Keep evidence strength after normalization; min/max alone would undo Bayesian shrinkage.
        var centenaReliability = history.Count / (history.Count + 1000 * config.Alpha);
        for (var n = 0; n < 10000; n++) { raw[1][n] *= centenaReliability; raw[4][n] *= reliability; raw[6][n] *= transitions.Reliability; }
        var weights = Weights(config, request.Model);
        var scores = new double[10000];
        for (var n = 0; n < 10000; n++) for (var k = 0; k < 7; k++) scores[n] += weights[k] * raw[k][n];
        var order = Enumerable.Range(0, 10000).ToArray();
        Array.Sort(order, (a, b) => { var c = scores[b].CompareTo(scores[a]); return c != 0 ? c : a.CompareTo(b); });
        var ranking = new List<MilharCandidate>(request.Top);
        for (var i = 0; i < request.Top; i++)
        {
            var n = order[i]; var p = Candidates[n];
            var globalWeight = 1 - weights[4];
            ranking.Add(new(i + 1, p.Number, p.Number[1..], p.Number[2..], scores[n],
                globalWeight > 0 ? (scores[n] - weights[4] * raw[4][n]) / globalWeight : 0,
                new(raw[0][n], raw[1][n], raw[2][n], raw[3][n], raw[4][n], raw[5][n], raw[6][n]),
                evidence ? new(global.WindowEvidence(p), Enumerable.Range(0, 4).Select(k => global.Digits[k, p.Digits[k]]).ToArray(),
                    Enumerable.Range(0, 6).ToDictionary(k => PairNames[k] + "=" + p.Pairs[k].ToString("D2"), k => global.Pairs[k, p.Pairs[k]]),
                    transitions.Evidence(p), scheduleRows.Count, reliability) : null));
        }
        var invalid = source.Count(x => x.Bank == request.Bank && x.Timestamp < request.Cutoff &&
            (request.Prize == null || x.Position == request.Prize) && !ValidNumber(x.Number));
        return new(Version, request with { Configuration = config }, history.Count, invalid, history[^1].Timestamp, weights, ranking, timer.ElapsedMilliseconds);
    }

    private sealed class Frequencies
    {
        public double[] Dezena = new double[100], Centena = new double[1000], Recency = new double[100];
        public double[,] Digits = new double[4, 10], Pairs = new double[6, 100];
        private readonly Dictionary<string, double[]> windows = [];
        public Frequencies(List<MilharObservation> rows, MilharConfiguration config)
        {
            for (var w = 0; w < config.Windows.Length; w++)
            {
                var size = config.Windows[w] == 0 ? rows.Count : Math.Min(config.Windows[w], rows.Count);
                var tens = new double[100]; var hundreds = new double[1000]; var digits = new double[4, 10]; var pairs = new double[6, 100];
                for (var i = rows.Count - size; i < rows.Count; i++)
                {
                    var p = Candidates[int.Parse(rows[i].Number)]; tens[p.Dezena]++; hundreds[p.Centena]++;
                    for (var k = 0; k < 4; k++) digits[k, p.Digits[k]]++;
                    for (var k = 0; k < 6; k++) pairs[k, p.Pairs[k]]++;
                }
                windows[config.Windows[w] == 0 ? "total" : config.Windows[w].ToString()] = tens.Select(x => size == 0 ? 0 : x / size).ToArray();
                var weight = config.WindowWeights[w];
                for (var k = 0; k < 100; k++) Dezena[k] += weight * (size == 0 ? 0 : tens[k] / size);
                for (var k = 0; k < 1000; k++) Centena[k] += weight * (hundreds[k] + config.Alpha) / (size + 1000 * config.Alpha);
                for (var k = 0; k < 4; k++) for (var d = 0; d < 10; d++) Digits[k, d] += weight * (digits[k, d] + config.Alpha) / (size + 10 * config.Alpha);
                for (var k = 0; k < 6; k++) for (var d = 0; d < 100; d++) Pairs[k, d] += weight * (pairs[k, d] + config.Alpha) / (size + 100 * config.Alpha);
            }
            double sum = 0;
            for (var i = 0; i < rows.Count; i++) { var weight = Decay(rows.Count - 1 - i, config.HalfLife); Recency[int.Parse(rows[i].Number) % 100] += weight; sum += weight; }
            if (sum > 0) for (var i = 0; i < 100; i++) Recency[i] /= sum;
        }
        public double DigitsScore(Parts p) { double total = 0; for (var k = 0; k < 4; k++) total += Digits[k, p.Digits[k]]; return total / 4; }
        public double PairScore(Parts p) { double total = 0; for (var k = 0; k < 6; k++) total += Pairs[k, p.Pairs[k]]; return total / 6; }
        public Dictionary<string, double> WindowEvidence(Parts p) => windows.ToDictionary(x => x.Key, x => x.Value[p.Dezena]);
    }

    private sealed class TransitionFeatures
    {
        private readonly List<(MilharObservation Previous, double[] Tens, double[] Hundreds, double[,] Digits, double[] Groups, int Support)> streams = [];
        public double Reliability { get; }
        public TransitionFeatures(List<MilharObservation> rows, double alpha)
        {
            foreach (var stream in rows.GroupBy(x => x.Position))
            {
                var sequence = stream.ToArray(); var previous = sequence[^1]; var p = Candidates[int.Parse(previous.Number)];
                var tens = new double[100]; var hundreds = new double[100]; var digits = new double[4, 10]; var groups = new double[25];
                int nt = 0, nc = 0, ng = 0; var nd = new int[4];
                for (var i = 1; i < sequence.Length; i++)
                {
                    if (sequence[i - 1].Timestamp >= sequence[i].Timestamp) continue;
                    var a = Candidates[int.Parse(sequence[i - 1].Number)]; var b = Candidates[int.Parse(sequence[i].Number)];
                    if (a.Dezena == p.Dezena) { tens[b.Dezena]++; nt++; }
                    if (a.Centena == p.Centena) { hundreds[b.Dezena]++; nc++; }
                    for (var k = 0; k < 4; k++) if (a.Digits[k] == p.Digits[k]) { digits[k, b.Digits[k]]++; nd[k]++; }
                    if (previous.Group is >= 1 and <= 25 && sequence[i - 1].Group == previous.Group && sequence[i].Group is >= 1 and <= 25)
                    { groups[sequence[i].Group!.Value - 1]++; ng++; }
                }
                for (var k = 0; k < 100; k++) { tens[k] = (tens[k] + alpha) / (nt + 100 * alpha); hundreds[k] = (hundreds[k] + alpha) / (nc + 100 * alpha); }
                for (var k = 0; k < 4; k++) for (var d = 0; d < 10; d++) digits[k, d] = (digits[k, d] + alpha) / (nd[k] + 10 * alpha);
                for (var k = 0; k < 25; k++) groups[k] = (groups[k] + alpha) / (ng + 25 * alpha);
                streams.Add((previous, tens, hundreds, digits, groups, nt + nc + nd.Sum() + ng));
            }
            var support = streams.Sum(x => x.Support);
            Reliability = support / (support + 100 * alpha);
        }
        public double Score(Parts p)
        {
            double value = 0;
            foreach (var s in streams) { value += s.Tens[p.Dezena] + s.Hundreds[p.Dezena]; for (var k = 0; k < 4; k++) value += s.Digits[k, p.Digits[k]]; value += s.Groups[Group(p.Dezena) - 1]; }
            return streams.Count == 0 ? 0 : value / (streams.Count * 7);
        }
        private static int Group(int dezena) => dezena == 0 ? 25 : (dezena - 1) / 4 + 1;
        public object Evidence(Parts p) => streams.Select(s => new { s.Previous.Position, s.Previous.Number, s.Previous.Timestamp,
            DezenaToDezena = s.Tens[p.Dezena], CentenaToDezena = s.Hundreds[p.Dezena],
            Digits = Enumerable.Range(0, 4).Select(k => s.Digits[k, p.Digits[k]]), Group = s.Groups[Group(p.Dezena) - 1], s.Support, Reliability }).ToArray();
    }
}
