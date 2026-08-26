namespace LotteryLab.Api.Models;
public record ParsedResult(int Position, string Number, string? Dezena, string? Centena, int? Group, string? Animal);
public record ImportPreview(string FileName, DateOnly? Date, string? Bank, string? Time, List<ParsedResult> Results, string RawText);
public record ForecastCandidate(string Value, double Score, double Continuity, double Delay, double Reversal, int Rank);
public record ForecastResponse(string Strategy, List<ForecastCandidate> Dezenas, object Metrics);
public record AnalysisRequest(string Bank, string Time, int WindowDays = 15, int Top = 8);
public record AiRequest(string Bank, string Time, int WindowDays = 30, string? Question = null);
