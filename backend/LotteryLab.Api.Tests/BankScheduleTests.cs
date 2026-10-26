using System.Reflection;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class BankScheduleTests
{
    private static string[] Schedules(string bank) => (string[])typeof(PredictionService)
        .GetMethod("BankSchedules", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bank])!;

    [Theory]
    [InlineData("LT NACIONAL", "02:00", "23:00")]
    [InlineData("LOOK LOTERIAS", "07:00", "23:00")]
    [InlineData(" look loterias ", "07:00", "23:00")]
    public void EachBankHasEightOrderedDistinctTimes(string bank, string first, string last)
    {
        var schedules = Schedules(bank);
        Assert.Equal(8, schedules.Length);
        Assert.Equal(schedules.Order(StringComparer.Ordinal), schedules);
        Assert.Equal(8, schedules.Distinct().Count());
        Assert.Equal(first, schedules[0]);
        Assert.Equal(last, schedules[^1]);
        Assert.All(schedules, time => Assert.True(TimeOnly.TryParse(time, out _)));
    }
}
