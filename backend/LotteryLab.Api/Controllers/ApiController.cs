using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LotteryLab.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class ApiController(Db db, PdfImportService pdf, AnalysisService analysis, AiService ai,
    NumberGeneratorService generator, PredictionService predictions, ExternalResultsService externalResults,
    RioExternalResultsService rioExternalResults) : ControllerBase
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

    [HttpPost("imports/sync")]
    public async Task<IActionResult> SyncExternalResults(DateOnly? date, string bank = "LT NACIONAL", CancellationToken cancellationToken = default)
    {
        var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        if (target > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)))
            return BadRequest(new { message = "Não é possível sincronizar uma data futura." });
        var normalizedBank = bank.Trim().ToUpperInvariant();
        if (normalizedBank is not ("LT NACIONAL" or "PT RIO"))
            return BadRequest(new { message = "Banca inválida. Escolha LT NACIONAL ou PT RIO." });
        return normalizedBank == RioExternalResultsService.Bank
            ? Ok(await rioExternalResults.Sync(target, cancellationToken))
            : Ok(await externalResults.Sync(target, cancellationToken));
    }

    [HttpGet("imports/sync/status")]
    public IActionResult ExternalSyncStatus() => Ok(new { national = externalResults.Status(), rio = rioExternalResults.Status() });

    [HttpPost("imports/commit")]
    public async Task<IActionResult> Commit(ImportPreview preview)
    {
        var validationErrors = Validate(preview);
        if (validationErrors.Count > 0) return BadRequest(new { message = "Revise os dados antes de importar.", errors = validationErrors });

        await using var connection = db.Open();
        var keys = preview.Extractions.Select(x => new
        {
            x.Bank,
            Date = x.Date!.Value.ToDateTime(TimeOnly.MinValue),
            Time = x.Time!
        }).ToList();
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var exists = await connection.ExecuteScalarAsync<bool>(
                "select exists(select 1 from extractions where bank=@Bank and extraction_date=@Date and extraction_time=@Time::time)", key);
            if (exists) existingKeys.Add(ExtractionKey(key.Bank, DateOnly.FromDateTime(key.Date), key.Time));
        }

        await using var transaction = await connection.BeginTransactionAsync();
        var imported = new List<object>();
        var insertedCount = 0;
        var updatedCount = 0;
        foreach (var extraction in preview.Extractions)
        {
            var wasExisting = existingKeys.Contains(ExtractionKey(extraction.Bank, extraction.Date!.Value, extraction.Time!));
            var id = await connection.ExecuteScalarAsync<long>(
                @"insert into extractions(bank,extraction_date,extraction_time,source_file)
                  values(@Bank,@Date,@Time::time,@FileName)
                  on conflict(bank,extraction_date,extraction_time) do update
                  set source_file=excluded.source_file, imported_at=now()
                  returning id",
                new
                {
                    extraction.Bank,
                    Date = extraction.Date!.Value.ToDateTime(TimeOnly.MinValue),
                    extraction.Time,
                    preview.FileName
                }, transaction);

            if (wasExisting)
                await connection.ExecuteAsync("delete from results where extraction_id=@id", new { id }, transaction);
            foreach (var result in extraction.Results)
            {
                var normalized = NormalizeResult(result);
                await connection.ExecuteAsync(
                    @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                      values(@id,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                    new { id, normalized.Position, normalized.Number, normalized.Centena, normalized.Dezena, Group = normalized.Group, normalized.Animal }, transaction);
            }
            if (wasExisting) updatedCount++; else insertedCount++;
            imported.Add(new { id, extraction.Bank, extraction.Date, extraction.Time, count = extraction.Results.Count, action = wasExisting ? "updated" : "inserted" });
        }
        await transaction.CommitAsync();
        foreach (var extraction in preview.Extractions)
            await predictions.EvaluatePending(extraction.Bank, extraction.Date!.Value, extraction.Time!);
        return Ok(new
        {
            count = imported.Count,
            insertedCount,
            updatedCount,
            message = $"{insertedCount} novas extrações inseridas e {updatedCount} existentes atualizadas.",
            extractions = imported
        });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(
        string? bank = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        string? time = null,
        int page = 1,
        int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(bank)) { where.Add("e.bank ilike @Bank"); parameters.Add("Bank", $"%{bank.Trim()}%"); }
        if (startDate is not null) { where.Add("e.extraction_date >= @StartDate"); parameters.Add("StartDate", startDate.Value.ToDateTime(TimeOnly.MinValue)); }
        if (endDate is not null) { where.Add("e.extraction_date <= @EndDate"); parameters.Add("EndDate", endDate.Value.ToDateTime(TimeOnly.MinValue)); }
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TimeOnly.TryParse(time, out _)) return BadRequest(new { message = "Horário de filtro inválido." });
            where.Add("e.extraction_time = @Time::time"); parameters.Add("Time", time);
        }
        var whereSql = where.Count == 0 ? "" : "where " + string.Join(" and ", where);
        parameters.Add("Offset", (page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);

        await using var connection = db.Open();
        var total = await connection.ExecuteScalarAsync<long>($"select count(*) from extractions e {whereSql}", parameters);
        var items = await connection.QueryAsync($@"select e.id,e.bank,e.extraction_date,e.extraction_time,count(r.id) results
            from extractions e left join results r on r.extraction_id=e.id {whereSql}
            group by e.id order by e.extraction_date desc,e.extraction_time desc
            offset @Offset limit @PageSize", parameters);
        return Ok(new { items, total, page, pageSize, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) });
    }

    [HttpGet("history/{id:long}")]
    public async Task<IActionResult> HistoryDetail(long id)
    {
        await using var connection = db.Open();
        var extraction = await connection.QuerySingleOrDefaultAsync<(long Id, string Bank, DateTime Date, TimeSpan Time)>(
            @"select id, bank, extraction_date as Date, extraction_time as Time
              from extractions where id=@id", new { id });
        if (extraction.Id == 0) return NotFound(new { message = "Extração não encontrada." });

        var results = (await connection.QueryAsync<ParsedResult>(
            @"select position, number,
                     case when position=7 then null else number end as milhar,
                     centena, dezena, group_no as ""Group"", animal
              from results where extraction_id=@id order by position", new { id })).ToList();
        return Ok(new ParsedExtraction(
            DateOnly.FromDateTime(extraction.Date), extraction.Bank,
            $"{extraction.Time.Hours:00}:{extraction.Time.Minutes:00}", results, []));
    }

    [HttpPut("history/{id:long}")]
    public async Task<IActionResult> UpdateHistory(long id, ParsedExtraction extraction)
    {
        var preview = new ImportPreview("edicao-manual", $"edit-{id}", false, [extraction], []);
        var validationErrors = Validate(preview);
        if (validationErrors.Count > 0) return BadRequest(new { message = "Revise os dados antes de salvar.", errors = validationErrors });

        await using var connection = db.Open();
        var exists = await connection.ExecuteScalarAsync<bool>("select exists(select 1 from extractions where id=@id)", new { id });
        if (!exists) return NotFound(new { message = "Extração não encontrada." });

        var duplicate = await connection.ExecuteScalarAsync<bool>(
            @"select exists(select 1 from extractions
              where bank=@Bank and extraction_date=@Date and extraction_time=@Time::time and id<>@id)",
            new
            {
                id,
                extraction.Bank,
                Date = extraction.Date!.Value.ToDateTime(TimeOnly.MinValue),
                extraction.Time
            });
        if (duplicate) return Conflict(new { message = "Já existe outra extração para esta banca, data e horário." });

        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"update extractions set bank=@Bank, extraction_date=@Date, extraction_time=@Time::time where id=@id",
            new
            {
                id,
                extraction.Bank,
                Date = extraction.Date!.Value.ToDateTime(TimeOnly.MinValue),
                extraction.Time
            }, transaction);
        await connection.ExecuteAsync("delete from results where extraction_id=@id", new { id }, transaction);
        foreach (var result in extraction.Results)
        {
            var normalized = NormalizeResult(result);
            await connection.ExecuteAsync(
                @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                  values(@id,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                new { id, normalized.Position, normalized.Number, normalized.Centena, normalized.Dezena, Group = normalized.Group, normalized.Animal }, transaction);
        }
        await transaction.CommitAsync();
        await predictions.EvaluatePending(extraction.Bank, extraction.Date!.Value, extraction.Time!);
        return Ok(new { id, message = "Extração atualizada com sucesso." });
    }

    [HttpPut("history/batch")]
    public async Task<IActionResult> UpdateHistoryBatch(BatchHistoryUpdate request)
    {
        if (request.Items.Count == 0) return BadRequest(new { message = "Selecione ao menos uma extração." });
        if (request.Items.Select(x => x.Id).Distinct().Count() != request.Items.Count)
            return BadRequest(new { message = "A seleção contém extrações repetidas." });

        var errors = request.Items.SelectMany(item =>
            Validate(new ImportPreview("edicao-em-lote", $"edit-{item.Id}", false, [item.Extraction], []))
                .Select(error => $"Extração {item.Id}: {error}")).ToList();
        if (errors.Count > 0) return BadRequest(new { message = "Revise os dados antes de salvar.", errors });

        await using var connection = db.Open();
        foreach (var item in request.Items)
        {
            var exists = await connection.ExecuteScalarAsync<bool>("select exists(select 1 from extractions where id=@Id)", new { item.Id });
            if (!exists) return NotFound(new { message = $"Extração {item.Id} não encontrada." });
            var duplicate = await connection.ExecuteScalarAsync<bool>(
                @"select exists(select 1 from extractions
                  where bank=@Bank and extraction_date=@Date and extraction_time=@Time::time and id<>@Id)",
                new
                {
                    item.Id,
                    item.Extraction.Bank,
                    Date = item.Extraction.Date!.Value.ToDateTime(TimeOnly.MinValue),
                    item.Extraction.Time
                });
            if (duplicate) return Conflict(new { message = $"Já existe outra extração para {item.Extraction.Bank}, {item.Extraction.Date} e {item.Extraction.Time}." });
        }

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var item in request.Items)
        {
            await connection.ExecuteAsync(
                @"update extractions set bank=@Bank, extraction_date=@Date, extraction_time=@Time::time where id=@Id",
                new
                {
                    item.Id,
                    item.Extraction.Bank,
                    Date = item.Extraction.Date!.Value.ToDateTime(TimeOnly.MinValue),
                    item.Extraction.Time
                }, transaction);
            await connection.ExecuteAsync("delete from results where extraction_id=@Id", new { item.Id }, transaction);
            foreach (var result in item.Extraction.Results)
            {
                var normalized = NormalizeResult(result);
                await connection.ExecuteAsync(
                    @"insert into results(extraction_id,position,number,centena,dezena,group_no,animal)
                      values(@Id,@Position,@Number,@Centena,@Dezena,@Group,@Animal)",
                    new { item.Id, normalized.Position, normalized.Number, normalized.Centena, normalized.Dezena, Group = normalized.Group, normalized.Animal }, transaction);
            }
        }
        await transaction.CommitAsync();
        foreach (var item in request.Items)
            await predictions.EvaluatePending(item.Extraction.Bank, item.Extraction.Date!.Value, item.Extraction.Time!);
        return Ok(new { count = request.Items.Count, message = "Extrações atualizadas com sucesso." });
    }

    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast(string bank = "LT NACIONAL", string time = "21:00", int windowDays = 15, int top = 8) =>
        Ok(await analysis.Forecast(bank, time, Math.Clamp(windowDays, 1, 3650), Math.Clamp(top, 1, 100)));

    [HttpGet("generator")]
    public async Task<IActionResult> GenerateNumbers(
        string bank = "LT NACIONAL",
        string time = "21:00",
        DateOnly? targetDate = null,
        int windowDays = 90,
        int quantity = 10)
    {
        if (string.IsNullOrWhiteSpace(bank)) return BadRequest(new { message = "Informe a banca." });
        if (!TimeOnly.TryParse(time, out _)) return BadRequest(new { message = "Horário inválido." });
        var date = targetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        return Ok(await generator.Generate(bank.Trim(), time, date, Math.Clamp(windowDays, 7, 3650), Math.Clamp(quantity, 1, 100)));
    }

    [HttpPost("predictions/generate")]
    public async Task<IActionResult> GeneratePrediction(PredictionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Bank)) return BadRequest(new { message = "Informe a banca." });
        if (!TimeOnly.TryParse(request.Time, out _)) return BadRequest(new { message = "Horário inválido." });
        return Ok(await predictions.GenerateAndSave(request));
    }

    [HttpGet("predictions/animal-trends")]
    public async Task<IActionResult> AnimalTrends(string bank = "LT NACIONAL", string time = "21:00",
        DateOnly? targetDate = null, int windowDays = 90)
    {
        if (string.IsNullOrWhiteSpace(bank)) return BadRequest(new { message = "Informe a banca." });
        if (!TimeOnly.TryParse(time, out _)) return BadRequest(new { message = "Horário inválido." });
        var date = targetDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        return Ok(await predictions.AnimalTrends(bank.Trim(), time, date, Math.Clamp(windowDays, 7, 3650)));
    }

    [HttpGet("predictions")]
    public async Task<IActionResult> Predictions(string? bank = null, DateOnly? targetDate = null,
        string? time = null, string? status = null, int page = 1, int pageSize = 20)
    {
        if (!string.IsNullOrWhiteSpace(time) && !TimeOnly.TryParse(time, out _))
            return BadRequest(new { message = "Horário de filtro inválido." });
        if (!string.IsNullOrWhiteSpace(status) && status is not ("PENDING" or "EVALUATED"))
            return BadRequest(new { message = "Situação de filtro inválida." });
        return Ok(await predictions.List(bank, targetDate, time, status, page, pageSize));
    }

    [HttpGet("predictions/{id:guid}")]
    public async Task<IActionResult> Prediction(Guid id)
    {
        var result = await predictions.Detail(id);
        return result is null ? NotFound(new { message = "Previsão não encontrada." }) : Ok(result);
    }

    [HttpGet("predictions/statistics")]
    public async Task<IActionResult> PredictionStatistics(string? bank = null, DateOnly? startDate = null,
        DateOnly? endDate = null, string? time = null)
    {
        if (!string.IsNullOrWhiteSpace(time) && !TimeOnly.TryParse(time, out _))
            return BadRequest(new { message = "Horário de filtro inválido." });
        return Ok(await predictions.Statistics(bank, startDate, endDate, time));
    }

    [HttpGet("predictions/window-backtest")]
    public async Task<IActionResult> PredictionWindowBacktest(string bank = "LT NACIONAL", DateOnly? date = null,
        int quantity = 28, string windows = "30,60,90,120,180,240", bool useSameDayResults = false)
    {
        if (string.IsNullOrWhiteSpace(bank)) return BadRequest(new { message = "Informe a banca." });
        var parsedWindows = windows.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var value) ? value : 0).ToArray();
        var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        return Ok(await predictions.CompareWindows(bank.Trim(), target, quantity, parsedWindows, useSameDayResults));
    }

    [HttpPost("predictions/{id:guid}/evaluate")]
    public async Task<IActionResult> EvaluatePrediction(Guid id)
    {
        var result = await predictions.Evaluate(id);
        return result is null ? NotFound(new { message = "Previsão não encontrada." }) : Ok(result);
    }

    [HttpDelete("predictions/{id:guid}")]
    public async Task<IActionResult> DeletePrediction(Guid id)
    {
        var count = await predictions.Delete([id]);
        return count == 0 ? NotFound(new { message = "Previsão não encontrada." }) : Ok(new { count, message = "Previsão excluída." });
    }

    [HttpPost("predictions/delete-batch")]
    public async Task<IActionResult> DeletePredictions(PredictionDeleteRequest request)
    {
        if (request.Ids.Count == 0) return BadRequest(new { message = "Selecione ao menos uma previsão." });
        var count = await predictions.Delete(request.Ids);
        return Ok(new { count, message = $"{count} previsões excluídas." });
    }

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
            var expectedResults = extraction.Bank.Trim().Equals(RioExternalResultsService.Bank, StringComparison.OrdinalIgnoreCase) ? 5 : 7;
            if (extraction.Results.Count != expectedResults)
                errors.Add($"{label}: são necessários exatamente {expectedResults} resultados.");
            if (extraction.Results.Select(x => x.Position).Distinct().Count() != extraction.Results.Count) errors.Add($"{label}: posições repetidas.");
            foreach (var result in extraction.Results)
            {
                var digits = new string((result.Number ?? "").Where(char.IsDigit).ToArray());
                var length = result.Position == 7 ? 3 : 4;
                if (result.Position < 1 || result.Position > expectedResults || digits.Length != length)
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

    private static string ExtractionKey(string bank, DateOnly date, string time) =>
        $"{bank.Trim()}|{date:yyyy-MM-dd}|{TimeOnly.Parse(time):HH:mm}";
}
