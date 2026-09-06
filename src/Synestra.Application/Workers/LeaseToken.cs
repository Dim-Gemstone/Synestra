using System.Security.Cryptography;

namespace Synestra.Application.Workers;

public static class LeaseToken
{
    public static string Generate() => Encode(RandomNumberGenerator.GetBytes(32));

    public static byte[]? Hash(string? token)
    {
        if (token is null || token.Length != 43
            || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return null;
        }

        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        return StringComparer.Ordinal.Equals(Encode(bytes), token) ? SHA256.HashData(bytes) : null;
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
