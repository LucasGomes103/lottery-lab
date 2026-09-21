using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class MilharStatisticsTests
{
    private static readonly DateTime Cutoff = new(2026, 1, 10, 21, 0, 0);
    private static MilharObservation Row(int day, string number, string bank = "TEST", string time = "21:00", int prize = 1) =>
        new(day, new DateTime(2026, 1, day).Add(TimeOnly.Parse(time).ToTimeSpan()), bank, time, prize, number, null);
    private static MilharRankingRequest Request => new("TEST", "21:00", Cutoff, Top: 10000);

    [Theory]
    [InlineData(5684, 84, 684, "5684")]
    [InlineData(7, 7, 7, "0007")]
    public void ExtractsSuffixesDigitsAndPositionPairs(int value, int ten, int hundred, string number)
    {
        var p = MilharStatisticsEngine.Extract(value);
        Assert.Equal(ten, p.Dezena); Assert.Equal(hundred, p.Centena); Assert.Equal(number, p.Number);
        Assert.Equal(number.Select(c => c - '0'), p.Digits);
        if (value == 5684) Assert.Equal(new[] { 56, 58, 54, 68, 64, 84 }, p.Pairs);
    }
    [Fact]
    public void DecayHasConfiguredHalfLifeAndNormalizationHandlesConstantValues()
    {
        Assert.Equal(.5, MilharStatisticsEngine.Decay(30, 30), 12);
        Assert.Equal(.25, MilharStatisticsEngine.Decay(60, 30), 12);
        double[] values = [2, 4, 6]; MilharStatisticsEngine.Normalize(values); Assert.Equal(new[] { 0d, .5, 1 }, values);
        double[] constant = [1, 1]; MilharStatisticsEngine.Normalize(constant); Assert.All(constant, x => Assert.Equal(0, x));
    }
    [Fact]
    public void RankingIsCompleteDeterministicNormalizedAndUsesConfiguredWeights()
    {
        var config = new MilharConfiguration { Weights = [1, 0, 0, 0, 0, 0, 0] };
        var result = MilharStatisticsEngine.Rank([Row(1, "5684"), Row(2, "0084")], Request with { Configuration = config });
        Assert.Equal(10000, result.Ranking.Count); Assert.Equal(10000, result.Ranking.Select(x => x.Milhar).Distinct().Count());
        Assert.Equal("0084", result.Ranking[0].Milhar);
        Assert.All(result.Ranking, x => { Assert.InRange(x.ScoreFinal, 0, 1); Assert.Equal(x.Scores.Dezena, x.ScoreFinal); });
        Assert.Equal(Enumerable.Range(1, 10000), result.Ranking.Select(x => x.Ranking));
        Assert.Equal(1, result.Ranking[0].Evidence!.DezenaFrequencies["10"]);
    }
    [Fact]
    public void NoFutureLeakageExcludesTargetAndAllSimultaneousPrizes()
    {
        var old = new[] { Row(1, "1234"), Row(2, "5678") };
        var request = Request with { Prize = null };
        var expected = MilharStatisticsEngine.Rank(old, request);
        var actual = MilharStatisticsEngine.Rank([.. old, Row(10, "9999"), Row(10, "9999", prize: 2), Row(11, "9999")], request);
        Assert.Equal(expected.Ranking.Select(x => (x.Milhar, x.ScoreFinal, x.Scores)), actual.Ranking.Select(x => (x.Milhar, x.ScoreFinal, x.Scores)));
        Assert.Equal(2, actual.Sample); Assert.True(actual.HistoryEnd < Cutoff);
    }
    [Fact]
    public void FiltersBankPrizeAndKeepsScheduleFeatureSeparateFromGlobal()
    {
        var rows = new[] { Row(1, "1234"), Row(2, "5678", time: "12:00"), Row(3, "9999", bank: "OTHER"), Row(4, "9999", prize: 2) };
        var ranked = MilharStatisticsEngine.Rank(rows, Request);
        Assert.Equal(2, ranked.Sample); Assert.Equal(1, ranked.Ranking[0].Evidence!.ScheduleSample);
        var noOtherBank = MilharStatisticsEngine.Rank(rows.Take(2).ToArray(), Request);
        Assert.Equal(noOtherBank.Ranking.Select(x => x.ScoreFinal), ranked.Ranking.Select(x => x.ScoreFinal));
        Assert.All(ranked.Ranking, x => Assert.InRange(x.Scores.Horario, 0, 1d / 51));
    }
    [Fact]
    public void InvalidConfigurationAndEmptyHistoryAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MilharStatisticsEngine.Rank([], Request));
        Assert.Throws<ArgumentException>(() => MilharStatisticsEngine.Rank([Row(1, "1234")], Request with { Configuration = new() { HalfLife = 0 } }));
        Assert.Throws<ArgumentException>(() => MilharStatisticsEngine.Rank([Row(1, "1234")], Request with { Configuration = new() { Weights = [1, 1, 1, 1, 1, 1, 1] } }));
    }

    private static List<MilharObservation> Series(int count = 20) => Enumerable.Range(0, count)
        .Select(i => new MilharObservation(i + 1, new DateTime(2025, 1, 1, 21, 0, 0).AddDays(i), "TEST", "21:00", 1, (i * 137 % 10000).ToString("D4"), null)).ToList();
    private static MilharBacktestRequest BacktestRequest => new("TEST", "21:00", new(2025, 1, 1), new(2025, 2, 1), MinimumHistory: 2);

    [Fact]
    public void BacktestUsesStrictPastAndModelsShareTargetsAndPartitions()
    {
        var rows = Series(12); rows.AddRange(rows.ToArray().Select(x => x with { Position = 2, Number = "9999" }));
        var report = MilharBacktestEngine.Run(rows, BacktestRequest with { Prize = null, Ablation = true });
        Assert.Equal(10, report.Models.Count);
        var reference = report.Models[0].Trials;
        Assert.All(report.Models, m => Assert.Equal(reference.Select(x => (x.ExtractionId, x.Prize, x.Partition)), m.Trials.Select(x => (x.ExtractionId, x.Prize, x.Partition))));
        Assert.All(reference, x => { Assert.True(x.HistoryEnd < x.Date); Assert.Equal(rows.Count(r => r.Timestamp < x.Date), x.HistoryCount); });
        Assert.All(reference.GroupBy(x => x.Date), g => Assert.Single(g.Select(x => x.Partition).Distinct()));
        Assert.Equal(reference.Count, report.Models[0].All.Distribution.Values.Sum());
    }

    [Fact]
    public void BacktestNoFutureLeakageEarlierRanksDoNotChangeWhenFutureChanges()
    {
        var rows = Series(12); var request = BacktestRequest with { CompareModels = false };
        var original = MilharBacktestEngine.Run(rows, request).Models[0];
        var changed = MilharBacktestEngine.Run(rows.Select(x => x.ExtractionId >= 10 ? x with { Number = "9999" } : x).ToList(), request).Models[0];
        Assert.Equal(original.Trials.Where(x => x.ExtractionId < 10).Select(x => x.Rank), changed.Trials.Where(x => x.ExtractionId < 10).Select(x => x.Rank));
        var target = rows[5];
        var baseline = MilharStatisticsEngine.Rank(rows, new("TEST", "21:00", target.Timestamp));
        var altered = MilharStatisticsEngine.Rank(rows.Select(x => x.ExtractionId >= target.ExtractionId ? x with { Number = "9999" } : x).ToList(), new("TEST", "21:00", target.Timestamp));
        Assert.Equal(baseline.Ranking.Select(x => x.ScoreFinal), altered.Ranking.Select(x => x.ScoreFinal));
    }

    [Fact]
    public void OptimizationNeverChoosesWeightsUsingFinalTest()
    {
        var rows = Series(12);
        var request = BacktestRequest with { CompareModels = false, Optimize = [new() { HalfLife = 10 }, new() { HalfLife = 50 }] };
        var original = MilharBacktestEngine.Run(rows, request);
        var testStart = original.Models[0].Trials.First(x => x.Partition == "test").Date;
        var changed = MilharBacktestEngine.Run(rows.Select(x => x.Timestamp >= testStart ? x with { Number = "9999" } : x).ToList(), request);
        Assert.Equal(original.Winner, changed.Winner);
        Assert.Equal(original.Models[0].Partitions["validation"].Mrr, changed.Models[0].Partitions["validation"].Mrr);
    }

    [Fact]
    public void BaselineAndConfidenceAndMetricsHaveKnownValues()
    {
        Assert.Equal(.01, MilharBacktestEngine.Baseline(100, 1), 12);
        Assert.Equal(.001, MilharBacktestEngine.Baseline(1, 10), 12);
        var ci = MilharBacktestEngine.Wilson(0, 100);
        Assert.Equal(0, ci.Low, 12); Assert.InRange(ci.High, .0369, .0371);
        Assert.Null(MilharBacktestEngine.Metrics([]).Mrr);
        var scores = new MilharScores(1, .5, .2, .3, .4, .5, .6);
        Assert.Equal(.25 + .075 + .03 + .045 + .04 + .05 + .06,
            MilharBacktestEngine.Weighted(scores, new MilharConfiguration().Weights), 12);
    }

    [Fact]
    public void RecencyFavorsRecentObservationsAndSameDayEarlierResultsAreAvailable()
    {
        var response = MilharStatisticsEngine.Rank([Row(1, "1201"), Row(10, "3402", time: "12:00"), Row(10, "9999")],
            Request with { Configuration = new() { Weights = [0, 0, 0, 1, 0, 0, 0], HalfLife = 1 } });
        Assert.Equal(2, response.Sample);
        Assert.True(response.Ranking.Single(x => x.Milhar == "3402").ScoreFinal > response.Ranking.Single(x => x.Milhar == "1201").ScoreFinal);
    }

    [Fact]
    public void PairEvidenceDistinguishesPositionsAndMissingScheduleHasNoInfluence()
    {
        var response = MilharStatisticsEngine.Rank([Row(1, "5684", time: "12:00")], Request);
        var ab = response.Ranking.Single(x => x.Milhar == "5600").Evidence!.Pairs["AB=56"];
        var cd = response.Ranking.Single(x => x.Milhar == "0056").Evidence!.Pairs["CD=56"];
        Assert.True(ab > cd);
        Assert.All(response.Ranking, x => { Assert.Equal(0, x.Scores.Horario); Assert.Equal(0, x.Scores.Transicao); });
        Assert.InRange(response.Ranking.Max(x => x.Scores.Centena), 0, 1d / 1001);
    }

    [Fact]
    public void TransitionsUsePreviousResultOfSameScheduleAndPrize()
    {
        var request = Request with { Configuration = new() { Weights = [0, 0, 0, 0, 0, 0, 1] } };
        var history = new[] { Row(1, "1201"), Row(2, "3402"), Row(3, "1201"), Row(4, "3402"), Row(5, "1201"), Row(6, "9999", time: "12:00"), Row(7, "9999", prize: 2) };
        var result = MilharStatisticsEngine.Rank(history, request);
        Assert.True(result.Ranking.Single(x => x.Milhar == "3402").ScoreFinal > result.Ranking.Single(x => x.Milhar == "3403").ScoreFinal);
        var expected = MilharStatisticsEngine.Rank(history.Take(5).ToArray(), request);
        Assert.Equal(expected.Ranking.Select(x => (x.Milhar, x.ScoreFinal)), result.Ranking.Select(x => (x.Milhar, x.ScoreFinal)));
    }

    [Fact]
    public void ApiRequiresExistingAnalysisPermission()
    {
        var authorization = Assert.Single(typeof(LotteryLab.Api.Controllers.MilharStatisticsController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal(LotteryLab.Api.Security.Permissions.AnalysisUse, authorization.Policy);
    }
}
