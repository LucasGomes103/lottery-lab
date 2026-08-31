using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed class NumberGeneratorService(Db db)
{
    public async Task<NumberGenerationResponse> Generate(string bank, string time, DateOnly targetDate, int windowDays, int quantity)
    {
        await using var connection = db.Open();
        var target = targetDate.ToDateTime(TimeOnly.MinValue);
        var start = target.AddDays(-windowDays);
        var rows = (await connection.QueryAsync<(DateTime Date, string Number)>(
            @"select e.extraction_date as Date, r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@bank and e.extraction_time=@time::time
                and e.extraction_date < @target and e.extraction_date >= @start
                and r.position between 1 and 5
              order by e.extraction_date, r.position",
            new { bank, time, target, start })).ToList();

        var sampleExtractions = rows.Select(x => x.Date.Date).Distinct().Count();
        if (rows.Count == 0)
            return new NumberGenerationResponse("HISTORICAL_SUFFIX_V1", bank, time, targetDate, windowDays, 0, 0,
                "INSUFICIENTE", [], new
                {
                    distinctMilhares = 0, distinctCentenas = 0, distinctDezenas = 0,
                    onePositionMilharCoveragePercent = 0,
                    onePositionCentenaCoveragePercent = 0,
                    onePositionDezenaCoveragePercent = 0
                }, "Não existem resultados anteriores suficientes para gerar um ranking histórico.");
        var milharFrequency = new Dictionary<string, double>();
        var centenaFrequency = new Dictionary<string, double>();
        var dezenaFrequency = new Dictionary<string, double>();
        var digitFrequency = new double[4, 10];
        var halfLife = Math.Max(7d, windowDays / 2d);

        foreach (var row in rows)
        {
            var number = row.Number.PadLeft(4, '0')[^4..];
            var age = Math.Max(0, (target.Date - row.Date.Date).TotalDays);
            var weight = Math.Pow(0.5, age / halfLife);
            Add(milharFrequency, number, weight);
            Add(centenaFrequency, number[^3..], weight);
            Add(dezenaFrequency, number[^2..], weight);
            for (var position = 0; position < 4; position++) digitFrequency[position, number[position] - '0'] += weight;
        }

        var maxMilhar = Math.Max(1, milharFrequency.Values.DefaultIfEmpty(0).Max());
        var maxCentena = Math.Max(1, centenaFrequency.Values.DefaultIfEmpty(0).Max());
        var maxDezena = Math.Max(1, dezenaFrequency.Values.DefaultIfEmpty(0).Max());
        var maxDigits = Enumerable.Range(0, 4).Select(position => Enumerable.Range(0, 10).Max(digit => digitFrequency[position, digit])).ToArray();

        var ranked = Enumerable.Range(0, 10_000).Select(value =>
        {
            var milhar = value.ToString("0000");
            var centena = milhar[^3..];
            var dezena = milhar[^2..];
            var milharSignal = milharFrequency.GetValueOrDefault(milhar) / maxMilhar;
            var centenaSignal = centenaFrequency.GetValueOrDefault(centena) / maxCentena;
            var dezenaSignal = dezenaFrequency.GetValueOrDefault(dezena) / maxDezena;
            var digitSignal = Enumerable.Range(0, 4).Average(position => maxDigits[position] == 0 ? 0 : digitFrequency[position, milhar[position] - '0'] / maxDigits[position]);
            var score = 0.10 * milharSignal + 0.25 * centenaSignal + 0.40 * dezenaSignal + 0.25 * digitSignal;
            return new { milhar, centena, dezena, score, milharSignal, centenaSignal, dezenaSignal, digitSignal };
        }).OrderByDescending(x => x.score).ThenBy(x => x.milhar).ToList();

        var selected = new List<GeneratedNumber>();
        var usedCentenas = new HashSet<string>();
        var usedDezenas = new HashSet<string>();
        foreach (var candidate in ranked)
        {
            if (!usedCentenas.Add(candidate.centena) || !usedDezenas.Add(candidate.dezena)) continue;
            selected.Add(new GeneratedNumber(selected.Count + 1, candidate.milhar, candidate.centena, candidate.dezena,
                Math.Round(candidate.score * 100, 2), Math.Round(candidate.milharSignal * 100, 2),
                Math.Round(candidate.centenaSignal * 100, 2), Math.Round(candidate.dezenaSignal * 100, 2),
                Math.Round(candidate.digitSignal * 100, 2)));
            if (selected.Count == quantity) break;
        }

        var baseline = new
        {
            distinctMilhares = selected.Count,
            distinctCentenas = selected.Select(x => x.Centena).Distinct().Count(),
            distinctDezenas = selected.Select(x => x.Dezena).Distinct().Count(),
            onePositionMilharCoveragePercent = Math.Round(100d * selected.Count / 10_000, 4),
            onePositionCentenaCoveragePercent = Math.Round(100d * selected.Count / 1_000, 3),
            onePositionDezenaCoveragePercent = Math.Round(100d * selected.Count / 100, 2)
        };
        var robustness = sampleExtractions < 30 ? "INSUFICIENTE" : sampleExtractions < 100 ? "BAIXA" : "EXPERIMENTAL";
        return new NumberGenerationResponse("HISTORICAL_SUFFIX_V1", bank, time, targetDate, windowDays,
            sampleExtractions, rows.Count, robustness, selected, baseline,
            "Score é um ranking histórico, não uma probabilidade. Vantagem sobre escolhas aleatórias ainda precisa ser demonstrada por backtest walk-forward.");
    }

    private static void Add(Dictionary<string, double> values, string key, double weight) => values[key] = values.GetValueOrDefault(key) + weight;
}
