using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bdxl;

/// <summary>
/// Reading a disc's files the usual way - through the Windows file cache, which keeps what was read from a disc in
/// spare memory until the disc is ejected - or, with noCache ('verify --no-cache'), from the disc itself.
/// </summary>
public static class DiscRead
{
    public static Stream Open(string path, bool noCache) =>
        noCache ? new UncachedFile(path) : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

    public static byte[] AllBytes(string path, bool noCache)
    {
        if (!noCache) return File.ReadAllBytes(path);
        using var file = new UncachedFile(path);
        var data = new byte[file.Length];
        file.ReadExactly(data);
        return data;
    }
}

/// <summary>
/// A file read past the Windows file cache (FILE_FLAG_NO_BUFFERING): every read goes to the device, and nothing read is
/// kept in memory. Such reads must start and end on sector boundaries and go to sector-aligned memory, so the file is
/// read in aligned blocks (default 1 MiB; any multiple of 4 KiB fits every sector size: 512, 2048 on optical discs,
/// 4096) and served from there. A block that cannot be read fails only the reads that need it.
/// </summary>
public sealed class UncachedFile : Stream
{
    const FileOptions NoBuffering = (FileOptions)0x20000000;
    public const int Alignment = 4096;
    readonly SafeFileHandle handle;
    readonly byte[] memory;   // pinned, so the aligned part never moves
    readonly int aligned, blockSize;
    long blockAt = -1;        // where the block in memory starts in the file (-1: none)
    int blockLength;
    long position;

    public UncachedFile(string path, int blockSize = 1 << 20)
    {
        if (blockSize <= 0 || blockSize % Alignment != 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "a multiple of 4 KiB");
        this.blockSize = blockSize;
        handle = OpenHandle(path);
        Length = RandomAccess.GetLength(handle);
        memory = GC.AllocateUninitializedArray<byte>(blockSize + Alignment, pinned: true);
        long address = Marshal.UnsafeAddrOfPinnedArrayElement(memory, 0);
        aligned = (int)((Alignment - address % Alignment) % Alignment);
    }

    /// <summary>A handle that reads past the cache: only whole, aligned sectors into aligned memory.</summary>
    public static SafeFileHandle OpenHandle(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);

    public override long Length { get; }

    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || position >= Length) return 0;
        long at = position / blockSize * blockSize;
        if (at != blockAt)
        {
            blockAt = -1;   // a read that fails leaves no block behind
            blockLength = RandomAccess.Read(handle, memory.AsSpan(aligned, blockSize), at);
            blockAt = at;
        }
        int offset = (int)(position - at), n = Math.Min(buffer.Length, blockLength - offset);
        if (n <= 0) return 0;
        memory.AsSpan(aligned + offset, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => Length + offset
    };

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) handle.Dispose();
        base.Dispose(disposing);
    }
}
