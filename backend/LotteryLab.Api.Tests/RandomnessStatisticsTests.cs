using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class RandomnessStatisticsTests
{
    [Theory]
    [InlineData(36.415, 24, 0.05)]
    [InlineData(16.919, 9, 0.05)]
    [InlineData(123.225, 99, 0.05)]
    [InlineData(42.980, 24, 0.01)]
    [InlineData(3.841, 1, 0.05)]
    [InlineData(9.0, 9, 0.4373)]
    public void ChiSquarePValueMatchesTables(double statistic, int degreesOfFreedom, double expected) =>
        Assert.Equal(expected, RandomnessService.ChiSquarePValue(statistic, degreesOfFreedom), 3);

    [Theory]
    [InlineData(1.96, 0.05)]
    [InlineData(2.241, 0.025)]
    [InlineData(2.576, 0.01)]
    [InlineData(0, 1)]
    [InlineData(-1.96, 0.05)]
    public void TwoSidedPValueMatchesNormalTable(double z, double expected) =>
        Assert.Equal(expected, RandomnessService.TwoSidedP(z), 3);

    [Fact]
    public void LogGammaMatchesFactorials()
    {
        Assert.Equal(Math.Log(24), RandomnessService.LogGamma(5), 10);
        Assert.Equal(0.5 * Math.Log(Math.PI), RandomnessService.LogGamma(0.5), 10);
    }
}
