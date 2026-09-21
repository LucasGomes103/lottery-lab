using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed class MilharStatisticsService(Db db)
{
    public async Task<MilharRankingResponse> Rank(MilharRankingRequest request, CancellationToken token)
    {
        MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, request.Configuration ?? new(), request.Model);
        return MilharStatisticsEngine.Rank(await Load(request.Bank, request.Cutoff, false, token), request, token);
    }
    public async Task<MilharBacktestResponse> Backtest(MilharBacktestRequest request, CancellationToken token)
    {
        MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, request.Configuration ?? new(), request.Model);
        return MilharBacktestEngine.Run(await Load(request.Bank, request.End, true, token), request, token);
    }
    public async Task<object> Debug(long extractionId, MilharRankingRequest request, CancellationToken token)
    {
        MilharStatisticsEngine.Validate(request.Bank, request.Time, request.Prize, request.Configuration ?? new(), request.Model);
        var source = await Load(request.Bank, request.Cutoff, true, token);
        var actual = source.Where(x => x.ExtractionId == extractionId && x.Timestamp == request.Cutoff && x.Time == request.Time &&
            (request.Prize == null || x.Position == request.Prize)).ToArray();
        if (actual.Length == 0 || actual.Any(x => !MilharStatisticsEngine.ValidNumber(x.Number)))
            throw new ArgumentException("Extração válida não encontrada para os filtros e corte informados.");
        var response = MilharStatisticsEngine.Rank(source, request with { Top = 10000 }, token, false);
        var history = MilharStatisticsEngine.History(source, request);
        var detailed = MilharStatisticsEngine.Rank(source, request with { Top = 100 }, token);
        return new { History = history, Previous = history.Where(x => x.Time == request.Time).GroupBy(x => x.Position).Select(g => g.Last()),
            Ranking = detailed, Actual = actual.Select(x => new { Observation = x, Candidate = response.Ranking.Single(c => c.Milhar == x.Number) }),
            Note = "Reconstrução usando a base atual; correções/importações posteriores podem alterar o histórico. O corte é o horário da extração, não o horário de publicação." };
    }
    private async Task<List<MilharObservation>> Load(string bank, DateTime cutoff, bool inclusive, CancellationToken token)
    {
        if (cutoff == default || cutoff.Kind != DateTimeKind.Unspecified) throw new ArgumentException("Informe data/hora local sem offset.");
        await using var connection = db.Open();
        // One parameterized query per request. Reuse the existing tables, including historical prize/group data.
        var rows = (await connection.QueryAsync<MilharObservation>(new CommandDefinition(
            @"select e.id as ExtractionId, e.extraction_date + e.extraction_time as Timestamp,
                     e.bank as Bank, to_char(e.extraction_time, 'HH24:MI') as Time,
                     r.position as Position, r.number as Number, r.group_no as ""Group""
              from extractions e join results r on r.extraction_id=e.id
              where e.bank=@bank and e.extraction_date <= @cutoff::date and (e.extraction_date + e.extraction_time < @cutoff
                    or (@inclusive and e.extraction_date + e.extraction_time = @cutoff))
                and r.position between 1 and 10
              order by e.extraction_date, e.extraction_time, r.position, e.id limit 250001",
            new { bank, cutoff, inclusive }, cancellationToken: token))).ToList();
        if (rows.Count > 250000) throw new ArgumentException("Histórico excede o limite de 250000 resultados desta versão.");
        return rows;
    }
}
