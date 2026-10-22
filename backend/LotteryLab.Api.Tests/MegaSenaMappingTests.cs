using System.Data;
using Dapper;
using LotteryLab.Api.Services;
using Xunit;

namespace LotteryLab.Api.Tests;

// O Npgsql informa colunas smallint[] com GetFieldType = System.Array. Uma DataTable com esse tipo de coluna
// reproduz a materialização do Dapper sem precisar de banco.
public class MegaSenaMappingTests
{
    private sealed record PositionalDraw(int Contest, short[] Numbers);

    private static DataTable DrawTable()
    {
        var table = new DataTable();
        table.Columns.Add("contest", typeof(int));
        table.Columns.Add("drawdate", typeof(DateTime));
        table.Columns.Add("numbers", typeof(Array));
        table.Columns.Add("senawinners", typeof(int));
        table.Columns.Add("senaprize", typeof(decimal));
        table.Columns.Add("quinawinners", typeof(int));
        table.Columns.Add("quinaprize", typeof(decimal));
        table.Columns.Add("quadrawinners", typeof(int));
        table.Columns.Add("quadraprize", typeof(decimal));
        table.Columns.Add("accumulated", typeof(bool));
        table.Columns.Add("nextestimate", typeof(decimal));
        table.Columns.Add("source", typeof(string));
        table.Columns.Add("importedat", typeof(DateTime));
        table.Rows.Add(3072, new DateTime(2026, 10, 20), new short[] { 2, 6, 9, 16, 31, 40 }, 0, 0m, 140, 38295.72m,
            9261, 954.26m, true, DBNull.Value, MegaSenaResultsService.FallbackUrl, DateTime.UtcNow);
        return table;
    }

    [Fact]
    public void PositionalRecordCannotBeMaterializedFromArrayColumn()
    {
        var table = new DataTable();
        table.Columns.Add("contest", typeof(int));
        table.Columns.Add("numbers", typeof(Array));
        table.Rows.Add(1, new short[] { 4, 5, 30, 33, 41, 52 });
        using var reader = table.CreateDataReader();
        Assert.Throws<InvalidOperationException>(() => reader.Parse<PositionalDraw>().ToList());
    }

    [Fact]
    public void MapsDrawRowFromArrayColumnAndNullableEstimate()
    {
        using var reader = DrawTable().CreateDataReader();
        var row = reader.Parse<MegaSenaService.DrawRow>().Single();
        Assert.Equal(3072, row.Contest);
        Assert.Equal(new short[] { 2, 6, 9, 16, 31, 40 }, row.Numbers);
        Assert.Equal(new DateTime(2026, 10, 20), row.DrawDate);
        Assert.Equal(954.26m, row.QuadraPrize);
        Assert.True(row.Accumulated);
        Assert.Null(row.NextEstimate);
    }

    [Fact]
    public void MapsNumbersAndGameRows()
    {
        var numbers = new DataTable();
        numbers.Columns.Add("contest", typeof(int));
        numbers.Columns.Add("numbers", typeof(Array));
        numbers.Rows.Add(1, new short[] { 4, 5, 30, 33, 41, 52 });
        using (var reader = numbers.CreateDataReader())
            Assert.Equal(new short[] { 4, 5, 30, 33, 41, 52 }, reader.Parse<MegaSenaService.DrawNumbersRow>().Single().Numbers);

        var games = new DataTable();
        games.Columns.Add("predictionid", typeof(Guid));
        games.Columns.Add("rank", typeof(int));
        games.Columns.Add("numbers", typeof(Array));
        var id = Guid.NewGuid();
        games.Rows.Add(id, 1, new short[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using (var reader = games.CreateDataReader())
        {
            var game = reader.Parse<MegaSenaService.GameRow>().Single();
            Assert.Equal(id, game.PredictionId);
            Assert.Equal(8, game.Numbers.Length);
        }
    }

    [Fact]
    public void MapsPredictionRow()
    {
        var table = new DataTable();
        foreach (var (name, type) in new[] { ("id", typeof(Guid)), ("targetcontest", typeof(int)), ("generatedat", typeof(DateTime)),
                     ("seed", typeof(long)), ("windowcontests", typeof(int)), ("numberspergame", typeof(int)), ("quantity", typeof(int)),
                     ("totalcost", typeof(decimal)), ("configjson", typeof(string)), ("scoresjson", typeof(string)), ("status", typeof(string)) })
            table.Columns.Add(name, type);
        table.Rows.Add(Guid.NewGuid(), 3073, DateTime.UtcNow, 123L, 100, 8, 5, 840m, "{}", "[]", "PENDING");
        using var reader = table.CreateDataReader();
        var row = reader.Parse<MegaSenaService.PredictionRow>().Single();
        Assert.Equal(3073, row.TargetContest);
        Assert.Equal(840m, row.TotalCost);
        Assert.Equal(123L, row.Seed);
    }
}
