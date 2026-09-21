using System.Diagnostics;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public static class MilharBacktestEngine
{
    public static MilharBacktestResponse Run(IReadOnlyList<MilharObservation> source, MilharBacktestRequest request, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew(); var config = request.Configuration ?? new();
        MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, config, request.Model);
        if (request.Start == default || request.End == DateTime.MaxValue || request.End < request.Start || request.Start.Kind != DateTimeKind.Unspecified || request.End.Kind != DateTimeKind.Unspecified || request.MinimumHistory < 2)
            throw new ArgumentException("Informe período local válido e histórico mínimo de pelo menos 2 resultados.");
        if (request.Optimize is { Length: > 12 }) throw new ArgumentException("Informe até 12 configurações para validação.");
        foreach (var option in request.Optimize ?? [])
        {
            if (option is null) throw new ArgumentException("Configuração de busca não pode ser nula.");
            MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, option, "C");
        }
        // Split whole timestamps, never prizes from one extraction across partitions.
        var historyRequest = new MilharRankingRequest(request.Bank, request.Time, request.End.AddTicks(1), request.Prize);
        var available = MilharStatisticsEngine.History(source, historyRequest);
        var targets = available.Where(x => x.Time == request.Time && x.Timestamp >= request.Start && x.Timestamp <= request.End)
            .GroupBy(x => x.Timestamp).Where(g => LowerBound(available, g.Key) >= request.MinimumHistory).ToArray();
        if (targets.Length == 0) throw new ArgumentException("Não há extrações elegíveis no período com o histórico mínimo informado.");
        if (targets.Length > 2000) throw new ArgumentException("Limite de 2000 extrações por execução. Reduza o período.");
        int trainEnd = (int)(targets.Length * .6), validationEnd = (int)(targets.Length * .8);
        string Partition(int i) => i < trainEnd ? "train" : i < validationEnd ? "validation" : "test";
        var search = new List<object>(); MilharConfiguration? winner = null;
        if (request.Optimize is { Length: > 0 })
        {
            if (trainEnd == 0 || validationEnd <= trainEnd || validationEnd >= targets.Length)
                throw new ArgumentException("Otimização exige extrações nas três partições.");
            double best = double.NegativeInfinity;
            foreach (var option in request.Optimize)
            {
                var validation = Evaluate(option, ["C"], trainEnd, validationEnd)["C"];
                var metric = Metrics(validation);
                search.Add(new { Configuration = option, Validation = metric });
                // Frozen objective, chosen before touching final test outcomes.
                if (metric.Mrr!.Value > best) { best = metric.Mrr.Value; winner = option; }
            }
            config = winner!;
        }
        var models = new List<string> { request.Model };
        if (request.CompareModels) models.AddRange(["A", "B", "C"]);
        if (request.Ablation) { models.Add("C"); models.AddRange(MilharStatisticsEngine.Components.Select(x => "without-" + x)); }
        models = models.Distinct().ToList();
        foreach (var model in models) MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, config, model);
        var evaluations = Evaluate(config, models, 0, targets.Length);
        var results = models.Select(model => new MilharModelResult(model, config, Metrics(evaluations[model]),
            new[] { "train", "validation", "test" }.ToDictionary(p => p, p => Metrics(evaluations[model].Where(x => x.Partition == p).ToList())), evaluations[model])).ToList();
        return new(MilharStatisticsEngine.Version, request with { Configuration = request.Configuration ?? new() }, results, winner, search.ToArray(), timer.ElapsedMilliseconds,
            "Walk-forward por data/hora local da extração; corte estrito (<), inclusive para todos os prêmios simultâneos. " +
            "Partições cronológicas 60/20/20 por extração elegível. Features são atualizadas com o passado observado; pesos ficam fixos. " +
            "Busca opcional escolhe maior MRR apenas na validação e avalia o vencedor uma vez no teste nesta execução. " +
            "Top K de centena/dezena verifica os sufixos das K milhares. Baseline uniforme sem reposição. " +
            "IC 95% Wilson por resultado, aproximado: prêmios e janelas podem ser dependentes; comparações múltiplas são exploratórias. " +
            "Empates usam milhar crescente. Score não é probabilidade; acertos isolados não demonstram poder preditivo.");

        Dictionary<string, List<MilharTrial>> Evaluate(MilharConfiguration settings, List<string> names, int start, int end)
        {
            var output = names.ToDictionary(x => x, _ => new List<MilharTrial>());
            var weights = names.ToDictionary(x => x, x => MilharStatisticsEngine.Weights(settings, x));
            for (var i = start; i < end; i++)
            {
                token.ThrowIfCancellationRequested(); var target = targets[i];
                // One feature pass per timestamp/configuration, shared across models and prizes.
                var response = MilharStatisticsEngine.Rank(available, new(request.Bank, request.Time, target.Key, request.Prize, 10000, "C", settings), token, false);
                var features = response.Ranking;
                foreach (var model in names)
                {
                    var weight = weights[model];
                    var order = features.Select(x => (Candidate: x, Score: Weighted(x.Scores, weight)))
                        .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Milhar, StringComparer.Ordinal).Select(x => x.Candidate).ToArray();
                    var rankMap = new int[10000]; for (var r = 0; r < order.Length; r++) rankMap[int.Parse(order[r].Milhar)] = r + 1;
                    foreach (var actual in target)
                    {
                        var rank = rankMap[int.Parse(actual.Number)];
                        output[model].Add(new(actual.ExtractionId, target.Key, actual.Bank, actual.Time, actual.Position, actual.Number, Partition(i), rank,
                            MilharStatisticsEngine.Tops.Select(k => rank <= k).ToArray(),
                            MilharStatisticsEngine.Tops.Select(k => order.Take(k).Any(c => c.Centena == actual.Number[1..])).ToArray(),
                            MilharStatisticsEngine.Tops.Select(k => order.Take(k).Any(c => c.Dezena == actual.Number[2..])).ToArray(),
                            response.Sample, response.HistoryEnd!.Value));
                    }
                }
            }
            return output;
        }
    }
    private static int LowerBound(List<MilharObservation> rows, DateTime cutoff)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (rows[mid].Timestamp < cutoff) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public static double Weighted(MilharScores s, double[] w) =>
        s.Dezena * w[0] + s.Centena * w[1] + s.Digitos * w[2] + s.Recencia * w[3] + s.Horario * w[4] + s.Pares * w[5] + s.Transicao * w[6];
    public static double Baseline(int top, int matchingNumbers)
    {
        double miss = 1; for (var i = 0; i < top; i++) miss *= (10000d - matchingNumbers - i) / (10000 - i);
        return 1 - miss;
    }
    public static (double Low, double High) Wilson(int hits, int count)
    {
        if (count <= 0) throw new ArgumentException("Amostra vazia.");
        const double z = 1.959963984540054;
        var p = (double)hits / count; var denominator = 1 + z * z / count;
        var center = (p + z * z / (2 * count)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / count + z * z / (4d * count * count)) / denominator;
        return (Math.Max(0, center - margin), Math.Min(1, center + margin));
    }
    public static MilharMetrics Metrics(List<MilharTrial> rows)
    {
        var rates = new List<MilharRate>();
        foreach (var kind in new[] { "milhar", "centena", "dezena" })
        for (var i = 0; i < 4; i++)
        {
            var hits = rows.Count(x => (kind == "milhar" ? x.MilharHits : kind == "centena" ? x.CentenaHits : x.DezenaHits)[i]);
            var rate = rows.Count == 0 ? 0 : (double)hits / rows.Count;
            var baseline = Baseline(MilharStatisticsEngine.Tops[i], kind == "milhar" ? 1 : kind == "centena" ? 10 : 100);
            var ci = rows.Count == 0 ? ((double Low, double High)?)null : Wilson(hits, rows.Count);
            rates.Add(new(kind, MilharStatisticsEngine.Tops[i], hits, rate, baseline, rate - baseline, rate / baseline, ci?.Low, ci?.High));
        }
        var sorted = rows.Select(x => x.Rank).Order().ToArray();
        int[] boundaries = [0, 10, 20, 50, 100, 250, 500, 1000, 5000, 10000];
        var distribution = new Dictionary<string, int>();
        for (var i = 1; i < boundaries.Length; i++) distribution[$"{boundaries[i - 1] + 1}-{boundaries[i]}"] = rows.Count(x => x.Rank > boundaries[i - 1] && x.Rank <= boundaries[i]);
        return new(rows.Count, rows.Count == 0 ? null : rows.Average(x => x.Rank),
            rows.Count == 0 ? null : (sorted[(rows.Count - 1) / 2] + sorted[rows.Count / 2]) / 2d,
            rows.Count == 0 ? null : rows.Average(x => 1d / x.Rank), rates, distribution);
    }
}
