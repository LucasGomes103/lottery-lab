using System.Collections.Concurrent;

namespace LotteryLab.Api.Services;

public sealed class HistorySyncJobService(IServiceScopeFactory scopes)
{
    private sealed class Job
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Status { get; set; } = "RUNNING";
        public int Completed { get; set; }
        public int Total { get; init; }
        public string CurrentStep { get; set; } = "Preparando carga";
        public object? Result { get; set; }
        public string? Error { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Job> jobs = new();

    public object Start(string bank, DateOnly start, DateOnly end)
    {
        var job = new Job { Total = end.DayNumber - start.DayNumber + 1 };
        jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var history = scope.ServiceProvider.GetRequiredService<ResultFacilHistoryService>();
                var result = await history.Sync(bank, start, end, default, (completed, total, step) =>
                {
                    job.Completed = completed; job.CurrentStep = step;
                });
                job.Result = result; job.Completed = job.Total;
                job.CurrentStep = result.Errors.Count == 0 ? "Carga concluída" : $"Carga concluída com {result.Errors.Count} erros";
                job.Status = result.Errors.Count == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
            }
            catch (Exception exception)
            {
                job.Error = exception.Message; job.CurrentStep = "Carga interrompida"; job.Status = "FAILED";
            }
        });
        return Snapshot(job);
    }

    public object? Get(Guid id) => jobs.TryGetValue(id, out var job) ? Snapshot(job) : null;

    private static object Snapshot(Job job) => new
    {
        id = job.Id, status = job.Status, completed = job.Completed, total = job.Total,
        percent = job.Total == 0 ? 0 : Math.Round(100d * job.Completed / job.Total, 1),
        currentStep = job.CurrentStep, result = job.Result, error = job.Error
    };
}
