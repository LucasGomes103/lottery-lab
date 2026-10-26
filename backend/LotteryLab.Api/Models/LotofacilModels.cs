namespace LotteryLab.Api.Models;

public record LotofacilDraw(int Contest, DateOnly DrawDate, int[] Numbers,
    int Winners15, decimal Prize15, int Winners14, decimal Prize14, int Winners13, decimal Prize13,
    int Winners12, decimal Prize12, int Winners11, decimal Prize11, bool Accumulated, decimal? NextEstimate, string Source);

public record LotofacilGenerateRequest(int? TargetContest = null, int Quantity = 5, int NumbersPerGame = 15,
    int WindowContests = 100, long? Seed = null);

public record LotofacilNumberFeatures(double WindowFrequency, double HistoryFrequency, double Delay,
    double Momentum, double PairAffinity);

public record LotofacilNumberScore(int Number, double Score, LotofacilNumberFeatures Features,
    int WindowHits, int HistoryHits, int DelayContests);

public record LotofacilGame(int Rank, int[] Numbers);

public record LotofacilGameResult(int Rank, int[] Numbers, int Hits, int[] MatchedNumbers,
    long Count15, long Count14, long Count13, long Count12, long Count11, decimal ReturnAmount);

public record LotofacilPrizeSummary(int Hits, long Count15, long Count14, long Count13, long Count12, long Count11);
