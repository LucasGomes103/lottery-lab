using LotteryLab.Api.Security;
using Xunit;

namespace LotteryLab.Api.Tests;

public sealed class PasswordServiceTests
{
    [Fact]
    public void HashUsesSaltAndVerifiesOnlyCorrectPassword()
    {
        var service = new PasswordService();
        const string password = "SenhaForte@2026";
        var first = service.Hash(password);
        var second = service.Hash(password);

        Assert.NotEqual(first, second);
        Assert.True(service.Verify(password, first));
        Assert.False(service.Verify("SenhaErrada@2026", first));
    }

    [Theory]
    [InlineData("curta", false)]
    [InlineData("somenteletrasminusculas", false)]
    [InlineData("SenhaForte@2026", true)]
    public void PasswordPolicyIsEnforced(string password, bool valid) =>
        Assert.Equal(valid, PasswordService.Validate(password) is null);
}
