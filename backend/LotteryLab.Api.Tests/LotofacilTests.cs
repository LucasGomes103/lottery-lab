using System.Data;
using Dapper;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class LotofacilTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static List<int[]> SyntheticHistory(int count, int seed = 42)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(1, 25).OrderBy(_ => random.Next()).Take(15).Order().ToArray()).ToList();
    }

    private static IEnumerable<int[]> Subsets(int[] source, int size, int start = 0)
    {
        if (size == 0) { yield return []; yield break; }
        for (var i = start; i <= source.Length - size; i++)
            foreach (var tail in Subsets(source, size - 1, i + 1))
                yield return [source[i], .. tail];
    }

    [Fact]
    public void ParsesCaixaJsonFixture()
    {
        var draw = LotofacilResultsService.ParseCaixa(Fixture("lotofacil-caixa-3813.json"));
        Assert.Equal(3813, draw.Contest);
        Assert.Equal(new DateOnly(2026, 10, 24), draw.DrawDate);
        Assert.Equal(new[] { 4, 5, 6, 8, 10, 12, 13, 14, 15, 16, 17, 20, 22, 23, 25 }, draw.Numbers);
        Assert.True(draw.Accumulated);
        Assert.Equal(0, draw.Winners15);
        Assert.Equal(217, draw.Winners14);
        Assert.Equal(2278.37m, draw.Prize14);
        Assert.Equal(7335, draw.Winners13);
        Assert.Equal(35m, draw.Prize13);
        Assert.Equal(90378, draw.Winners12);
        Assert.Equal(14m, draw.Prize12);
        Assert.Equal(487151, draw.Winners11);
        Assert.Equal(7m, draw.Prize11);
        Assert.Equal(5_000_000m, draw.NextEstimate);
        Assert.Equal(LotofacilResultsService.CaixaUrl, draw.Source);
    }

    [Fact]
    public void ParsesFallbackBulkFixture()
    {
        using var document = System.Text.Json.JsonDocument.Parse(Fixture("lotofacil-fallback-1-3.json"));
        var draws = document.RootElement.EnumerateArray().Select(LotofacilResultsService.ParseFallback).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, draws.Select(x => x.Contest));
        var first = draws[0];
        Assert.Equal(new DateOnly(2003, 9, 29), first.DrawDate);
        Assert.Equal(new[] { 2, 3, 5, 6, 9, 10, 11, 13, 14, 16, 18, 20, 23, 24, 25 }, first.Numbers);
        Assert.Equal(5, first.Winners15);
        Assert.Equal(49765.82m, first.Prize15);
        Assert.Equal(2m, first.Prize11);
        Assert.False(first.Accumulated);
        Assert.All(draws, x => Assert.Equal(15, x.Numbers.Length));
    }

    [Fact]
    public void ParsesFallbackJsonWithScientificNotation()
    {
        const string json = """
            {"loteria":"lotofacil","concurso":3813,"data":"24/10/2026",
             "dezenas":["04","05","06","08","10","12","13","14","15","16","17","20","22","23","25"],
             "premiacoes":[{"faixa":1,"ganhadores":0,"valorPremio":0.0},{"faixa":2,"ganhadores":217,"valorPremio":2278.37},
             {"faixa":3,"ganhadores":7335,"valorPremio":35.0},{"faixa":4,"ganhadores":90378,"valorPremio":14.0},
             {"faixa":5,"ganhadores":487151,"valorPremio":7.0}],"acumulou":true,"valorEstimadoProximoConcurso":5.0E6}
            """;
        var draw = LotofacilResultsService.ParseFallback(json);
        Assert.Equal(3813, draw.Contest);
        Assert.Equal(487151, draw.Winners11);
        Assert.Equal(5_000_000m, draw.NextEstimate);
        Assert.Equal(LotofacilResultsService.FallbackUrl, draw.Source);
    }

    [Fact]
    public void RejectsResponseWithoutFifteenNumbers()
    {
        const string json = """{"concurso":1,"data":"29/09/2003","dezenas":["01","02","03"]}""";
        Assert.Throws<InvalidDataException>(() => LotofacilResultsService.ParseFallback(json));
    }

    [Theory]
    [InlineData(15, 3.50)]
    [InlineData(16, 56)]
    [InlineData(17, 476)]
    [InlineData(18, 2856)]
    [InlineData(19, 13566)]
    [InlineData(20, 54264)]
    public void PriceTableMatchesCaixa(int numbers, double price) =>
        Assert.Equal((decimal)price, LotofacilService.GameCost(numbers));

    [Fact]
    public void SixteenNumbersWithFifteenHitsYieldsOneFifteenAndFifteenFourteens()
    {
        var drawn = Enumerable.Range(1, 15).ToArray();
        var prize = LotofacilService.Evaluate([.. drawn, 16], drawn);
        Assert.Equal(15, prize.Hits);
        Assert.Equal(1, prize.Count15);
        Assert.Equal(15, prize.Count14);
        Assert.Equal(0, prize.Count13);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(20)]
    public void CombinatorialPrizeCountsMatchBruteForce(int size)
    {
        var random = new Random(size);
        for (var round = 0; round < 6; round++)
        {
            var drawn = Enumerable.Range(1, 25).OrderBy(_ => random.Next()).Take(15).ToHashSet();
            var game = Enumerable.Range(1, 25).OrderBy(_ => random.Next()).Take(size).Order().ToArray();
            var expected = new long[16];
            foreach (var bet in Subsets(game, 15)) expected[bet.Count(drawn.Contains)]++;
            var prize = LotofacilService.Evaluate(game, drawn.ToArray());
            Assert.Equal(expected[15], prize.Count15);
            Assert.Equal(expected[14], prize.Count14);
            Assert.Equal(expected[13], prize.Count13);
            Assert.Equal(expected[12], prize.Count12);
            Assert.Equal(expected[11], prize.Count11);
            Assert.Equal(LotofacilService.Combinations(size, 15), expected.Sum());
        }
    }

    [Fact]
    public void ReturnUsesRateioAndFallsBackToFixedPrizes()
    {
        var drawn = Enumerable.Range(1, 15).ToArray();
        var prize = LotofacilService.Evaluate([.. drawn, 16], drawn); // 1 × 15 e 15 × 14
        Assert.Equal(1_000_000m + 15 * 2_000m, LotofacilService.ReturnAmount(prize, 1_000_000m, 2_000m, 35m, 14m, 7m));
        var eleven = LotofacilService.Evaluate([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 16, 17, 18, 19], drawn);
        Assert.Equal(1, eleven.Count11);
        Assert.Equal(7m, LotofacilService.ReturnAmount(eleven, 0, 0, 0, 0, 0));
        Assert.Equal(2m, LotofacilService.ReturnAmount(eleven, 0, 0, 10m, 4m, 2m));
    }

    [Fact]
    public void ScoringAndGenerationAreDeterministicForFixedSeed()
    {
        var history = SyntheticHistory(300);
        var first = LotofacilService.ScoreNumbers(history, 100);
        var second = LotofacilService.ScoreNumbers(history, 100);
        Assert.Equal(25, first.Count);
        Assert.Equal(first.Select(x => (x.Number, x.Score)), second.Select(x => (x.Number, x.Score)));
        Assert.All(first, x => Assert.InRange(x.Score, 0, 1));
        var a = LotofacilService.BuildGames(first, 10, 15, 1234);
        var b = LotofacilService.BuildGames(second, 10, 15, 1234);
        Assert.Equal(a.Games.Select(x => string.Join(',', x.Numbers)), b.Games.Select(x => string.Join(',', x.Numbers)));
    }

    [Theory]
    [InlineData(15, 10)]
    [InlineData(15, 50)]
    [InlineData(16, 20)]
    [InlineData(18, 30)]
    [InlineData(20, 50)]
    public void GeneratedGamesRespectConstraints(int numbersPerGame, int quantity)
    {
        var scores = LotofacilService.ScoreNumbers(SyntheticHistory(500, numbersPerGame), 100);
        var result = LotofacilService.BuildGames(scores, quantity, numbersPerGame, 99);
        Assert.Equal(quantity, result.Games.Count);
        Assert.All(result.Games, game =>
        {
            Assert.Equal(numbersPerGame, game.Numbers.Length);
            Assert.Equal(numbersPerGame, game.Numbers.Distinct().Count());
            Assert.All(game.Numbers, n => Assert.InRange(n, 1, 25));
            Assert.Equal(game.Numbers.Order(), game.Numbers);
        });
        Assert.Equal(quantity, result.Games.Select(x => string.Join(',', x.Numbers)).Distinct().Count());
        for (var i = 0; i < result.Games.Count; i++)
            for (var j = i + 1; j < result.Games.Count; j++)
                Assert.True(result.Games[i].Numbers.Intersect(result.Games[j].Numbers).Count() <= result.MaxSharedUsed);
        Assert.InRange(result.MaxSharedUsed, result.BaseMaxShared, numbersPerGame - 1);
        if (!result.BalanceRelaxed) Assert.All(result.Games, game => Assert.True(LotofacilService.IsBalanced(game.Numbers)));
    }

    [Fact]
    public void SmallBatchKeepsBaseOverlapAndBalance()
    {
        var scores = LotofacilService.ScoreNumbers(SyntheticHistory(500), 100);
        var result = LotofacilService.BuildGames(scores, 5, 15, 7);
        Assert.Equal(10, result.BaseMaxShared);
        Assert.Equal(result.BaseMaxShared, result.MaxSharedUsed);
        Assert.False(result.BalanceRelaxed);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, false)] // soma 120, muito baixa
    [InlineData(new[] { 1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 2, 4 }, false)] // só 2 pares
    [InlineData(new[] { 2, 3, 5, 6, 9, 10, 11, 13, 14, 16, 18, 20, 23, 24, 25 }, true)] // concurso 1
    public void BalanceChecksParityFrameAndSum(int[] game, bool balanced) =>
        Assert.Equal(balanced, LotofacilService.IsBalanced(game));

    [Fact]
    public void FrameHasSixteenBorderNumbers()
    {
        Assert.Equal(16, LotofacilService.Frame.Count);
        Assert.DoesNotContain(13, LotofacilService.Frame);
        Assert.Equal(9, Enumerable.Range(1, 25).Count(n => !LotofacilService.Frame.Contains(n)));
    }

    [Fact]
    public void RejectsInvalidGenerationRequests()
    {
        var scores = LotofacilService.ScoreNumbers(SyntheticHistory(50), 20);
        Assert.Throws<ArgumentException>(() => LotofacilService.BuildGames(scores, 1, 14, 1));
        Assert.Throws<ArgumentException>(() => LotofacilService.BuildGames(scores, 1, 21, 1));
        Assert.Throws<ArgumentException>(() => LotofacilService.BuildGames(scores, 0, 15, 1));
    }

    [Fact]
    public void DrawRowMaterializesFromArrayColumn()
    {
        var table = new DataTable();
        table.Columns.Add("contest", typeof(int));
        table.Columns.Add("numbers", typeof(Array));
        table.Columns.Add("prize11", typeof(decimal));
        table.Rows.Add(3813, new short[] { 4, 5, 6, 8, 10, 12, 13, 14, 15, 16, 17, 20, 22, 23, 25 }, 7m);
        using var reader = table.CreateDataReader();
        var row = reader.Parse<LotofacilService.DrawRow>().Single();
        Assert.Equal(3813, row.Contest);
        Assert.Equal(15, row.Numbers.Length);
        Assert.Equal(7m, row.Prize11);
    }
}
