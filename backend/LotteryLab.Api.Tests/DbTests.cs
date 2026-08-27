using LotteryLab.Api.Data;
using Npgsql;
using Xunit;

namespace LotteryLab.Api.Tests;

public sealed class DbTests
{
    [Fact]
    public void NormalizeConnectionString_ConvertsNeonUrl()
    {
        var normalized = Db.NormalizeConnectionString(
            "postgresql://sample_user:sample_password@example-pooler.neon.tech/sample_db?sslmode=require");
        var builder = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal("example-pooler.neon.tech", builder.Host);
        Assert.Equal("sample_db", builder.Database);
        Assert.Equal("sample_user", builder.Username);
        Assert.Equal("sample_password", builder.Password);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void NormalizeConnectionString_PreservesNpgsqlFormat()
    {
        const string configured = "Host=localhost;Database=lotterylab;Username=postgres;Password=postgres";
        Assert.Equal(configured, Db.NormalizeConnectionString(configured));
    }
}
