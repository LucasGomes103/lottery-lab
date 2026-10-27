namespace LotteryLab.Api.Models;

/// <summary>Bicho usa banca, horário, dias e valor; Mega-Sena e Lotofácil usam concursos e a quantidade de apostas simples.</summary>
public record LabRunRequest(string Game = "bicho", string? Bank = "LT NACIONAL", string? Time = "21:00",
    int TestDays = 180, int TrainDays = 180, int TestContests = 300, int TrainContests = 300,
    int? Quantity = null, decimal BetValue = 1m, int PrizeRange = 5, int WheelNumbers = 10);
