using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LotteryLab.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class ApiController(Db db, PdfImportService pdf, AnalysisService analysis, AiService ai) : ControllerBase
{
    [HttpPost("imports/preview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Preview(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Selecione um PDF não vazio." });
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Somente arquivos PDF são aceitos." });
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await pdf.ParseAsync(stream, Path.GetFileName(file.FileName), cancellationToken));
        }
        catch (InvalidDataException exception) { return BadRequest(new { message = exception.Message }); }
        catch (InvalidOperationException exception) { return UnprocessableEntity(new { message = exception.Message }); }
    }

    [HttpPost("imports/commit")]
    public async Task<IActionResult> Commit(ImportPreview preview)
    {
        var validationErrors = Validate(preview);
        if (validationErrors.Count > 0) return BadRequest(new { message = "Revise os dados antes de importar.", errors = validationErrors });

        await using var connection = db.Open();
        var keys = preview.Extractions.Select(x => new { x.Bank, Date = x.Date!.Value, Time = x.Time! }).ToList();
        var duplicates = new List<object>();
        foreach (var key in keys)
        {
            var exists = await connection.ExecuteScalarAsync<bool>(
                "select exists(select 1 from extractions where bank=@Bank and extraction_date=@Date and extraction_time=@Time::time)", key);
            if (exists) duplicates.Add(new { key.Bank, key.Date, key.Time });
        }
        if (duplicates.Count > 0) return Conflict(new { message = "Uma ou mais extrações já foram importadas.", duplicates });

        await using var transaction = await connection.BeginTransactionAsync();
        var imported = new List<object>();
        foreach (var extraction in preview.Extractions)
        {
            var id = await connection.ExecuteScalarAsync<long>(
                @"insert into extractions(bank,extraction_date,extraction_time,source_file)
                  values(@Bank,@Date,@Time::time,@FileName) returning id",
                new { extraction.Bank, Date = extraction.Date!.Value, extraction.Time, preview.FileName }, transaction);

            foreach (var result in extraction.Results)
            {
                var normalized = NormalizeResult(result);
                await connection.ExecuteAsync(
                    @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                      values(@id,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                    new { id, normalized.Position, normalized.Number, normalized.Centena, normalized.Dezena, Group = normalized.Group, normalized.Animal }, transaction);
            }
            imported.Add(new { id, extraction.Bank, extraction.Date, extraction.Time, count = extraction.Results.Count });
        }
        await transaction.CommitAsync();
        return Ok(new { count = imported.Count, extractions = imported });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(string bank = "LT NACIONAL", int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        await using var connection = db.Open();
        return Ok(await connection.QueryAsync(@"select e.id,e.bank,e.extraction_date,e.extraction_time,count(r.id) results
            from extractions e left join results r on r.extraction_id=e.id where e.bank=@bank
            group by e.id order by e.extraction_date desc,e.extraction_time desc limit @take", new { bank, take }));
    }

    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast(string bank = "LT NACIONAL", string time = "21:00", int windowDays = 15, int top = 8) =>
        Ok(await analysis.Forecast(bank, time, Math.Clamp(windowDays, 1, 3650), Math.Clamp(top, 1, 100)));

    [HttpGet("backtest")]
    public async Task<IActionResult> Backtest(string bank = "LT NACIONAL", string time = "21:00", int windowDays = 15, int top = 8) =>
        Ok(await analysis.Backtest(bank, time, Math.Clamp(windowDays, 1, 3650), Math.Clamp(top, 1, 100)));

    [HttpPost("ai/analyze")]
    public async Task<IActionResult> Ai(AiRequest request)
    {
        var windowDays = Math.Clamp(request.WindowDays, 1, 3650);
        var forecast = await analysis.Forecast(request.Bank, request.Time, windowDays, 8);
        var backtest = await analysis.Backtest(request.Bank, request.Time, windowDays, 8);
        return Ok(new { answer = await ai.Ask(new { forecast, backtest }, request.Question) });
    }

    private static List<string> Validate(ImportPreview preview)
    {
        var errors = new List<string>();
        if (preview.Extractions.Count == 0) errors.Add("Nenhuma extração foi informada.");
        foreach (var extraction in preview.Extractions)
        {
            var label = $"{extraction.Bank} {extraction.Date} {extraction.Time}";
            if (string.IsNullOrWhiteSpace(extraction.Bank)) errors.Add($"{label}: banca ausente.");
            if (extraction.Date is null) errors.Add($"{label}: data ausente.");
            if (!TimeOnly.TryParse(extraction.Time, out _)) errors.Add($"{label}: horário inválido.");
            if (extraction.Results.Count != 7) errors.Add($"{label}: são necessários exatamente sete resultados.");
            if (extraction.Results.Select(x => x.Position).Distinct().Count() != extraction.Results.Count) errors.Add($"{label}: posições repetidas.");
            foreach (var result in extraction.Results)
            {
                var digits = new string((result.Number ?? "").Where(char.IsDigit).ToArray());
                var length = result.Position == 7 ? 3 : 4;
                if (result.Position is < 1 or > 7 || digits.Length != length)
                    errors.Add($"{label}: resultado da posição {result.Position} deve possuir {length} dígitos.");
            }
        }
        return errors;
    }

    private static ParsedResult NormalizeResult(ParsedResult result)
    {
        var length = result.Position == 7 ? 3 : 4;
        var number = new string(result.Number.Where(char.IsDigit).ToArray()).PadLeft(length, '0')[^length..];
        var dezena = number[^2..];
        var centena = number[^3..];
        var value = int.Parse(dezena);
        var group = value == 0 ? 25 : (value + 3) / 4;
        string[] animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA", "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO", "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];
        return new ParsedResult(result.Position, number, result.Position == 7 ? null : number, centena, dezena, group, animals[group - 1]);
    }
}
