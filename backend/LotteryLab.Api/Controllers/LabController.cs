using LotteryLab.Api.Models;
using LotteryLab.Api.Security;
using LotteryLab.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LotteryLab.Api.Controllers;

// O laboratório é pesado como a bateria de decisão, então exige a mesma permissão de análise.
[ApiController]
[Route("api/lab")]
[Authorize(Policy = Permissions.AnalysisUse)]
public sealed class LabController(LabJobService lab) : ControllerBase
{
    [HttpPost("run")]
    public async Task<IActionResult> Run(LabRunRequest request)
    {
        try { return Accepted(await lab.Start(request, User.Identity?.Name)); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id) =>
        await lab.Get(id) is { } run ? Ok(run) : NotFound(new { message = "Execução não encontrada." });

    [HttpGet("runs")]
    public async Task<IActionResult> List(int limit = 20) => Ok(await lab.List(limit));

    [HttpDelete("runs/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) => Ok(await lab.Delete([id]));

    [HttpPost("runs/delete-batch")]
    public async Task<IActionResult> DeleteBatch(PredictionDeleteRequest request)
    {
        if (request.Ids is not { Count: > 0 }) return BadRequest(new { message = "Selecione ao menos uma execução." });
        if (request.Ids.Count > 500) return BadRequest(new { message = "Exclua no máximo 500 execuções por vez." });
        return Ok(await lab.Delete(request.Ids));
    }
}
