using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LotteryLab.Api.Security;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AuthService auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (token.Length < 32) return AuthenticateResult.Fail("Token inválido.");
        var user = await auth.Authenticate(token);
        if (user is null) return AuthenticateResult.Fail("Sessão inválida ou expirada.");
        return AuthenticateResult.Success(new AuthenticationTicket(auth.Principal(user), Scheme.Name));
    }
}
