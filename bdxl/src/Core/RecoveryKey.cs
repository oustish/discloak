namespace Bdxl;

/// <summary>
/// The optional recovery key: 32 random bytes, shown once as text. The text is the key plus 2 checksum bytes (start of
/// its SHA-256) in Crockford base32, in groups of 5. Reading it forgives case, spaces, O for 0 and I/L for 1, and a
/// typo fails the checksum instead of looking like a wrong key.
/// </summary>
public static class RecoveryKey
{
    public const int Size = 32;
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static byte[] New() => RandomNumberGenerator.GetBytes(Size);

    public static string Format(ReadOnlySpan<byte> key)
    {
        var data = new byte[Size + 2];
        key.CopyTo(data);
        SHA256.HashData(key).AsSpan(0, 2).CopyTo(data.AsSpan(Size));

        var sb = new StringBuilder();
        int bits = 0, buffer = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return string.Join("-", sb.ToString().Chunk(5).Select(c => new string(c)));
    }

    /// <summary>The key in text, or null with the reason.</summary>
    public static byte[]? Parse(string text, out string problem)
    {
        var chars = text.ToUpperInvariant().Where(ch => ch is not ('-' or ' ' or '\t')).Select(ch => ch switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            _ => ch
        }).ToArray();
        int expected = ((Size + 2) * 8 + 4) / 5;
        if (chars.Length != expected) { problem = $"a recovery key has {expected} characters (this has {chars.Length})"; return null; }

        var data = new byte[Size + 2];
        int bits = 0, buffer = 0, n = 0;
        foreach (char ch in chars)
        {
            int v = Alphabet.IndexOf(ch);
            if (v < 0) { problem = $"'{ch}' cannot be part of a recovery key"; return null; }
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8 && n < data.Length) { data[n++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        var key = data.AsSpan(0, Size).ToArray();
        if (!SHA256.HashData(key).AsSpan(0, 2).SequenceEqual(data.AsSpan(Size)))
        {
            problem = "the recovery key is mistyped (its checksum does not match)";
            return null;
        }
        problem = "";
        return key;
    }
}
