using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace TelemetryGuard.MigrationRunner.Provisioning;

/// <summary>
/// Key formats pinned by DAT-09:
///   API key : "tg_ak_" + 43 base62 chars over 32 random bytes (49 chars total)
///   Site key: "tg_sk_" + 22 base62 chars over 16 random bytes (28 chars total)
/// Raw API keys are printed ONCE and never stored: dbo.ApiKeys.KeyHash is
/// SHA-256 over the UTF-8 bytes of the FULL raw key string, prefix included
/// (matches DAT-04's resolver and DAT-08's seed convention).
/// </summary>
public static class KeyGenerator
{
    public const string ApiKeyPrefix = "tg_ak_";
    public const string SiteKeyPrefix = "tg_sk_";
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string NewApiKey() => ApiKeyPrefix + ToBase62(RandomNumberGenerator.GetBytes(32), 43);
    public static string NewSiteKey() => SiteKeyPrefix + ToBase62(RandomNumberGenerator.GetBytes(16), 22);

    public static byte[] Sha256(string rawKey) => SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));

    internal static string ToBase62(byte[] bytes, int outputLength)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[outputLength];
        for (var i = outputLength - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var rem);
            chars[i] = Alphabet[(int)rem];
        }
        return new string(chars); // value is 0 here: 62^43 > 2^256 and 62^22 > 2^128
    }
}
