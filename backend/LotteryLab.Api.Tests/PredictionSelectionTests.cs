using System.Collections;
using System.Reflection;
using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

public class PredictionSelectionTests
{
    [Theory]
    [InlineData(150, 3, true)]
    [InlineData(151, 3, true)]
    [InlineData(400, 1, true)]
    [InlineData(1200, 3, true)]
    [InlineData(150, 25, false)]
    public void SelectionFillsQuantityWithUniqueThousandsAndBalancedSelectedAnimals(int quantity, int groupCount, bool restricted)
    {
        var scoredType = typeof(PredictionService).GetNestedType("Scored", BindingFlags.NonPublic)!;
        var ranked = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(scoredType))!;
        var features = new PredictionFeatures(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        for (var group = 1; group <= groupCount; group++)
        for (var prefix = 0; prefix < 100; prefix++)
        for (var ending = (group - 1) * 4 + 1; ending <= group * 4; ending++)
        {
            var number = $"{prefix:00}{ending % 100:00}";
            ranked.Add(Activator.CreateInstance(scoredType, number, number[^3..], number[^2..], group,
                1d, 1d, features, new List<string>()));
        }
        var select = typeof(PredictionService).GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Static)!;
        var selected = (List<PredictionCandidate>)select.Invoke(null, [ranked, quantity, 42L, restricted])!;
        Assert.Equal(quantity, selected.Count);
        Assert.Equal(quantity, selected.Select(x => x.Milhar).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, quantity), selected.Select(x => x.Rank));
        if (restricted)
            foreach (var group in Enumerable.Range(1, groupCount))
                Assert.Equal(quantity / groupCount + (group <= quantity % groupCount ? 1 : 0), selected.Count(x => x.Group == group));
    }
}
