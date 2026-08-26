using LotteryLab.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LotteryLab.Api.Tests;

public sealed class PdfImportServiceTests
{
    private readonly PdfImportService service = new(new ConfigurationBuilder().Build(), NullLogger<PdfImportService>.Instance);

    [Fact]
    public void ParseText_SeparatesSchedulesAndPreservesNumberWidths()
    {
        const string text = """
            RESULTADOS
            26/08/2026
            > LT NACIONAL 15HS
            1: 0.110 G.03 BURRO
            2: 0.478 G.20 PERU
            3: 7.198 G.25 VACA
            4: 5.294 G.24 VEADO
            5: 1.370 G.18 PORCO
            6: 4.450 G.13 GALO
            7: 052 G.13 GALO
            > LT NACIONAL 17HS
            1: 1.286 G.22 TIGRE
            2: 0.060 G.15 JACARE
            3: 2.017 G.05 CACHORRO
            4: 5.580 G.20 PERU
            5: 5.584 G.21 TOURO
            6: 4.527 G.07 CARNEIRO
            7: 077 G.20 PERU
            """;

        var extractions = service.ParseText(text);

        Assert.Equal(2, extractions.Count);
        Assert.All(extractions, extraction => Assert.Equal(7, extraction.Results.Count));
        Assert.Equal("15:00", extractions[0].Time);
        Assert.Equal(new DateOnly(2026, 8, 26), extractions[0].Date);
        Assert.Equal("0110", extractions[0].Results[0].Number);
        Assert.Equal("052", extractions[0].Results[6].Number);
        Assert.Null(extractions[0].Results[6].Milhar);
        Assert.Equal("0060", extractions[1].Results[1].Number);
        Assert.Equal("060", extractions[1].Results[1].Centena);
        Assert.Equal("60", extractions[1].Results[1].Dezena);
    }
}
