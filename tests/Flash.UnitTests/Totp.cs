using System.Security.Cryptography;

namespace Flash.UnitTests;

/// <summary>RFC 6238 (SHA-1, 30 seconds, 6 digits), enough to play the part of an authenticator app.</summary>
public static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var message = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
