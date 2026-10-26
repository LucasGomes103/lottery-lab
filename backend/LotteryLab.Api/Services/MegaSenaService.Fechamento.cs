using System.Text.Json;
using Dapper;
using LotteryLab.Api.Models;

namespace LotteryLab.Api.Services;

public sealed partial class MegaSenaService
{
    private const int SuggestionWindowContests = 100;

    /// <summary>Grava um fechamento como um registro comum de jogos: a conferência automática dos prêmios vale igual.</summary>
    public async Task<object> SaveWheel(WheelResult wheel, string? label)
    {
        await using var connection = db.Open();
        var latest = await connection.ExecuteScalarAsync<int?>("select max(contest) from megasena_draws")
            ?? throw new ArgumentException("Não há resultados da Mega-Sena. Faça a carga histórica primeiro.");
        var target = latest + 1;
        var config = new
        {
            method = "fechamento", guarantee = wheel.Guarantee.Code, guaranteeLabel = wheel.Guarantee.Label,
            numbers = wheel.Numbers, fullCombinations = wheel.FullCombinations, betPrice = BetPrice,
            label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
        };
        var id = Guid.NewGuid();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            @"insert into megasena_predictions(id,target_contest,seed,window_contests,numbers_per_game,quantity,total_cost,config,scores)
              values(@id,@target,0,0,6,@quantity,@totalCost,@config::jsonb,'[]'::jsonb)",
            new { id, target, quantity = wheel.Games.Count, totalCost = wheel.Games.Count * BetPrice,
                config = JsonSerializer.Serialize(config, JsonOptions) }, transaction);
        for (var i = 0; i < wheel.Games.Count; i++)
            await connection.ExecuteAsync("insert into megasena_prediction_games(prediction_id,rank,numbers) values(@id,@rank,@numbers)",
                new { id, rank = i + 1, numbers = wheel.Games[i].Select(x => (short)x).ToArray() }, transaction);
        await transaction.CommitAsync();
        return (await Load(connection, [id], includeScores: false)).Single();
    }

    /// <summary>As N dezenas mais bem pontuadas pelo mesmo score do gerador, para o próximo concurso.</summary>
    public async Task<object> SuggestWheelNumbers(int quantity)
    {
        if (quantity is < MegaSenaWheelService.MinNumbers or > MegaSenaWheelService.MaxNumbers)
            throw new ArgumentException($"Informe de {MegaSenaWheelService.MinNumbers} a {MegaSenaWheelService.MaxNumbers} dezenas.");
        await using var connection = db.Open();
        var history = (await connection.QueryAsync<DrawNumbersRow>("select contest,numbers from megasena_draws order by contest"))
            .Select(x => x.Numbers.Select(n => (int)n).ToArray()).ToList();
        if (history.Count < 10) throw new ArgumentException("São necessários pelo menos 10 concursos importados.");
        var top = ScoreNumbers(history, SuggestionWindowContests).Take(quantity).ToList();
        return new { numbers = top.Select(x => x.Number).Order().ToArray(), scores = top,
            windowContests = SuggestionWindowContests, historyContests = history.Count };
    }
}
