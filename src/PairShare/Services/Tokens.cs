using System.Security.Cryptography;
using System.Text;

namespace PairShare.Services;

internal static class Tokens
{
    /// <summary>URL-safe random token with <paramref name="bytes"/> bytes of entropy.</summary>
    public static string New(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string ShortId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    public static string Digits(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(chars);
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
