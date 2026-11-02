namespace Bdxl;

/// <summary>
/// How a file (or a part of one) is laid into a disc's data stream: its bytes as they are, or - compressed - one zstd
/// frame per 1 MiB of the file, back to back: a standard zstd stream ('zstd -d' reads it). zstd gives the same frames
/// for the same data and level, so 'scan' can count the stored size that 'stage' will write.
/// </summary>
public static class StoredForm
{
    public const int Block = 1 << 20;

    /// <summary>Thrown when the data would take more room than planned: the file changed since it was scanned.</summary>
    public sealed class TooLargeException() : Exception("larger than when it was scanned");

    /// <summary>
    /// Reads `length` bytes from src (from its position) and writes them in stored form to output - never more than
    /// `limit` bytes (TooLargeException). Returns the SHA-256 of the bytes read and the stored size.
    /// A source that ends early gives fewer bytes (and another hash).
    /// </summary>
    public static (string Sha256, long Stored) Write(Stream src, long length, int level, Action<ReadOnlySpan<byte>> output, long limit,
                                                     CancellationToken ct = default, Action<long>? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var zstd = level > 0 ? new ZstdSharp.Compressor(level) : null;
        var block = new byte[Block];
        long read = 0, stored = 0;
        while (read < length)
        {
            ct.ThrowIfCancellationRequested();
            int n = src.ReadAtLeast(block.AsSpan(0, (int)Math.Min(Block, length - read)), (int)Math.Min(Block, length - read), throwOnEndOfStream: false);
            if (n == 0) break;
            hash.AppendData(block, 0, n);
            ReadOnlySpan<byte> bytes = zstd != null ? zstd.Wrap(block.AsSpan(0, n)) : block.AsSpan(0, n);
            if (stored + bytes.Length > limit) throw new TooLargeException();
            output(bytes);
            stored += bytes.Length;
            read += n;
            progress?.Invoke(read);
        }
        return (Convert.ToHexString(hash.GetHashAndReset()), stored);
    }

    /// <summary>What 'scan' needs in one read: the file's SHA-256 and the size of its stored form at `level` (progress: bytes read).</summary>
    public static (string Sha256, long Stored) HashAndMeasure(string path, int level, Action<long> progress)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Block, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var zstd = new ZstdSharp.Compressor(level);
        var block = new byte[Block];
        long read = 0, stored = 0;
        int n;
        while ((n = input.ReadAtLeast(block, Block, throwOnEndOfStream: false)) > 0)
        {
            hash.AppendData(block, 0, n);
            stored += zstd.Wrap(block.AsSpan(0, n)).Length;
            read += n;
            progress(read);
            if (n < Block) break;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()), stored);
    }

    /// <summary>Turns the stored form back into the original bytes (written to output); returns their SHA-256.</summary>
    public static string Read(Stream stored, int level, Stream output, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream source = stored;
        ZstdSharp.DecompressionStream? zstd = null;
        if (level > 0 && stored.Length > 0) source = zstd = new ZstdSharp.DecompressionStream(stored, leaveOpen: true);
        try
        {
            var buffer = new byte[Block];
            int n;
            while ((n = Fill(source, buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, n);
                output.Write(buffer, 0, n);
            }
        }
        catch (Exception ex) when (ex is ZstdSharp.ZstdException or EndOfStreamException)
        {
            throw new InvalidDataException($"the stored copy does not decompress ({ex.Message})");
        }
        finally { zstd?.Dispose(); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static int Fill(Stream s, byte[] buffer) => s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
}
