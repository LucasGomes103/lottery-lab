using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using LotteryLab.Api.Data;

namespace LotteryLab.Api.Services;

public sealed class ResultFacilHistoryService(HttpClient http, Db db, PredictionService predictions)
{
    private static readonly Regex Block = new(@"<h3[^>]*>(?<title>.*?)</h3>.*?<table[^>]*id=""(?<id>[^""]+)""[^>]*data-extends=""(?<data>[^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Number = new(@"""(?<position>\d+)_(?<number>\d{4})_\d{2}""", RegexOptions.Compiled);
    private static readonly string[] Animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA", "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO", "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];

    public async Task<object> Sync(string bank, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (end < start) throw new ArgumentException("A data final deve ser igual ou posterior à inicial.");
        if (end.DayNumber - start.DayNumber > 3650) throw new ArgumentException("O intervalo máximo é de 10 anos.");
        var imported = 0; var skipped = 0; var errors = new List<string>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            try { imported += await SyncDate(bank, date, cancellationToken); }
            catch (Exception exception) { skipped++; errors.Add($"{date:dd/MM/yyyy}: {exception.Message}"); }
        }
        return new { bank, start, end, importedExtractions = imported, skippedDays = skipped, errors, source = "https://www.resultadofacil.com.br/" };
    }

    private async Task<int> SyncDate(string bank, DateOnly date, CancellationToken cancellationToken)
    {
        var slug = bank == "LOOK LOTERIAS" ? "resultados-look-loterias" : "resultados-loteria-nacional";
        var url = $"{slug}-do-dia-{date:yyyy-MM-dd}-1ao10";
        var html = await http.GetStringAsync(url, cancellationToken);
        var blocks = Block.Matches(html).Cast<Match>().Select(match => new { match.Groups["title"].Value, Encoded = match.Groups["id"].Value[..^1] + match.Groups["data"].Value[1..] }).ToList();
        await using var connection = db.Open(); var count = 0;
        foreach (var block in blocks)
        {
            var title = Regex.Replace(block.Value, "<.*?>", " ");
            var hour = Regex.Match(title, bank == "LOOK LOTERIAS" ? @"(?<h>\d{2})h" : @"LN\s+(?<h>\d{2}):");
            if (!hour.Success) continue;
            var encoded = Regex.Replace(WebUtility.HtmlDecode(block.Encoded), "[^A-Za-z0-9+/=]", "").TrimEnd('=');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var numbers = Number.Matches(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))).Cast<Match>()
                .Select(x => (Position: int.Parse(x.Groups["position"].Value), Number: x.Groups["number"].Value)).Where(x => x.Position is >= 1 and <= 10).ToList();
            if (numbers.Count != 10) continue;
            var time = $"{hour.Groups["h"].Value}:00";
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            var id = await connection.ExecuteScalarAsync<long?>(@"insert into extractions(bank,extraction_date,extraction_time,source_file)
                values(@bank,@date,@time::time,@source) on conflict(bank,extraction_date,extraction_time) do update set source_file=excluded.source_file returning id",
                new { bank, date = date.ToDateTime(TimeOnly.MinValue), time, source = "RESULTADOFACIL:" + url }, tx) ?? throw new InvalidOperationException("Não foi possível obter a extração.");
            foreach (var row in numbers)
            {
                var dezena = row.Number[^2..]; var centena = row.Number[^3..]; var value = int.Parse(dezena); var group = value == 0 ? 25 : (value + 3) / 4;
                await connection.ExecuteAsync(@"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                    values(@id,@position,@number,@centena,@dezena,@group,@animal)
                    on conflict(extraction_id,position) do update set number=excluded.number,centena=excluded.centena,dezena=excluded.dezena,group_no=excluded.group_no,animal=excluded.animal",
                    new { id, position = row.Position, number = row.Number, centena, dezena, group, animal = Animals[group - 1] }, tx);
            }
            await tx.CommitAsync(cancellationToken); await predictions.EvaluatePending(bank, date, time); count++;
        }
        return count;
    }
}
