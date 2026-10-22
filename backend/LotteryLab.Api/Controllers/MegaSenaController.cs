using LotteryLab.Api.Models;
using LotteryLab.Api.Security;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LotteryLab.Api.Controllers;

[ApiController]
[Route("api/megasena")]
[Authorize]
public sealed class MegaSenaController(MegaSenaService megaSena, MegaSenaResultsService results,
    MegaSenaHistoryJobService historyJobs) : ControllerBase
{
    [HttpPost("sync-history")]
    [Authorize(Policy = Permissions.ImportsWrite)]
    public IActionResult SyncHistory() => Accepted(historyJobs.Start());

    [HttpGet("sync-history/status")]
    [Authorize(Policy = Permissions.ImportsWrite)]
    public IActionResult SyncHistoryStatus() => Ok(historyJobs.Status());

    [HttpPost("sync-latest")]
    [Authorize(Policy = Permissions.ImportsWrite)]
    public async Task<IActionResult> SyncLatest(CancellationToken cancellationToken)
    {
        try { return Ok(await results.SyncLatest(cancellationToken)); }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"Não foi possível consultar a Mega-Sena: {error.Message}" });
        }
    }

    [HttpGet("draws")]
    [Authorize(Policy = Permissions.PredictionsRead)]
    public async Task<IActionResult> Draws(int page = 1, int pageSize = 20) => Ok(await megaSena.Draws(page, pageSize));

    [HttpGet("draws/{contest:int}")]
    [Authorize(Policy = Permissions.PredictionsRead)]
    public async Task<IActionResult> Draw(int contest) =>
        await megaSena.Draw(contest) is { } draw ? Ok(draw) : NotFound(new { message = "Concurso não encontrado." });

    [HttpPost("generate")]
    [Authorize(Policy = Permissions.PredictionsWrite)]
    public async Task<IActionResult> Generate(MegaSenaGenerateRequest request)
    {
        try { return Ok(await megaSena.Generate(request)); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    [HttpGet("predictions")]
    [Authorize(Policy = Permissions.PredictionsRead)]
    public async Task<IActionResult> Predictions(int page = 1, int pageSize = 20) => Ok(await megaSena.List(page, pageSize));

    [HttpGet("predictions/{id:guid}")]
    [Authorize(Policy = Permissions.PredictionsRead)]
    public async Task<IActionResult> Prediction(Guid id) =>
        await megaSena.Get(id) is { } prediction ? Ok(prediction) : NotFound(new { message = "Jogo não encontrado." });

    [HttpPost("predictions/delete-batch")]
    [Authorize(Policy = Permissions.PredictionsWrite)]
    public async Task<IActionResult> DeletePredictions(PredictionDeleteRequest request)
    {
        if (request.Ids is not { Count: > 0 }) return BadRequest(new { message = "Selecione ao menos um jogo." });
        if (request.Ids.Count > 500) return BadRequest(new { message = "Exclua no máximo 500 registros por vez." });
        return Ok(new { deleted = await megaSena.Delete(request.Ids) });
    }

    [HttpGet("statistics")]
    [Authorize(Policy = Permissions.DashboardRead)]
    public async Task<IActionResult> Statistics() => Ok(await megaSena.Statistics());

    [HttpGet("backtest")]
    [Authorize(Policy = Permissions.AnalysisUse)]
    public async Task<IActionResult> Backtest(int contests = 200, int numbersPerGame = 6, int quantity = 5, int windowContests = 100)
    {
        try { return Ok(await megaSena.Backtest(contests, numbersPerGame, quantity, windowContests)); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }
}
