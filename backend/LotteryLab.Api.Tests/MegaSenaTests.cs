using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class MegaSenaTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static List<int[]> SyntheticHistory(int count, int seed = 42)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(1, 60).OrderBy(_ => random.Next()).Take(6).Order().ToArray()).ToList();
    }

    [Fact]
    public void ParsesCaixaJsonFixture()
    {
        var draw = MegaSenaResultsService.ParseCaixa(Fixture("megasena-caixa-3072.json"));
        Assert.Equal(3072, draw.Contest);
        Assert.Equal(new DateOnly(2026, 10, 20), draw.DrawDate);
        Assert.Equal(new[] { 2, 6, 9, 16, 31, 40 }, draw.Numbers);
        Assert.True(draw.Accumulated);
        Assert.Equal(0, draw.SenaWinners);
        Assert.Equal(140, draw.QuinaWinners);
        Assert.Equal(38295.72m, draw.QuinaPrize);
        Assert.Equal(9261, draw.QuadraWinners);
        Assert.Equal(954.26m, draw.QuadraPrize);
        Assert.NotNull(draw.NextEstimate);
        Assert.Equal(MegaSenaResultsService.CaixaUrl, draw.Source);
    }

    [Fact]
    public void ParsesFallbackJsonWithScientificNotation()
    {
        const string json = """
            {"loteria":"megasena","concurso":1,"data":"11/03/1996","dezenas":["04","05","30","33","41","52"],
             "premiacoes":[{"faixa":1,"ganhadores":0,"valorPremio":0.0},{"faixa":2,"ganhadores":17,"valorPremio":39158.92},
             {"faixa":3,"ganhadores":2016,"valorPremio":330.21}],"acumulou":true,"valorEstimadoProximoConcurso":2.0E8}
            """;
        var draw = MegaSenaResultsService.ParseFallback(json);
        Assert.Equal(1, draw.Contest);
        Assert.Equal(new[] { 4, 5, 30, 33, 41, 52 }, draw.Numbers);
        Assert.Equal(2016, draw.QuadraWinners);
        Assert.Equal(330.21m, draw.QuadraPrize);
        Assert.Equal(200_000_000m, draw.NextEstimate);
    }

    [Fact]
    public void ScoringAndGenerationAreDeterministicForFixedSeed()
    {
        var history = SyntheticHistory(300);
        var first = MegaSenaService.ScoreNumbers(history, 100);
        var second = MegaSenaService.ScoreNumbers(history, 100);
        Assert.Equal(60, first.Count);
        Assert.Equal(first.Select(x => (x.Number, x.Score)), second.Select(x => (x.Number, x.Score)));
        Assert.All(first, x => Assert.InRange(x.Score, 0, 1));

        var gamesA = MegaSenaService.BuildGames(first, 10, 6, 12345).Games;
        var gamesB = MegaSenaService.BuildGames(second, 10, 6, 12345).Games;
        Assert.Equal(gamesA.Select(x => string.Join('-', x.Numbers)), gamesB.Select(x => string.Join('-', x.Numbers)));
        var gamesC = MegaSenaService.BuildGames(first, 10, 6, 54321).Games;
        Assert.NotEqual(gamesA.Select(x => string.Join('-', x.Numbers)), gamesC.Select(x => string.Join('-', x.Numbers)));
    }

    [Fact]
    public void ScoringUsesWindowFrequency()
    {
        // A dezena 7 sai em todos os concursos recentes; deve liderar a frequência da janela.
        var history = SyntheticHistory(200).Select((draw, i) => i < 150 ? draw
            : draw.Contains(7) ? draw : draw.Take(5).Append(7).Distinct().Order().ToArray()).ToList();
        var scores = MegaSenaService.ScoreNumbers(history, 50);
        var seven = scores.Single(x => x.Number == 7);
        Assert.Equal(1.0, seven.Features.WindowFrequency);
        Assert.Equal(0, seven.DelayContests);
        Assert.Equal(7, scores[0].Number);
    }

    [Theory]
    [InlineData(6, 20)]
    [InlineData(8, 15)]
    [InlineData(15, 50)]
    public void GamesRespectConstraints(int numbersPerGame, int quantity)
    {
        var scores = MegaSenaService.ScoreNumbers(SyntheticHistory(300), 100);
        var result = MegaSenaService.BuildGames(scores, quantity, numbersPerGame, 99);
        Assert.Equal(quantity, result.Games.Count);
        Assert.Equal(Enumerable.Range(1, quantity), result.Games.Select(x => x.Rank));
        Assert.All(result.Games, game =>
        {
            Assert.Equal(numbersPerGame, game.Numbers.Length);
            Assert.Equal(numbersPerGame, game.Numbers.Distinct().Count());
            Assert.All(game.Numbers, n => Assert.InRange(n, 1, 60));
            Assert.Equal(game.Numbers.Order(), game.Numbers);
        });
        Assert.Equal(quantity, result.Games.Select(x => string.Join('-', x.Numbers)).Distinct().Count());
        for (var i = 0; i < result.Games.Count; i++)
        for (var j = i + 1; j < result.Games.Count; j++)
            Assert.True(result.Games[i].Numbers.Intersect(result.Games[j].Numbers).Count() <= result.MaxSharedUsed);
        if (!result.BalanceRelaxed) Assert.All(result.Games, game => Assert.True(MegaSenaService.IsBalanced(game.Numbers)));
    }

    [Fact]
    public void SixNumberGamesKeepStrictOverlapLimit()
    {
        var scores = MegaSenaService.ScoreNumbers(SyntheticHistory(300), 100);
        var result = MegaSenaService.BuildGames(scores, 10, 6, 7);
        Assert.Equal(3, result.MaxSharedUsed);
        Assert.False(result.BalanceRelaxed);
    }

    [Fact]
    public void RejectsInvalidParameters()
    {
        var scores = MegaSenaService.ScoreNumbers(SyntheticHistory(50), 20);
        Assert.Throws<ArgumentException>(() => MegaSenaService.BuildGames(scores, 0, 6, 1));
        Assert.Throws<ArgumentException>(() => MegaSenaService.BuildGames(scores, 51, 6, 1));
        Assert.Throws<ArgumentException>(() => MegaSenaService.BuildGames(scores, 5, 5, 1));
        Assert.Throws<ArgumentException>(() => MegaSenaService.BuildGames(scores, 5, 16, 1));
    }

    [Fact]
    public void EvaluatesSixNumberGame()
    {
        int[] drawn = [2, 6, 9, 16, 31, 40];
        var quadra = MegaSenaService.Evaluate([2, 6, 9, 16, 50, 51], drawn);
        Assert.Equal(new MegaSenaPrizeSummary(4, 0, 0, 1), quadra);
        var quina = MegaSenaService.Evaluate([2, 6, 9, 16, 31, 51], drawn);
        Assert.Equal(new MegaSenaPrizeSummary(5, 0, 1, 0), quina);
        var sena = MegaSenaService.Evaluate(drawn, drawn);
        Assert.Equal(new MegaSenaPrizeSummary(6, 1, 0, 0), sena);
        Assert.Equal(new MegaSenaPrizeSummary(3, 0, 0, 0), MegaSenaService.Evaluate([2, 6, 9, 50, 51, 52], drawn));
        Assert.Equal(954.26m, MegaSenaService.ReturnAmount(quadra, 0m, 38295.72m, 954.26m));
        Assert.Equal(38295.72m, MegaSenaService.ReturnAmount(quina, 0m, 38295.72m, 954.26m));
    }

    [Fact]
    public void EvaluatesEightNumberGameAsEquivalentCombinations()
    {
        int[] drawn = [2, 6, 9, 16, 31, 40];
        // 8 dezenas com 4 acertos: C(4,4)·C(4,2) = 6 quadras.
        var four = MegaSenaService.Evaluate([2, 6, 9, 16, 50, 51, 52, 53], drawn);
        Assert.Equal(new MegaSenaPrizeSummary(4, 0, 0, 6), four);
        // 5 acertos: 3 quinas (C(5,5)·C(3,1)) e 15 quadras (C(5,4)·C(3,2)).
        var five = MegaSenaService.Evaluate([2, 6, 9, 16, 31, 50, 51, 52], drawn);
        Assert.Equal(new MegaSenaPrizeSummary(5, 0, 3, 15), five);
        // 6 acertos: 1 sena, C(6,5)·C(2,1) = 12 quinas e C(6,4)·C(2,2) = 15 quadras.
        var six = MegaSenaService.Evaluate([2, 6, 9, 16, 31, 40, 50, 51], drawn);
        Assert.Equal(new MegaSenaPrizeSummary(6, 1, 12, 15), six);
        Assert.Equal(1_000_000m + 12 * 40_000m + 15 * 1_000m, MegaSenaService.ReturnAmount(six, 1_000_000m, 40_000m, 1_000m));
        Assert.Equal(3 * 38295.72m + 15 * 954.26m, MegaSenaService.ReturnAmount(five, 0m, 38295.72m, 954.26m));
    }

    [Theory]
    [InlineData(6, 1, 6.00)]
    [InlineData(7, 7, 42.00)]
    [InlineData(8, 28, 168.00)]
    [InlineData(10, 210, 1260.00)]
    [InlineData(15, 5005, 30030.00)]
    public void ComputesCombinationCost(int numbers, long bets, decimal cost)
    {
        Assert.Equal(bets, MegaSenaService.Combinations(numbers, 6));
        Assert.Equal(cost, MegaSenaService.GameCost(numbers));
    }

    [Fact]
    public void HypergeometricExpectationMatchesFormula()
    {
        Assert.Equal(0.6, MegaSenaService.ExpectedHits(6), 10);
        Assert.Equal(1.5, MegaSenaService.ExpectedHits(15), 10);
        Assert.Equal(50_063_860, MegaSenaService.Combinations(60, 6));
    }

}
