namespace LotteryLab.Api.Models;

public record MegaSenaDraw(int Contest, DateOnly DrawDate, int[] Numbers,
    int SenaWinners, decimal SenaPrize, int QuinaWinners, decimal QuinaPrize,
    int QuadraWinners, decimal QuadraPrize, bool Accumulated, decimal? NextEstimate, string Source);

public record MegaSenaGenerateRequest(int? TargetContest = null, int Quantity = 5, int NumbersPerGame = 6,
    int WindowContests = 100, long? Seed = null);

public record MegaSenaWheelRequest(int[]? Numbers, string? Guarantee = "4se4", string? Label = null);

public record MegaSenaNumberFeatures(double WindowFrequency, double HistoryFrequency, double Delay,
    double Momentum, double PairAffinity);

public record MegaSenaNumberScore(int Number, double Score, MegaSenaNumberFeatures Features,
    int WindowHits, int HistoryHits, int DelayContests);

public record MegaSenaGame(int Rank, int[] Numbers);

public record MegaSenaGameResult(int Rank, int[] Numbers, int Hits, int[] MatchedNumbers,
    long SenaCount, long QuinaCount, long QuadraCount, decimal ReturnAmount);

public record MegaSenaPrizeSummary(int Hits, long SenaCount, long QuinaCount, long QuadraCount);
