using LotteryLab.Api.Models;
using LotteryLab.Api.Security;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LotteryLab.Api.Controllers;

[ApiController]
[Route("api/statistics")]
[Authorize(Policy = Permissions.AnalysisUse)]
public sealed class MilharStatisticsController(MilharStatisticsService service) : ControllerBase
{
    [HttpPost("milhar-ranking")]
    public Task<IActionResult> Rank(MilharRankingRequest request, CancellationToken token) => Execute(async () => await service.Rank(request, token));
    [HttpPost("milhar-backtest")]
    public Task<IActionResult> Backtest(MilharBacktestRequest request, CancellationToken token) => Execute(async () => await service.Backtest(request, token));
    [HttpPost("milhar-backtest/{extractionId:long}/debug")]
    public Task<IActionResult> Debug(long extractionId, MilharRankingRequest request, CancellationToken token) => Execute(() => service.Debug(extractionId, request, token));
    private async Task<IActionResult> Execute(Func<Task<object>> action)
    {
        try { return Ok(await action()); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }
}
