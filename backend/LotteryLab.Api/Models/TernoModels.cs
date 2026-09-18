namespace LotteryLab.Api.Models;

public record TernoRequest(string Bank, string Time, DateOnly TargetDate, int Quantity = 10,
    int WindowDays = 90, List<int>? Groups = null, decimal TotalStake = 10m);
public record TernoGame(int Rank, string[] Dezenas, double Score);
public record TernoResult(int Rank, string[] Dezenas, double Score, int MatchedDezenas, bool Hit);
