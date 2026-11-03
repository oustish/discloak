namespace Discloak;

/// <summary>
/// A write-only stream that parses JSON lines as they arrive (from FileCrypto.Decrypt), so a large catalog file is never
/// held whole in memory: only the line being completed is buffered. Call Complete() after the last write.
/// skipIfFirst: when the data starts with these bytes it is not JSON lines at all (the too-old marker) and is ignored.
/// </summary>
public sealed class JsonLinesSink<T> : Stream
{
    readonly string name;
    readonly byte[]? skipIfFirst;
    byte[] carry = new byte[64 * 1024];   // the start of a line whose end has not arrived yet
    int carried, lineNo;
    bool started, skipping;

    public List<T> Items { get; } = new();

    public JsonLinesSink(string name, ReadOnlySpan<byte> skipIfFirst = default)
    {
        this.name = name;
        this.skipIfFirst = skipIfFirst.IsEmpty ? null : skipIfFirst.ToArray();
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (!started && skipIfFirst != null)
        {
            // the marker is far shorter than the first block written, so the first write decides
            started = true;
            skipping = data.StartsWith(skipIfFirst);
        }
        started = true;
        if (skipping) return;

        while (!data.IsEmpty)
        {
            int end = data.IndexOf((byte)'\n');
            if (end < 0) { Carry(data); return; }
            if (carried > 0)
            {
                Carry(data[..end]);
                Line(carry.AsSpan(0, carried));
                carried = 0;
            }
            else Line(data[..end]);
            data = data[(end + 1)..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>The last line, if the data did not end with a newline.</summary>
    public List<T> Complete()
    {
        if (carried > 0 && !skipping) Line(carry.AsSpan(0, carried));
        carried = 0;
        return Items;
    }

    void Carry(ReadOnlySpan<byte> part)
    {
        if (carried + part.Length > carry.Length) Array.Resize(ref carry, Math.Max(carry.Length * 2, carried + part.Length));
        part.CopyTo(carry.AsSpan(carried));
        carried += part.Length;
    }

    void Line(ReadOnlySpan<byte> line)
    {
        lineNo++;
        if (line.Trim((ReadOnlySpan<byte>)[(byte)' ', (byte)'\t', (byte)'\r']).IsEmpty) return;
        try
        {
            var item = JsonStore.DeserializeLine<T>(line);
            if (item != null) Items.Add(item);
        }
        catch (JsonException ex) { throw new JsonException($"{name} line {lineNo}: {ex.Message}"); }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A write-only stream that only hashes what is written (SHA-256): to learn whether serialized data changed without keeping it.</summary>
public sealed class HashingSink : Stream
{
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public string Hex() => Convert.ToHexString(hash.GetHashAndReset());

    public override void Write(ReadOnlySpan<byte> data) => hash.AppendData(data);
    public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
