namespace LotteryLab.Api.Models;

public record PredictionRequest(string Bank, string Time, DateOnly? TargetDate = null, int WindowDays = 90,
    int Quantity = 10, List<int>? Groups = null, bool UseRecommendedWindow = true,
    decimal BetAmount = 30m, decimal DezenaPayout = 8.57m, decimal CentenaPayout = 57.14m,
    decimal MilharPayout = 296.30m, int PrizeRange = 5,
    decimal DezenaStake = 10m, decimal CentenaStake = 10m, decimal MilharStake = 10m);
public record PredictionDeleteRequest(List<Guid> Ids);

public record ManualPredictionRequest(string Bank, string Time, DateOnly TargetDate, string Numbers,
    int PrizeRange = 5, decimal DezenaStake = 0m, decimal CentenaStake = 0m, decimal MilharStake = 0m);

public record PredictionFeatures(
    double Frequency, double TimeFrequency, double Delay, double Continuity,
    double Transition, double Momentum, double Reversal, double DigitAffinity,
    double Novelty, double RepetitionPenalty);

public record PredictionCandidate(
    int Rank, string Milhar, string Centena, string Dezena, int Group,
    string SelectionType, double StatisticalScore, double FinalScore,
    PredictionFeatures Features, List<string> Reasons);

public record PredictionResponse(
    Guid Id, string Algorithm, int AlgorithmVersion, string Bank, string Time,
    DateOnly TargetDate, int WindowDays, int Quantity, long RandomSeed,
    int SampleExtractions, int SampleResults, string Robustness,
    object Composition, List<PredictionCandidate> Numbers, string Warning,
    decimal BetAmount, decimal DezenaPayout, decimal CentenaPayout, decimal MilharPayout,
    bool UsedRecommendedWindow, int PrizeRange, decimal DezenaStake, decimal CentenaStake, decimal MilharStake);

public record PredictionEvaluation(
    Guid PredictionId, long ExtractionId, bool HitMilhar, bool HitCentena, bool HitDezena,
    int? BestMilharPosition, int? BestCentenaPosition, int? BestDezenaPosition);

public record AnimalTrend(int Rank, int Group, string Animal, List<string> Dezenas, double Score,
    double Frequency, double RecentStrength, double Delay, List<string> Reasons);
public record AnimalTrendResponse(string Bank, string Time, DateOnly TargetDate, int WindowDays,
    int SampleExtractions, List<AnimalTrend> Animals, string Warning);
