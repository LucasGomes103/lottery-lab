namespace LotteryLab.Api.Services;

// Mesmo modelo do MegaSenaHistoryJobService: estado em memória, uma carga por vez.
public sealed class LotofacilHistoryJobService(IServiceScopeFactory scopes, LotofacilSyncState automatic)
{
    private sealed class Job
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Status { get; set; } = "RUNNING";
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentStep { get; set; } = "Consultando o último concurso";
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        public LotofacilHistorySyncResult? Result { get; set; }
        public string? Error { get; set; }
    }

    private readonly object gate = new();
    private Job? current;

    public object Start()
    {
        lock (gate)
        {
            if (current is { Status: "RUNNING" }) return Snapshot(current);
            current = new Job();
        }
        var job = current;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var results = scope.ServiceProvider.GetRequiredService<LotofacilResultsService>();
                var result = await results.SyncHistory(default, (completed, total, step) =>
                {
                    job.Completed = completed; job.Total = total; job.CurrentStep = step;
                });
                job.Result = result; job.Completed = job.Total;
                job.CurrentStep = result.Errors.Count == 0 ? "Carga concluída" : $"Carga concluída com {result.Errors.Count} erros";
                job.Status = result.Errors.Count == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
            }
            catch (Exception exception)
            {
                job.Error = LotofacilResultsService.Describe(exception); job.CurrentStep = "Carga interrompida"; job.Status = "FAILED";
            }
            finally { job.FinishedAt = DateTimeOffset.UtcNow; }
        });
        return Snapshot(job);
    }

    public object Status() => current is { } job ? Snapshot(job) : new { status = "IDLE", automatic = AutomaticSnapshot() };

    private object AutomaticSnapshot() => new { automatic.LastAttempt, automatic.LastSuccess, automatic.LastContest,
        automatic.LastSource, automatic.LastError, automatic.LastCaixaError };

    private object Snapshot(Job job) => new
    {
        id = job.Id, status = job.Status, completed = job.Completed, total = job.Total,
        percent = job.Total == 0 ? (job.Status == "RUNNING" ? 0 : 100) : Math.Round(100d * job.Completed / job.Total, 1),
        currentStep = job.CurrentStep, job.StartedAt, job.FinishedAt, result = job.Result, error = job.Error,
        errors = job.Result?.Errors.Take(20), automatic = AutomaticSnapshot()
    };
}
