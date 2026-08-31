using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public sealed class RioExternalResultsServiceTests
{
    [Fact]
    public void Parse_UsesOnlyDeuNoPosteAndNormalizesSchedules()
    {
        const string html = """
            <script type="application/ld+json">
            {"@graph":[{"@type":"Dataset","temporalCoverage":"2026-08-30","variableMeasured":[
              {"name":"A FEDERAL DO BRASIL - RJ 19:00 — 1º prêmio","value":"8932 · Grupo 08 · Camelo"},
              {"name":"DEU NO POSTE - RJ, 14:20, PT 14:20 — 1º prêmio","value":"6171 · Grupo 18 · Porco"},
              {"name":"DEU NO POSTE - RJ, 14:20, PT 14:20 — 2º prêmio","value":"8221 · Grupo 06 · Cabra"},
              {"name":"DEU NO POSTE - RJ, 14:20, PT 14:20 — 3º prêmio","value":"0398 · Grupo 25 · Vaca"},
              {"name":"DEU NO POSTE - RJ, 14:20, PT 14:20 — 4º prêmio","value":"0698 · Grupo 25 · Vaca"},
              {"name":"DEU NO POSTE - RJ, 14:20, PT 14:20 — 5º prêmio","value":"7206 · Grupo 02 · Águia"}] }]}
            </script>
            """;

        var results = RioExternalResultsService.Parse(html, new DateOnly(2026, 8, 30));

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal("14:00", result.Time));
        Assert.Equal([1, 2, 3, 4, 5], results.Select(result => result.Position));
        Assert.Equal("0398", results[2].Number);
    }
}
