namespace Discloak;

public static class Hashing
{
    const int BufferSize = 1 << 20;

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>The same, telling progress how many bytes were read so far after every block (progress of large files).</summary>
    public static string HashFile(string path, Action<long> progress)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return Hash(fs, progress);
    }

    /// <summary>The same, stopping with OperationCanceledException when ct is cancelled (checked every block).</summary>
    public static string HashFile(string path, CancellationToken ct) => HashFile(path, _ => ct.ThrowIfCancellationRequested());

    /// <summary>SHA-256 of a stream from its position to its end; progress gets the bytes read so far after every block.</summary>
    public static string Hash(Stream input, Action<long> progress)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long read = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            read += n;
            progress(read);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string Hash(Stream input, CancellationToken ct) => Hash(input, _ => ct.ThrowIfCancellationRequested());

    public static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// Copies a file (or length bytes from offset - one part of a split file) and hashes the bytes that were actually
    /// written, in a single pass. Fails if dst exists. length -1 = to the end. ct is checked every block.
    /// </summary>
    public static string CopyAndHash(string src, string dst, long offset = 0, long length = -1, CancellationToken ct = default)
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
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buf, 0, n);
            output.Write(buf, 0, n);
            remaining -= n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
