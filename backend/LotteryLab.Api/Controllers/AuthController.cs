using System.Security.Claims;
using Dapper;
using LotteryLab.Api.Models;
using LotteryLab.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace LotteryLab.Api.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await auth.Login(request.Username ?? "", request.Password ?? "");
        return result is null ? Unauthorized(new { message = "Usuário ou senha inválidos." }) : Ok(new { result.Value.Token, user = result.Value.User });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(CurrentUser());

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) await auth.Logout(header[7..].Trim());
        return Ok(new { message = "Sessão encerrada." });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var validation = PasswordService.Validate(request.NewPassword ?? "");
        if (validation is not null) return BadRequest(new { message = validation });
        var changed = await auth.ChangePassword(CurrentUserId(), request.CurrentPassword ?? "", request.NewPassword!);
        return changed ? Ok(new { message = "Senha alterada. Entre novamente com a nova senha." })
            : BadRequest(new { message = "A senha atual está incorreta." });
    }

    [Authorize(Policy = Permissions.UsersManage)]
    [HttpGet("users")]
    public async Task<IActionResult> Users() => Ok(await auth.ListUsers());

    [Authorize(Policy = Permissions.UsersManage)]
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser(CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length < 3)
            return BadRequest(new { message = "O usuário deve possuir pelo menos 3 caracteres." });
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return BadRequest(new { message = "Informe o nome." });
        var validation = PasswordService.Validate(request.Password ?? "");
        if (validation is not null) return BadRequest(new { message = validation });
        try { return Ok(new { id = await auth.Create(request.Username, request.DisplayName, request.Password!, request.Permissions ?? []) }); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Conflict(new { message = "Esse nome de usuário já existe." }); }
    }

    [Authorize(Policy = Permissions.UsersManage)]
    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, UpdateUserRequest request)
    {
        if (id == CurrentUserId()) return BadRequest(new { message = "Altere sua própria senha em Minha conta." });
        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            var validation = PasswordService.Validate(request.NewPassword);
            if (validation is not null) return BadRequest(new { message = validation });
        }
        var count = await auth.Update(id, request.DisplayName, request.IsActive, request.Permissions ?? [], request.NewPassword);
        return count == 0 ? BadRequest(new { message = "Usuário não encontrado ou administrador protegido." }) : Ok(new { message = "Usuário atualizado." });
    }

    [Authorize(Policy = Permissions.UsersManage)]
    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        if (id == CurrentUserId()) return BadRequest(new { message = "Você não pode excluir a própria conta." });
        var count = await auth.Delete(id);
        return count == 0 ? BadRequest(new { message = "Usuário não encontrado ou administrador protegido." }) : Ok(new { message = "Usuário excluído." });
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private object CurrentUser() => new
    {
        id = CurrentUserId(), username = User.Identity!.Name, displayName = User.FindFirstValue("display_name"),
        role = User.FindFirstValue(ClaimTypes.Role), permissions = User.FindAll("permission").Select(x => x.Value),
        mustChangePassword = User.FindFirstValue("must_change_password") == "true"
    };
}
