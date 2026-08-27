using Npgsql;

namespace LotteryLab.Api.Data;

public sealed class Db
{
    private readonly string connectionString;

    public Db(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default ausente");
        connectionString = NormalizeConnectionString(configured);
    }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static string NormalizeConnectionString(string configured)
    {
        var value = configured.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("A URL de conexão do PostgreSQL é inválida.");

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2)
            throw new InvalidOperationException("A URL de conexão do PostgreSQL não contém usuário e senha.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = Uri.UnescapeDataString(userInfo[1]),
            SslMode = SslMode.Require
        };

        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = Uri.UnescapeDataString(pair[0]);
            var queryValue = Uri.UnescapeDataString(pair[1]);
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<SslMode>(queryValue, true, out var sslMode))
                builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }
}
