namespace Bdxl;

public static class Hashing
{
    const int BufferSize = 1 << 20;

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// Copies a file (or length bytes from offset - one part of a split file) and hashes the bytes that were actually
    /// written, in a single pass. Fails if dst exists. length -1 = to the end.
    /// </summary>
    public static string CopyAndHash(string src, string dst, long offset = 0, long length = -1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        input.Seek(offset, SeekOrigin.Begin);
        long remaining = length < 0 ? long.MaxValue : length;
        var buf = new byte[BufferSize];
        int n;
        while (remaining > 0 && (n = input.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
        {
            hash.AppendData(buf, 0, n);
            output.Write(buf, 0, n);
            remaining -= n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
