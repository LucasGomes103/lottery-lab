using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using LotteryLab.Api.Data;

namespace LotteryLab.Api.Security;

public sealed record AuthUser(Guid Id, string Username, string DisplayName, string Role, string[] Permissions,
    bool IsActive, bool MustChangePassword);

public sealed class AuthService(Db db, PasswordService passwords)
{
    public async Task<(string Token, AuthUser User)?> Login(string username, string password)
    {
        await using var connection = db.Open();
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            """
            select id,username,display_name as "DisplayName",password_hash as "PasswordHash",role,
                   permissions,is_active as "IsActive",must_change_password as "MustChangePassword"
            from app_users where lower(username)=lower(@username)
            """, new { username = username.Trim() });
        if (row is null || !row.IsActive || !passwords.Verify(password, row.PasswordHash)) return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await connection.ExecuteAsync(
            "insert into auth_sessions(id,user_id,token_hash,expires_at) values(@id,@userId,@hash,now()+interval '7 days')",
            new { id = Guid.NewGuid(), userId = row.Id, hash = TokenHash(token) });
        await connection.ExecuteAsync("update app_users set last_login_at=now() where id=@id", new { row.Id });
        return (token, ToUser(row));
    }

    public async Task<AuthUser?> Authenticate(string token)
    {
        await using var connection = db.Open();
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            """
            select u.id,u.username,u.display_name as "DisplayName",u.password_hash as "PasswordHash",u.role,
                   u.permissions,u.is_active as "IsActive",u.must_change_password as "MustChangePassword"
            from auth_sessions s join app_users u on u.id=s.user_id
            where s.token_hash=@hash and s.expires_at>now() and u.is_active=true
            """, new { hash = TokenHash(token) });
        if (row is null) return null;
        await connection.ExecuteAsync("update auth_sessions set last_used_at=now() where token_hash=@hash", new { hash = TokenHash(token) });
        return ToUser(row);
    }

    public async Task Logout(string token)
    {
        await using var connection = db.Open();
        await connection.ExecuteAsync("delete from auth_sessions where token_hash=@hash", new { hash = TokenHash(token) });
    }

    public async Task<bool> ChangePassword(Guid userId, string currentPassword, string newPassword)
    {
        await using var connection = db.Open();
        var current = await connection.ExecuteScalarAsync<string?>("select password_hash from app_users where id=@userId", new { userId });
        if (current is null || !passwords.Verify(currentPassword, current)) return false;
        await connection.ExecuteAsync(
            "update app_users set password_hash=@hash,must_change_password=false,updated_at=now() where id=@userId",
            new { userId, hash = passwords.Hash(newPassword) });
        await connection.ExecuteAsync("delete from auth_sessions where user_id=@userId", new { userId });
        return true;
    }

    public async Task<IEnumerable<dynamic>> ListUsers()
    {
        await using var connection = db.Open();
        return await connection.QueryAsync(
            """
            select id,username,display_name,role,permissions,is_active,must_change_password,created_at,last_login_at
            from app_users order by lower(username)
            """);
    }

    public async Task<Guid> Create(string username, string displayName, string password, IEnumerable<string> permissions)
    {
        await using var connection = db.Open();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            insert into app_users(id,username,display_name,password_hash,role,permissions,is_active,must_change_password)
            values(@id,@username,@displayName,@hash,'USER',@permissions,true,true) returning id
            """,
            new { id = Guid.NewGuid(), username = username.Trim(), displayName = displayName.Trim(), hash = passwords.Hash(password), permissions = NormalizePermissions(permissions) });
    }

    public async Task<int> Update(Guid id, string displayName, bool isActive, IEnumerable<string> permissions, string? newPassword)
    {
        await using var connection = db.Open();
        var count = await connection.ExecuteAsync(
            "update app_users set display_name=@displayName,is_active=@isActive,permissions=@permissions,updated_at=now() where id=@id and role<>'ADMIN'",
            new { id, displayName = displayName.Trim(), isActive, permissions = NormalizePermissions(permissions) });
        if (count > 0 && !string.IsNullOrWhiteSpace(newPassword))
        {
            await connection.ExecuteAsync("update app_users set password_hash=@hash,must_change_password=true where id=@id", new { id, hash = passwords.Hash(newPassword) });
            await connection.ExecuteAsync("delete from auth_sessions where user_id=@id", new { id });
        }
        if (!isActive) await connection.ExecuteAsync("delete from auth_sessions where user_id=@id", new { id });
        return count;
    }

    public async Task<int> Delete(Guid id)
    {
        await using var connection = db.Open();
        return await connection.ExecuteAsync("delete from app_users where id=@id and role<>'ADMIN'", new { id });
    }

    public ClaimsPrincipal Principal(AuthUser user)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username),
            new("display_name", user.DisplayName), new(ClaimTypes.Role, user.Role), new("must_change_password", user.MustChangePassword.ToString().ToLowerInvariant()) };
        if (!user.MustChangePassword)
            claims.AddRange((user.Role == "ADMIN" ? Permissions.All : user.Permissions).Select(x => new Claim("permission", x)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Session"));
    }

    private static AuthUser ToUser(UserRow row) => new(row.Id, row.Username, row.DisplayName, row.Role,
        row.Role == "ADMIN" ? Permissions.All : row.Permissions, row.IsActive, row.MustChangePassword);
    private static string[] NormalizePermissions(IEnumerable<string> values)
    {
        var normalized = values.Distinct().Where(Permissions.All.Contains).ToHashSet();
        if (normalized.Contains(Permissions.HistoryWrite)) normalized.Add(Permissions.HistoryRead);
        if (normalized.Contains(Permissions.PredictionsWrite)) normalized.Add(Permissions.PredictionsRead);
        return normalized.ToArray();
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = "";
        public string[] Permissions { get; set; } = [];
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
    }
}
