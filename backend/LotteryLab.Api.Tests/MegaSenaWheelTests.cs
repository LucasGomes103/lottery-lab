using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class MegaSenaWheelTests
{
    private readonly MegaSenaWheelService _service = new();

    [Theory]
    [InlineData(7, "4se4")]
    [InlineData(10, "4se4")]
    [InlineData(12, "4se4")]
    [InlineData(10, "4se5")]
    [InlineData(15, "4se5")]
    [InlineData(10, "5se5")]
    [InlineData(10, "4se6")]
    public void Fechamento_garante_o_premio(int quantidade, string garantia)
    {
        var dezenas = Enumerable.Range(1, quantidade).Select(i => i * 3).ToArray();
        var result = _service.Build(dezenas, garantia);

        Assert.All(result.Games, g =>
        {
            Assert.Equal(6, g.Distinct().Count());
            Assert.All(g, n => Assert.Contains(n, dezenas));
        });
        Assert.True(MegaSenaWheelService.VerifyGuarantee(result.Numbers, result.Games, result.Guarantee));
        Assert.True(result.Games.Count < result.FullCombinations);
    }

    [Fact]
    public void Dez_dezenas_quadra_se_sair_4_fica_perto_do_otimo()
    {
        // O melhor fechamento conhecido para 10 dezenas / quadra se sair 4 tem 20 jogos.
        var result = _service.Build(Enumerable.Range(1, 10), "4se4");
        Assert.InRange(result.Games.Count, 20, 24);
    }

    [Fact]
    public void Mesma_semente_gera_os_mesmos_jogos()
    {
        var a = _service.Build(Enumerable.Range(1, 11), "4se4", seed: 42);
        var b = _service.Build(Enumerable.Range(1, 11), "4se4", seed: 42);
        Assert.Equal(a.Games.Select(g => string.Join(",", g)), b.Games.Select(g => string.Join(",", g)));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(21)]
    public void Rejeita_quantidade_fora_do_limite(int quantidade)
    {
        Assert.Throws<ArgumentException>(() => _service.Build(Enumerable.Range(1, quantidade), "4se4"));
    }

    [Fact]
    public void Rejeita_dezena_invalida()
    {
        Assert.Throws<ArgumentException>(() => _service.Build(new[] { 0, 1, 2, 3, 4, 5, 6 }, "4se4"));
    }
}
