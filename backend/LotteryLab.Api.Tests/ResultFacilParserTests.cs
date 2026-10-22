using System.Text;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class ResultFacilParserTests
{
    // A fonte divide o base64 entre o id (sem o último caractere) e data-extends (sem o primeiro).
    private static string Table(int seed)
    {
        var payload = "[" + string.Join(",", Enumerable.Range(1, 10).Select(p => $"\"{p}_{(seed * 100 + p):0000}_{p:00}\"")) + "]";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return $"""<table class="" id="{encoded[..12]}D" data-extends="a{encoded[12..]}"><thead><tr><th>Prêmio</th></tr></thead></table>""";
    }

    private static string Card(string title, int seed) =>
        $"""<h2 class="h4">{title}</h2> <img class="img-reportar-erro" data-a="1" /> <div class="social-media-bar"><span>Compartilhar</span></div> <script>document.write('<' + 'h1>' + t + '</' + 'h1>');</script> {Table(seed)}""";

    private static string Page(params string[] cards) =>
        "<h3>&#128269;Resultado do Jogo do Bicho</h3><form></form>" + string.Concat(cards) + "<h2><strong>Resultado do dia</strong></h2><h3>como jogar?</h3>";

    [Fact]
    public void ParsesNacionalH2TitlesAndSkipsFederal()
    {
        var html = Page(
            Card("A FEDERAL DO BRASIL do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 1),
            Card("Nacional - LN 23:00 do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 2),
            Card("Nacional - LN 02:00 do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 3));
        var extractions = ResultFacilHistoryService.ParseExtractions(html, "LT NACIONAL");
        Assert.Equal(new[] { "23:00", "02:00" }, extractions.Select(x => x.Time));
        Assert.All(extractions, x => Assert.Equal(Enumerable.Range(1, 10), x.Numbers.Select(n => n.Position)));
        Assert.Equal("0201", extractions[0].Numbers[0].Number);
        Assert.Equal("0310", extractions[1].Numbers[9].Number);
    }

    [Fact]
    public void ParsesLookH2TitlesAndSkipsFederal()
    {
        var html = Page(
            Card("FEDERAL do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 1),
            Card("LOOK - GOIÁS, 23h do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 2),
            Card("LOOK - GOIÁS, 07h do 1º ao 10º - Resultado do dia 21/10/2026 (Quarta-feira)", 3));
        var extractions = ResultFacilHistoryService.ParseExtractions(html, "LOOK LOTERIAS");
        Assert.Equal(new[] { "23:00", "07:00" }, extractions.Select(x => x.Time));
    }

    [Fact]
    public void StillParsesLegacyH3Titles()
    {
        var html = $"<h3>Nacional - LN 10:00 do 1º ao 10º</h3>{Table(4)}";
        var extraction = Assert.Single(ResultFacilHistoryService.ParseExtractions(html, "LT NACIONAL"));
        Assert.Equal("10:00", extraction.Time);
    }
}
