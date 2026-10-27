using System.Net;
using LotteryLab.Api.Data;
using LotteryLab.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LotteryLab.Api.Tests;

public class LatestResultSourcesTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Responde por URL exata; qualquer outra URL recebe 403, como a Caixa faz com o Render.
    private sealed class FakeHandler(Dictionary<string, (HttpStatusCode Status, string Body)> responses) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body) = responses.TryGetValue(url, out var response) ? response : (HttpStatusCode.Forbidden, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static Db Db() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost" }).Build());

    private static LotofacilResultsService Lotofacil(FakeHandler handler) =>
        new(new HttpClient(handler), Db(), new LotofacilSyncState(), NullLogger<LotofacilResultsService>.Instance);

    private static MegaSenaResultsService MegaSena(FakeHandler handler) =>
        new(new HttpClient(handler), Db(), new MegaSenaSyncState(), NullLogger<MegaSenaResultsService>.Instance);

    private static string Lotofacil3813Fallback() => Fixture("lotofacil-fallback-3814.json").Replace("\"concurso\":3814", "\"concurso\":3813");

    [Fact]
    public void ParsesMegaSenaResultadoFacilPage()
    {
        var draw = MegaSenaResultsService.ParseResultadoFacil(Fixture("megasena-resultadofacil-3075.html"));
        Assert.Equal(3075, draw.Contest);
        Assert.Equal(new DateOnly(2026, 10, 26), draw.DrawDate);
        Assert.Equal(new[] { 7, 10, 14, 27, 36, 55 }, draw.Numbers);
        Assert.Equal(0, draw.SenaWinners);
        Assert.Equal(0m, draw.SenaPrize);
        Assert.Equal(182, draw.QuinaWinners);
        Assert.Equal(19433.12m, draw.QuinaPrize);
        Assert.Equal(11018, draw.QuadraWinners);
        Assert.Equal(458.57m, draw.QuadraPrize);
        Assert.True(draw.Accumulated);
        Assert.Equal(305_000_000m, draw.NextEstimate);
        Assert.Equal(MegaSenaResultsService.ResultadoFacilUrl, draw.Source);
        Assert.True(MegaSenaResultsService.IsComplete(draw));
    }

    [Fact]
    public void ParsesLotofacilResultadoFacilPage()
    {
        var draw = LotofacilResultsService.ParseResultadoFacil(Fixture("lotofacil-resultadofacil-3814.html"));
        Assert.Equal(3814, draw.Contest);
        Assert.Equal(new DateOnly(2026, 10, 26), draw.DrawDate);
        Assert.Equal(new[] { 1, 4, 6, 8, 9, 12, 13, 15, 16, 17, 18, 21, 22, 24, 25 }, draw.Numbers);
        Assert.Equal(2, draw.Winners15);
        Assert.Equal(2134251.70m, draw.Prize15);
        Assert.Equal(389, draw.Winners14);
        Assert.Equal(2015.87m, draw.Prize14);
        Assert.Equal(12337, draw.Winners13);
        Assert.Equal(134112, draw.Winners12);
        Assert.Equal(709025, draw.Winners11);
        Assert.Equal(7m, draw.Prize11);
        Assert.False(draw.Accumulated);
        Assert.Null(draw.NextEstimate);
        Assert.Equal(LotofacilResultsService.ResultadoFacilUrl, draw.Source);
    }

    [Fact]
    public void ResultadoFacilPageWithoutResultIsRejected() =>
        Assert.Throws<InvalidDataException>(() => MegaSenaResultsService.ParseResultadoFacil("<html><body>Página não encontrada</body></html>"));

    [Fact]
    public void DrawWithoutPrizeBreakdownIsIncomplete()
    {
        var draw = MegaSenaResultsService.ParseCaixa(Fixture("megasena-caixa-3075.json"));
        Assert.True(MegaSenaResultsService.IsComplete(draw));
        Assert.False(MegaSenaResultsService.IsComplete(draw with { QuadraWinners = 0 }));
        Assert.False(MegaSenaResultsService.IsComplete(draw with { Numbers = [1, 2, 3, 4, 5] }));
    }

    [Fact]
    public async Task UsesNextContestFromCaixaWhenLatestLags()
    {
        var latest = Fixture("megasena-caixa-3075.json").Replace("\"numero\": 3075", "\"numero\": 3074");
        var handler = new FakeHandler(new()
        {
            [MegaSenaResultsService.CaixaUrl] = (HttpStatusCode.OK, latest),
            [$"{MegaSenaResultsService.CaixaUrl}/3075"] = (HttpStatusCode.OK, Fixture("megasena-caixa-3075.json"))
        });
        var draw = await MegaSena(handler).FetchNewest();
        Assert.Equal(3075, draw.Contest);
        Assert.Equal(MegaSenaResultsService.CaixaUrl, draw.Source);
    }

    [Fact]
    public async Task UsesNextContestFromFallbackWhenCaixaIsBlocked()
    {
        var handler = new FakeHandler(new()
        {
            [$"{LotofacilResultsService.FallbackUrl}/latest"] = (HttpStatusCode.OK, Lotofacil3813Fallback()),
            [$"{LotofacilResultsService.FallbackUrl}/3814"] = (HttpStatusCode.OK, Fixture("lotofacil-fallback-3814.json"))
        });
        var draw = await Lotofacil(handler).FetchNewest();
        Assert.Equal(3814, draw.Contest);
        Assert.Equal(LotofacilResultsService.FallbackUrl, draw.Source);
        // Depois da recusa no "último", a Caixa não é consultada de novo no mesmo ciclo.
        Assert.DoesNotContain($"{LotofacilResultsService.CaixaUrl}/3814", handler.Requested);
    }

    [Fact]
    public async Task UsesResultadoFacilWhenApisAreStale()
    {
        var handler = new FakeHandler(new()
        {
            [$"{LotofacilResultsService.FallbackUrl}/latest"] = (HttpStatusCode.OK, Lotofacil3813Fallback()),
            [$"{LotofacilResultsService.FallbackUrl}/3814"] = (HttpStatusCode.NotFound, "{\"status\":404}"),
            [LotofacilResultsService.ResultadoFacilUrl] = (HttpStatusCode.OK, Fixture("lotofacil-resultadofacil-3814.html"))
        });
        var draw = await Lotofacil(handler).FetchNewest();
        Assert.Equal(3814, draw.Contest);
        Assert.Equal(LotofacilResultsService.ResultadoFacilUrl, draw.Source);
    }

    [Fact]
    public async Task UsesResultadoFacilWhenApisAreDown()
    {
        var handler = new FakeHandler(new()
        {
            [LotofacilResultsService.ResultadoFacilUrl] = (HttpStatusCode.OK, Fixture("lotofacil-resultadofacil-3814.html"))
        });
        var draw = await Lotofacil(handler).FetchNewest();
        Assert.Equal(3814, draw.Contest);
        Assert.Equal(LotofacilResultsService.ResultadoFacilUrl, draw.Source);
    }

    [Fact]
    public async Task KeepsLatestWhenNoSourceHasANewerContest()
    {
        var handler = new FakeHandler(new()
        {
            [$"{LotofacilResultsService.FallbackUrl}/latest"] = (HttpStatusCode.OK, Fixture("lotofacil-fallback-3814.json")),
            [$"{LotofacilResultsService.FallbackUrl}/3815"] = (HttpStatusCode.NotFound, "{\"status\":404}"),
            [LotofacilResultsService.ResultadoFacilUrl] = (HttpStatusCode.OK, Fixture("lotofacil-resultadofacil-3814.html"))
        });
        var draw = await Lotofacil(handler).FetchNewest();
        Assert.Equal(3814, draw.Contest);
        Assert.Equal(LotofacilResultsService.FallbackUrl, draw.Source);
    }

    [Fact]
    public async Task FailsWithOriginalErrorWhenEverySourceIsDown()
    {
        var handler = new FakeHandler([]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Lotofacil(handler).FetchNewest());
    }
}
