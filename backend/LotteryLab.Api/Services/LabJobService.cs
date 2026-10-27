using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

/// <summary>
/// Executa o Laboratório em segundo plano, uma execução por vez (o plano gratuito do Render tem pouca CPU),
/// e grava parâmetros e resultado em lab_runs. O histórico é lido uma única vez por execução, só leitura.
/// </summary>
public sealed class LabJobService(Db db, ILogger<LabJobService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Banks = ["LT NACIONAL", "LOOK LOTERIAS"];
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    private sealed class Job
    {
        public Guid Id { get; init; }
        public required string Game { get; init; }
        public required object Parameters { get; init; }
        public string Status { get; set; } = "QUEUED";
        public string Stage { get; set; } = "Na fila";
        public int Completed { get; set; }
        public int Total { get; set; }
        public object? Result { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
    }

    private sealed class RunRow
    {
        public Guid Id { get; set; }
        public string Game { get; set; } = "";
        public string Status { get; set; } = "";
        public string Parameters { get; set; } = "{}";
        public string? Result { get; set; }
        public string? Error { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Verdict { get; set; }
    }

    public async Task<object> Start(LabRunRequest request, string? user)
    {
        var normalized = Normalize(request);
        var job = new Job { Id = Guid.NewGuid(), Game = normalized.Game, Parameters = normalized };
        await using (var connection = db.Open())
            await connection.ExecuteAsync(
                "insert into lab_runs(id,game,status,parameters,created_by) values(@Id,@Game,'QUEUED',@parameters::jsonb,@user)",
                new { job.Id, job.Game, parameters = JsonSerializer.Serialize(normalized, JsonOptions), user });
        jobs[job.Id] = job;
        foreach (var old in jobs.Values.Where(x => x.FinishedAt is not null).OrderByDescending(x => x.CreatedAt).Skip(20)) jobs.TryRemove(old.Id, out _);
        _ = Task.Run(() => Execute(job, normalized));
        return Snapshot(job);
    }

    private async Task Execute(Job job, LabRunRequest request)
    {
        await gate.WaitAsync();
        try
        {
            job.Status = "RUNNING"; job.Stage = "Carregando histórico";
            await SetStatus(job.Id, "RUNNING");
            job.Result = await Run(request, (completed, total) =>
            {
                job.Stage = "Testando estratégias"; job.Completed = completed; job.Total = total;
            });
            job.Status = "COMPLETED"; job.Stage = "Concluído";
        }
        catch (ArgumentException error) { job.Status = "FAILED"; job.Error = error.Message; }
        catch (Exception error)
        {
            job.Status = "FAILED"; job.Error = "Não foi possível concluir o laboratório. Tente novamente.";
            logger.LogError(error, "Falha no laboratório {Id}.", job.Id);
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            gate.Release();
            try
            {
                await using var connection = db.Open();
                await connection.ExecuteAsync(
                    "update lab_runs set status=@Status,result=@result::jsonb,error=@Error,finished_at=now() where id=@Id",
                    new { job.Id, job.Status, job.Error, result = job.Result is null ? null : JsonSerializer.Serialize(job.Result, JsonOptions) });
            }
            catch (Exception error) { logger.LogError(error, "Falha ao gravar o laboratório {Id}.", job.Id); }
        }
    }

    private async Task SetStatus(Guid id, string status)
    {
        await using var connection = db.Open();
        await connection.ExecuteAsync("update lab_runs set status=@status where id=@id", new { id, status });
    }

    /// <summary>Carrega o histórico do jogo e roda o backtest. Não grava nada.</summary>
    public async Task<object> Run(LabRunRequest request, Action<int, int>? progress = null)
    {
        request = Normalize(request);
        var clock = Stopwatch.StartNew();
        object result = request.Game switch
        {
            "bicho" => LabBicho.Run(await LoadBicho(request), new LabBichoOptions(request.Bank!, request.Time, request.TestDays, request.TrainDays,
                request.Quantity!.Value, request.BetValue, request.PrizeRange), progress),
            "megasena" => LabNumberGame.Run(LabNumberGame.MegaSena, await LoadMega(), Options(request), progress),
            _ => LabNumberGame.Run(LabNumberGame.Lotofacil, await LoadLotofacil(), Options(request), progress)
        };
        return new { report = result, elapsedMs = clock.ElapsedMilliseconds, explanation = Explanation };
    }

    public const string Explanation = "Os sorteios são aleatórios e independentes: nenhum histórico muda a chance do próximo resultado. " +
        "Este laboratório existe para medir isso com honestidade. O que o apostador controla de verdade é o custo (quanto aposta), " +
        "a cobertura (quantos números diferentes cobre) e a garantia (fechamentos que trocam chance de prêmio grande por prêmio menor mais frequente) — " +
        "não a previsão. Em média, toda estratégia devolve o mesmo percentual do valor apostado que o aleatório.";

    private static LabNumberOptions Options(LabRunRequest request) =>
        new(request.TestContests, request.TrainContests, request.Quantity!.Value, request.WheelNumbers);

    public static LabRunRequest Normalize(LabRunRequest request)
    {
        var game = (request.Game ?? "").Trim().ToLowerInvariant();
        if (game is not ("bicho" or "megasena" or "lotofacil")) throw new ArgumentException("Escolha Bicho, Mega-Sena ou Lotofácil.");
        if (game == "bicho")
        {
            var bank = (request.Bank ?? "").Trim().ToUpperInvariant();
            if (!Banks.Contains(bank)) throw new ArgumentException("Selecione Loteria Nacional ou Look Loterias.");
            string? time = null;
            if (!string.IsNullOrWhiteSpace(request.Time) && !request.Time.Trim().Equals("TODOS", StringComparison.OrdinalIgnoreCase))
            {
                if (!TimeOnly.TryParse(request.Time, out var parsed) || !PredictionService.BankSchedules(bank).Contains(parsed.ToString("HH:mm")))
                    throw new ArgumentException("Horário inválido para a banca.");
                time = parsed.ToString("HH:mm");
            }
            var quantity = request.Quantity ?? 10;
            if (quantity is < 1 or > LabBicho.MaxQuantity) throw new ArgumentException($"Informe de 1 a {LabBicho.MaxQuantity} milhares por extração.");
            if (request.BetValue is < 0.01m or > 1_000m) throw new ArgumentException("O valor por aposta deve ficar entre R$ 0,01 e R$ 1.000.");
            return request with
            {
                Game = game, Bank = bank, Time = time, Quantity = quantity, TestDays = Math.Clamp(request.TestDays, 7, 365),
                TrainDays = Math.Clamp(request.TrainDays, 0, 365), PrizeRange = Math.Clamp(request.PrizeRange, 1, 10)
            };
        }
        var games = request.Quantity ?? (game == "megasena" ? 7 : 16);
        if (games is < 1 or > LabNumberGame.MaxQuantity) throw new ArgumentException($"Informe de 1 a {LabNumberGame.MaxQuantity} jogos simples por concurso.");
        if (game == "megasena" && request.WheelNumbers is < 7 or > 12) throw new ArgumentException("O fechamento do laboratório usa de 7 a 12 dezenas.");
        return request with
        {
            Game = game, Bank = null, Time = null, Quantity = games, BetValue = game == "megasena" ? MegaSenaService.BetPrice : LotofacilService.BetPrice,
            TestContests = Math.Clamp(request.TestContests, 20, LabNumberGame.MaxTestContests), TrainContests = Math.Clamp(request.TrainContests, 0, 1000)
        };
    }

    private async Task<List<LabExtraction>> LoadBicho(LabRunRequest request)
    {
        await using var connection = db.Open();
        var last = await connection.ExecuteScalarAsync<DateTime?>("select max(extraction_date) from extractions where bank=@Bank", new { request.Bank })
            ?? throw new ArgumentException("Não há extrações importadas para essa banca.");
        var start = last.AddDays(-request.TestDays - request.TrainDays - LabBicho.HistoryMarginDays);
        var rows = await connection.QueryAsync<(long ExtractionId, DateTime Date, TimeSpan Time, int Position, string Number)>(
            @"select e.id as ExtractionId,e.extraction_date as Date,e.extraction_time as Time,r.position as Position,r.number as Number
              from results r join extractions e on e.id=r.extraction_id
              where e.bank=@Bank and e.extraction_date>=@start and r.position between 1 and 10
              order by e.extraction_date,e.extraction_time,r.position", new { request.Bank, start });
        return rows.GroupBy(x => x.ExtractionId).Select(g =>
        {
            var numbers = new string?[10];
            foreach (var row in g) numbers[row.Position - 1] = new string(row.Number.Where(char.IsDigit).ToArray()).PadLeft(4, '0')[^4..];
            return new LabExtraction(g.Key, g.First().Date.Date, g.First().Time, numbers);
        }).OrderBy(x => x.Date).ThenBy(x => x.Time).ToList();
    }

    private async Task<List<LabNumberDraw>> LoadMega()
    {
        await using var connection = db.Open();
        var rows = await connection.QueryAsync<MegaSenaService.DrawRow>(
            @"select contest,draw_date as DrawDate,numbers,quadra_prize as QuadraPrize,quina_prize as QuinaPrize,sena_prize as SenaPrize
              from megasena_draws order by contest");
        return rows.Select(x => new LabNumberDraw(x.Contest, x.DrawDate, x.Numbers.Select(n => (int)n).ToArray(),
            [x.QuadraPrize, x.QuinaPrize, x.SenaPrize])).ToList();
    }

    private async Task<List<LabNumberDraw>> LoadLotofacil()
    {
        await using var connection = db.Open();
        var rows = await connection.QueryAsync<LotofacilService.DrawRow>(
            @"select contest,draw_date as DrawDate,numbers,prize11 as Prize11,prize12 as Prize12,prize13 as Prize13,prize14 as Prize14,prize15 as Prize15
              from lotofacil_draws order by contest");
        return rows.Select(x => new LabNumberDraw(x.Contest, x.DrawDate, x.Numbers.Select(n => (int)n).ToArray(),
            [x.Prize11, x.Prize12, x.Prize13, x.Prize14, x.Prize15])).ToList();
    }

    public async Task<object?> Get(Guid id)
    {
        if (jobs.TryGetValue(id, out var job)) return Snapshot(job);
        await using var connection = db.Open();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(
            @"select id,game,status,parameters::text as Parameters,result::text as Result,error,created_by as CreatedBy,
                     created_at as CreatedAt,finished_at as FinishedAt from lab_runs where id=@id", new { id });
        if (row is null) return null;
        // Sem o job em memória, uma execução em andamento foi interrompida por reinício da API.
        var interrupted = row.Status is "QUEUED" or "RUNNING";
        return new
        {
            id = row.Id, game = row.Game, status = interrupted ? "FAILED" : row.Status, stage = interrupted ? "Interrompido" : "Concluído",
            completed = 0, total = 0, percent = interrupted ? 0 : 100,
            parameters = JsonSerializer.Deserialize<JsonElement>(row.Parameters),
            result = row.Result is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.Result),
            error = interrupted ? "A execução foi interrompida porque a API reiniciou. Rode novamente." : row.Error,
            createdAt = row.CreatedAt, finishedAt = row.FinishedAt
        };
    }

    public async Task<object> List(int limit = 20)
    {
        await using var connection = db.Open();
        var rows = await connection.QueryAsync<RunRow>(
            @"select id,game,status,parameters::text as Parameters,created_by as CreatedBy,created_at as CreatedAt,finished_at as FinishedAt,
                     result->'report'->'verdict'->>'title' as Verdict
              from lab_runs order by created_at desc limit @limit", new { limit = Math.Clamp(limit, 1, 100) });
        return rows.Select(x => new
        {
            id = x.Id, game = x.Game,
            status = jobs.TryGetValue(x.Id, out var job) ? job.Status : x.Status is "QUEUED" or "RUNNING" ? "FAILED" : x.Status,
            parameters = JsonSerializer.Deserialize<JsonElement>(x.Parameters), createdBy = x.CreatedBy,
            createdAt = x.CreatedAt, finishedAt = x.FinishedAt, verdict = x.Verdict
        });
    }

    private static object Snapshot(Job job) => new
    {
        id = job.Id, game = job.Game, status = job.Status, stage = job.Stage, completed = job.Completed, total = job.Total,
        percent = job.Status == "COMPLETED" ? 100 : job.Total == 0 ? 0 : Math.Round(100d * job.Completed / job.Total, 1),
        parameters = job.Parameters, result = job.Result, error = job.Error, createdAt = job.CreatedAt, finishedAt = job.FinishedAt
    };
}
