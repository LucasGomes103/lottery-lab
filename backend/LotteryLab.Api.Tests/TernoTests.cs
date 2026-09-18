using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class TernoTests
{
    [Fact]
    public void GeneratesExactQuantityWithoutRepeatedDezenasOrPermutations()
    {
        var scores = Enumerable.Range(0, 12).ToDictionary(x => x.ToString("00"), x => (double)x);
        var games = PredictionService.BuildTernos(scores, 150);
        Assert.Equal(150, games.Count);
        Assert.Equal(150, games.Select(x => string.Join('-', x.Dezenas)).Distinct().Count());
        Assert.All(games, game => Assert.Equal(3, game.Dezenas.Distinct().Count()));
        Assert.Contains(games, game => game.Dezenas.Contains("00"));
        Assert.Equal(new[] { "09", "10", "11" }, games[0].Dezenas);
        Assert.Equal(games.Select(x => x.Score).OrderDescending(), games.Select(x => x.Score));
    }

    [Fact]
    public void RejectsQuantityAboveAnimalCombinationCapacity()
    {
        var scores = Enumerable.Range(1, 4).ToDictionary(x => x.ToString("00"), _ => 1d);
        Assert.Equal(4, PredictionService.BuildTernos(scores, 4).Count);
        Assert.Throws<ArgumentException>(() => PredictionService.BuildTernos(scores, 5));
    }

    [Fact]
    public void MatchesAllThreeInAnyOrderIncludingZeroAndLeadingZero()
    {
        var game = new TernoGame(1, ["00", "03", "99"], 1);
        var results = new[] { (1, "4599"), (2, "1203"), (3, "8811"), (4, "5100"), (5, "2299") };
        var check = PredictionService.CheckTerno(game, results, 1, 5);
        Assert.True(check.Hit);
        Assert.Equal(3, check.MatchedDezenas);
    }

    [Fact]
    public void DoesNotCountSixthPrizeOrDuplicateResultsAsAdditionalDezenas()
    {
        var game = new TernoGame(1, ["00", "03", "99"], 1);
        var results = new[] { (1, "4500"), (2, "1203"), (3, "8800"), (4, "5103"), (5, "2203"), (6, "9999") };
        var check = PredictionService.CheckTerno(game, results, 1, 5);
        Assert.False(check.Hit);
        Assert.Equal(2, check.MatchedDezenas);
    }

    [Fact]
    public void PayoutUses13000TimesStakePerGameWithoutDividingByFive()
    {
        Assert.Equal(13000m, PredictionService.TernoPayout(10m, 10));
        Assert.Equal(1300m, PredictionService.TernoPayout(15m, 150));
        Assert.Equal(4333.33m, PredictionService.TernoPayout(1m, 3));
    }
}
