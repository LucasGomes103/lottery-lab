namespace LotteryLab.Api.Models;

public record LoginRequest(string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record CreateUserRequest(string Username, string DisplayName, string Password, List<string> Permissions);
public record UpdateUserRequest(string DisplayName, bool IsActive, List<string> Permissions, string? NewPassword);

