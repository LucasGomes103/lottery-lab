namespace LotteryLab.Api.Models;

public sealed record MilharConfiguration
{
    // Component order is part of the versioned API contract.
    public double[] Weights { get; init; } = [.25, .15, .15, .15, .10, .10, .10];
    public int[] Windows { get; init; } = [10, 30, 50, 100, 0];
    public double[] WindowWeights { get; init; } = [.30, .25, .20, .15, .10];
    public double HalfLife { get; init; } = 30;
    public double Alpha { get; init; } = 1;
    public double SchedulePrior { get; init; } = 50;
}

public record MilharRankingRequest(string Bank, string Time, DateTime Cutoff, int? Prize = 1,
    int Top = 100, string Model = "C", MilharConfiguration? Configuration = null);
public record MilharBacktestRequest(string Bank, string Time, DateTime Start, DateTime End,
    int? Prize = 1, string Model = "C", MilharConfiguration? Configuration = null,
    int MinimumHistory = 100, bool CompareModels = true, bool Ablation = false,
    MilharConfiguration[]? Optimize = null);
public sealed record MilharObservation(long ExtractionId, DateTime Timestamp, string Bank, string Time,
    int Position, string Number, int? Group);
public record MilharScores(double Dezena, double Centena, double Digitos, double Recencia,
    double Horario, double Pares, double Transicao);
public record MilharEvidence(Dictionary<string, double> DezenaFrequencies, double[] PositionalFrequencies,
    Dictionary<string, double> Pairs, object Transitions, int ScheduleSample, double ScheduleReliability);
public record MilharCandidate(int Ranking, string Milhar, string Centena, string Dezena,
    double ScoreFinal, double ScoreGlobal, MilharScores Scores, MilharEvidence? Evidence);
public record MilharRankingResponse(string Version, MilharRankingRequest Request, int Sample,
    int ExcludedInvalidNumbers, DateTime? HistoryEnd, double[] EffectiveWeights,
    List<MilharCandidate> Ranking, long ElapsedMilliseconds);
public record MilharTrial(long ExtractionId, DateTime Date, string Bank, string Time, int Prize,
    string Actual, string Partition, int Rank, bool[] MilharHits, bool[] CentenaHits, bool[] DezenaHits,
    int HistoryCount, DateTime HistoryEnd);
public record MilharRate(string Kind, int Top, int Hits, double Rate, double Baseline,
    double Difference, double Lift, double? ConfidenceLow, double? ConfidenceHigh);
public record MilharMetrics(int Tests, double? MeanRank, double? MedianRank, double? Mrr,
    List<MilharRate> Rates, Dictionary<string, int> Distribution);
public record MilharModelResult(string Model, MilharConfiguration Configuration, MilharMetrics All,
    Dictionary<string, MilharMetrics> Partitions, List<MilharTrial> Trials);
public record MilharBacktestResponse(string Version, MilharBacktestRequest Request,
    List<MilharModelResult> Models, MilharConfiguration? Winner, object[] ValidationSearch,
    long ElapsedMilliseconds, string Methodology);
