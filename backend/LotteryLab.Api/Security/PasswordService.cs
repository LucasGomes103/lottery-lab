using System.Security.Cryptography;

namespace LotteryLab.Api.Security;

public sealed class PasswordService
{
    private const int Iterations = 210_000;
    // Hash de uma senha aleatória, para que o login de usuário inexistente custe o mesmo que o de um real.
    private readonly string dummyHash;

    public PasswordService() => dummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public bool VerifyDummy(string password) => Verify(password, dummyHash);

    public static string? Validate(string password)
    {
        if (password.Length < 12) return "A senha deve possuir pelo menos 12 caracteres.";
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || password.All(char.IsLetterOrDigit))
            return "A senha deve combinar letras maiúsculas, minúsculas, números e símbolo.";
        return null;
    }
}
