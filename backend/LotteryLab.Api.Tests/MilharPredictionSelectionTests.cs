using System.Text.Json;
using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class MilharPredictionSelectionTests
{
    private static readonly DateTime Cutoff = new(2026, 1, 20, 21, 0, 0);
    private static MilharObservation[] History => Enumerable.Range(1, 10).Select(i =>
        new MilharObservation(i, Cutoff.AddDays(-i), "TEST", "21:00", 1, (i * 317).ToString("D4"), null)).ToArray();

    [Fact]
    public void GenerationIsExactlyTheNewComposedRankingWithoutExploration()
    {
        var config = new MilharConfiguration();
        var expected = MilharStatisticsEngine.Rank(History, new("TEST", "21:00", Cutoff, null, 100, "C", config));
        var actual = MilharPredictionSelection.Generate(History, "TEST", Cutoff, 100, 5, [], config);
        Assert.Equal(expected.Ranking.Select(x => x.Milhar), actual.Select(x => x.Milhar));
        Assert.All(actual, x => Assert.Equal("STATISTICAL", x.SelectionType));
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected.Ranking[i].ScoreFinal * 100, actual[i].FinalScore, 10);
            Assert.Equal(expected.Ranking[i].Scores, actual[i].Features.Statistics);
            Assert.Equal(i + 1, actual[i].Features.StatisticalRank);
        }
    }

    [Fact]
    public void SelectedAnimalsKeepBalancedQuotasAndDescendingScores()
    {
        var results = MilharPredictionSelection.Generate(History, "TEST", Cutoff, 151, 5, [1, 2, 3], new());
        Assert.Equal(151, results.Count);
        Assert.Equal(151, results.Select(x => x.Milhar).Distinct().Count());
        Assert.Equal(51, results.Count(x => x.Group == 1));
        Assert.Equal(50, results.Count(x => x.Group == 2));
        Assert.Equal(50, results.Count(x => x.Group == 3));
        Assert.Equal(results.Select(x => x.FinalScore).OrderDescending(), results.Select(x => x.FinalScore));
    }

    [Fact]
    public void GenerationExcludesTargetFutureAndUnselectedPrizes()
    {
        var history = History;
        var extra = new[] { history[0] with { Timestamp = Cutoff, Number = "9999" },
            history[0] with { Timestamp = Cutoff.AddDays(1), Number = "9999" },
            history[0] with { Position = 6, Number = "9999" } };
        var expected = MilharPredictionSelection.Generate(history, "TEST", Cutoff, 20, 5, [], new());
        var actual = MilharPredictionSelection.Generate([.. history, .. extra], "TEST", Cutoff, 20, 5, [], new());
        Assert.Equal(expected.Select(x => (x.Milhar, x.FinalScore)), actual.Select(x => (x.Milhar, x.FinalScore)));
    }

    [Fact]
    public void StoredFeaturesRoundTripAndLegacyPayloadStillDeserializes()
    {
        var candidate = MilharPredictionSelection.Generate(History, "TEST", Cutoff, 1, 5, [], new())[0];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(candidate.Features, JsonSerializer.Deserialize<PredictionFeatures>(JsonSerializer.Serialize(candidate.Features, options), options));
        var legacy = JsonSerializer.Deserialize<PredictionFeatures>("{\"frequency\":0.5,\"timeFrequency\":0.4}", options)!;
        Assert.Equal(.5, legacy.Frequency); Assert.Null(legacy.Statistics);
    }
}
