using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

/// <summary>Adapter between the statistical engine and the existing persisted prediction contract.</summary>
public static class MilharPredictionSelection
{
    public const string Algorithm = "MILHAR_STATISTICS";
    public const int Version = 1;

    public static List<PredictionCandidate> Generate(IReadOnlyList<MilharObservation> history, string bank,
        DateTime cutoff, int quantity, int prizeRange, int[] groups, MilharConfiguration configuration)
    {
        groups = groups.Distinct().Order().ToArray();
        if (prizeRange is < 1 or > 10 || groups.Any(g => g is < 1 or > 25) || quantity < 1 || quantity > (groups.Length == 0 ? 10000 : groups.Length * 400))
            throw new ArgumentException("Quantidade, prêmios ou grupos inválidos.");
        var eligible = history.Where(x => x.Position <= prizeRange).ToArray();
        var response = MilharStatisticsEngine.Rank(eligible,
            new(bank, cutoff.ToString("HH:mm"), cutoff, null, 10000, "C", configuration), evidence: false);
        var quotas = groups.Select((g, i) => (g, count: quantity / groups.Length + (i < quantity % groups.Length ? 1 : 0)))
            .ToDictionary(x => x.g, x => x.count);
        var counts = new Dictionary<int, int>();
        var result = new List<PredictionCandidate>(quantity);
        foreach (var candidate in response.Ranking)
        {
            var dezena = int.Parse(candidate.Dezena); var group = dezena == 0 ? 25 : (dezena + 3) / 4;
            if (groups.Length > 0 && (!quotas.TryGetValue(group, out var quota) || counts.GetValueOrDefault(group) >= quota)) continue;
            counts[group] = counts.GetValueOrDefault(group) + 1;
            var s = candidate.Scores;
            double[] values = [s.Dezena, s.Centena, s.Digitos, s.Recencia, s.Horario, s.Pares, s.Transicao];
            string[] labels = ["dezena", "centena", "dígitos", "recência", "horário", "pares", "transição"];
            var reasons = Enumerable.Range(0, 7).OrderByDescending(i => values[i] * response.EffectiveWeights[i])
                .Where(i => values[i] * response.EffectiveWeights[i] > 0).Take(3)
                .Select(i => $"{labels[i]}: {values[i]:F4} × peso {response.EffectiveWeights[i]:F2}").ToList();
            // Existing prediction screens use 0..100; native components remain in 0..1.
            var score = candidate.ScoreFinal * 100;
            result.Add(new(result.Count + 1, candidate.Milhar, candidate.Centena, candidate.Dezena, group,
                "STATISTICAL", score, score,
                new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, s, candidate.Ranking), reasons));
            if (result.Count == quantity) break;
        }
        return result;
    }
}
