namespace LotteryLab.Api.Models;

public record ParsedResult(int Position, string Number, string? Milhar, string? Centena, string? Dezena, int? Group, string? Animal);
public record ParsedExtraction(DateOnly? Date, string Bank, string? Time, List<ParsedResult> Results, List<string> Warnings);
public record ImportPreview(string FileName, string SourceHash, bool UsedOcr, List<ParsedExtraction> Extractions, List<string> Warnings);
public record HistoryUpdateItem(long Id, ParsedExtraction Extraction);
public record BatchHistoryUpdate(List<HistoryUpdateItem> Items);
public record GeneratedNumber(int Rank, string Milhar, string Centena, string Dezena, double Score, double MilharSignal, double CentenaSignal, double DezenaSignal, double DigitSignal);
public record NumberGenerationResponse(string Algorithm, string Bank, string Time, DateOnly TargetDate, int WindowDays, int SampleExtractions, int SampleResults, string Robustness, List<GeneratedNumber> Numbers, object Baseline, string Warning);
public record ForecastCandidate(string Value, double Score, double Continuity, double Delay, double Reversal, int Rank);
public record ForecastResponse(string Strategy, List<ForecastCandidate> Dezenas, object Metrics);
public record AnalysisRequest(string Bank, string Time, int WindowDays = 15, int Top = 8);
public record AiRequest(string Bank, string Time, int WindowDays = 30, string? Question = null);
