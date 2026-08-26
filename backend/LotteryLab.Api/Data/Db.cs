using Npgsql;
namespace LotteryLab.Api.Data;
public sealed class Db(IConfiguration config) {
  private readonly string _cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default ausente");
  public NpgsqlConnection Open() { var c = new NpgsqlConnection(_cs); c.Open(); return c; }
}
