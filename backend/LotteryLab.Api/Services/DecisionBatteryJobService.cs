using System.Collections.Concurrent;

namespace LotteryLab.Api.Services;

public sealed class DecisionBatteryJobService(IServiceScopeFactory scopes)
{
    private sealed class Job
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Status { get; set; } = "RUNNING";
        public int Completed { get; set; }
        public int Total { get; set; }
        public object? Result { get; set; }
        public string? Error { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Job> jobs = new();

    public object Start(string bank, int quantity, decimal betAmount, decimal dezenaPayout,
        decimal centenaPayout, decimal milharPayout, int maxEvaluations)
    {
        var job = new Job();
        jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var predictions = scope.ServiceProvider.GetRequiredService<PredictionService>();
                job.Result = await predictions.DecisionBattery(bank, quantity, betAmount, dezenaPayout, centenaPayout,
                    milharPayout, maxEvaluations, (completed, total) => { job.Completed = completed; job.Total = total; });
                job.Status = "COMPLETED";
            }
            catch (Exception exception)
            {
                job.Error = "Não foi possível concluir a bateria. Tente novamente.";
                job.Status = "FAILED";
                Console.Error.WriteLine(exception);
            }
        });
        return Snapshot(job);
    }

    public object? Get(Guid id) => jobs.TryGetValue(id, out var job) ? Snapshot(job) : null;

    private static object Snapshot(Job job) => new
    {
        id = job.Id,
        status = job.Status,
        completed = job.Completed,
        total = job.Total,
        percent = job.Total == 0 ? 0 : Math.Round(100d * job.Completed / job.Total, 1),
        result = job.Result,
        error = job.Error
    };
}
