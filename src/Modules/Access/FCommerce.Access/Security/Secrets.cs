using System.Security.Cryptography;
using System.Text;

namespace FCommerce.Access.Security;

/// <summary>
/// Password hashing with PBKDF2-SHA256 in a versioned format "v1.{iterations}.{salt}.{hash}" (base64), so the cost can be
/// raised later without invalidating stored hashes.
/// </summary>
internal static class PasswordHashing
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>A valid hash of a random password: verifying against it costs the same as a real check.</summary>
    public static readonly string Decoy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string stored, string password)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>One-time link tokens and security stamps.</summary>
internal static class Tokens
{
    /// <summary>A new token for a link (URL-safe) and the hash to store.</summary>
    public static (string Token, string Hash) New()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, HashOf(token));
    }

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewSecurityStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Material of API keys: a random identifier and secret, and the hash stored for the secret.</summary>
internal static class ApiKeySecrets
{
    /// <summary>A new key: the identifier, the secret, and the hash of the secret to store.</summary>
    public static (string Id, string Secret, string Hash) New()
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Domain.ApiKeyFormat.IdLength / 2));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (id, secret, HashOf(secret));
    }

    public static string HashOf(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The hash a lookup of an unknown identifier is compared against, so the answer takes as long as for a real key.</summary>
    public static readonly string Decoy = HashOf(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    public static bool Matches(string storedHash, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(HashOf(secret)));
}
