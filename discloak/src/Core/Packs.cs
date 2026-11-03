namespace Discloak;

/// <summary>
/// Writes a disc's data stream into packs (see <see cref="DiscFormat"/>), from pack `firstPack` on: whatever is written
/// goes into the stream at Position, each full chunk is encrypted at once, each full pack is closed and hashed.
/// Finish fills the last pack with zeros. Nothing is ever encrypted twice with the same key and nonce: every pack
/// gets its own random salt, so a pack written again (restage) has a new key.
/// </summary>
public sealed class PackWriter : IDisposable
{
    readonly string dir;
    readonly MasterKey key;
    readonly byte[] plain = new byte[DiscFormat.ChunkSize];
    readonly byte[] cipher = new byte[DiscFormat.ChunkSize];
    readonly byte[] tag = new byte[DiscFormat.TagSize];
    int pack, chunk, filled;
    FileStream? output;
    AesGcm? aes;
    byte[] prefix = [];
    IncrementalHash? hash;

    public PackWriter(string dir, int firstPack, MasterKey key)
    {
        this.dir = dir;
        this.key = key;
        pack = firstPack;
        Position = (firstPack - 1) * DiscFormat.PackData;
    }

    /// <summary>Where the next byte goes in the disc's data stream.</summary>
    public long Position { get; private set; }

    /// <summary>The packs closed so far: number and SHA-256 of the pack file.</summary>
    public List<(int Pack, string Sha256)> Written { get; } = new();

    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (output == null) Open();
            int n = Math.Min(DiscFormat.ChunkSize - filled, data.Length);
            data[..n].CopyTo(plain.AsSpan(filled));
            filled += n;
            Position += n;
            data = data[n..];
            if (filled == DiscFormat.ChunkSize) FlushChunk();
        }
    }

    /// <summary>Fills the pack being written with zeros and closes it (nothing happens at a pack boundary).</summary>
    public void Finish()
    {
        if (output == null) return;
        while (output != null)
        {
            plain.AsSpan(filled).Clear();
            Position += DiscFormat.ChunkSize - filled;
            filled = DiscFormat.ChunkSize;
            FlushChunk();
        }
    }

    void Open()
    {
        string path = Path.Combine(dir, DiscFormat.PackPath(pack));
        Io(path, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        });
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        prefix = RandomNumberGenerator.GetBytes(8);
        byte[] packKey = key.ObjectKey(salt, KeyPurpose.Pack);
        aes = new AesGcm(packKey, DiscFormat.TagSize);
        CryptographicOperations.ZeroMemory(packKey);
        hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Emit(Encoding.ASCII.GetBytes(DiscFormat.Magic));
        Emit(key.KeyIdBytes);
        Emit(salt);
        Emit(prefix);
        chunk = 0;
    }

    void Emit(ReadOnlySpan<byte> bytes)
    {
        try { output!.Write(bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Failed(output!.Name, ex); }
        hash!.AppendData(bytes);
    }

    /// <summary>
    /// A pack that cannot be written (disk full, no access) stops the whole command and nothing is recorded - it is
    /// never mistaken for a source file that could not be read (which only leaves that file out).
    /// </summary>
    static UserException Failed(string path, Exception ex) =>
        new($"Writing {path} failed ({ex.Message}). Nothing was recorded; delete the folder and run the command again.");

    static void Io(string path, Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Failed(path, ex); }
    }

    void FlushChunk()
    {
        aes!.Encrypt(DiscFormat.Nonce(prefix, chunk), plain, cipher, tag, DiscFormat.Aad(pack, chunk));
        Emit(cipher);
        Emit(tag);
        filled = 0;
        if (++chunk < DiscFormat.ChunksPerPack) return;

        var done = output!;
        output = null;
        Io(done.Name, done.Dispose);   // flushes the last bytes
        aes.Dispose();
        aes = null;
        Written.Add((pack, Convert.ToHexString(hash!.GetHashAndReset())));
        hash.Dispose();
        hash = null;
        pack++;
    }

    public void Dispose()
    {
        try { output?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // already failed: that error is the one reported
        aes?.Dispose();
        hash?.Dispose();
        CryptographicOperations.ZeroMemory(plain);
    }
}

/// <summary>
/// Reads byte ranges of a disc's data stream from its packs (a mounted disc or a folder laid out like one), decrypting
/// only the chunks a range needs. A damaged chunk, or one the drive cannot read, fails only the ranges that touch it.
/// wholePacks (verify --deep): every pack is read once, from start to end, hashed, and all of its chunks decrypted;
/// PackRead reports each pack (its hash, or null if it could not be read completely).
/// </summary>
public sealed class PackReader : IDisposable
{
    readonly string mount;
    readonly KeyRing keys;
    readonly bool wholePacks, noCache;
    int cachedPack, cachedChunk = -1;
    readonly byte[] cached = new byte[DiscFormat.ChunkSize];
    int headerPack;
    AesGcm? aes;
    byte[] prefix = [];
    FileStream? file;
    // wholePacks: the highest pack read and reported so far, and the pack whose plaintext is in memory
    int loaded, inMemory;
    byte[]? loadedPlain;
    string?[]? loadedProblems;

    /// <summary>noCache (wholePacks only): the packs are read from the disc itself, not the Windows file cache (UncachedFile).</summary>
    public PackReader(string mount, KeyRing keys, bool wholePacks = false, bool noCache = false)
    {
        this.mount = mount;
        this.keys = keys;
        this.wholePacks = wholePacks;
        this.noCache = noCache;
    }

    /// <summary>wholePacks: called for every pack as it is read: (pack, SHA-256 of the file or null, problem or null).</summary>
    public Action<int, string?, string?>? PackRead { get; init; }

    /// <summary>The stored bytes [at, at + length) as a stream.</summary>
    public Stream Open(long at, long length) => new RangeStream(this, at, length);

    /// <summary>wholePacks: reads every pack up to and including `last` that was not read yet.</summary>
    public void ReadThrough(int last)
    {
        while (loaded < last) Load(loaded + 1);
    }

    /// <summary>Plaintext of chunk `chunk` of pack `pack`. InvalidDataException if it is missing, damaged or unreadable.</summary>
    public ReadOnlySpan<byte> Chunk(int pack, int chunk)
    {
        if (wholePacks)
        {
            if (pack > loaded) ReadThrough(pack);
            else if (pack != inMemory) Load(pack, report: false);   // back to an earlier pack (not in stream order)
            if (loadedProblems![chunk] is { } problem) throw new InvalidDataException(problem);
            return loadedPlain.AsSpan(chunk * DiscFormat.ChunkSize, DiscFormat.ChunkSize);
        }
        if (pack == cachedPack && chunk == cachedChunk) return cached;
        cachedChunk = -1;
        OpenPack(pack);
        var sealedChunk = new byte[DiscFormat.ChunkSize + DiscFormat.TagSize];
        try
        {
            file!.Position = DiscFormat.HeaderSize + (long)chunk * sealedChunk.Length;
            if (file.ReadAtLeast(sealedChunk, sealedChunk.Length, throwOnEndOfStream: false) != sealedChunk.Length)
                throw new InvalidDataException($"pack {pack} is cut short");
        }
        catch (IOException ex) { throw new InvalidDataException($"pack {pack}, chunk {chunk} cannot be read ({ex.Message})"); }
        Decrypt(pack, chunk, sealedChunk, cached);
        (cachedPack, cachedChunk) = (pack, chunk);
        return cached;
    }

    void OpenPack(int pack)
    {
        if (headerPack == pack && file != null) return;
        file?.Dispose();
        file = null;
        aes?.Dispose();
        aes = null;
        headerPack = 0;
        string path = Path.Combine(mount, DiscFormat.PackPath(pack));
        if (!File.Exists(path)) throw new InvalidDataException($"pack {pack} is missing ({DiscFormat.PackPath(pack)})");
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            if (file.Length != DiscFormat.PackFileSize) throw new InvalidDataException($"pack {pack} has the wrong size ({file.Length:N0} bytes)");
            var header = new byte[DiscFormat.HeaderSize];
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) throw new InvalidDataException($"pack {pack} is cut short");
            UseHeader(pack, header);
        }
        catch (IOException ex)
        {
            file?.Dispose();
            file = null;
            throw new InvalidDataException($"pack {pack} cannot be read ({ex.Message})");
        }
        headerPack = pack;
    }

    void UseHeader(int pack, byte[] header)
    {
        if (!DiscFormat.IsMagic(header.AsSpan(0, 8))) throw new InvalidDataException($"pack {pack} is not a discloak pack");
        var keyId = header.AsSpan(8, MasterKey.KeyIdSize);
        var key = keys.Find(keyId) ?? throw new InvalidDataException($"pack {pack} is encrypted with another key (key id {Convert.ToHexString(keyId)})");
        byte[] packKey = key.ObjectKey(header.AsSpan(16, 32), KeyPurpose.Pack);
        aes?.Dispose();
        aes = new AesGcm(packKey, DiscFormat.TagSize);
        CryptographicOperations.ZeroMemory(packKey);
        prefix = header.AsSpan(48, 8).ToArray();
    }

    void Decrypt(int pack, int chunk, ReadOnlySpan<byte> sealedChunk, Span<byte> output)
    {
        try
        {
            aes!.Decrypt(DiscFormat.Nonce(prefix, chunk), sealedChunk[..DiscFormat.ChunkSize], sealedChunk[DiscFormat.ChunkSize..],
                         output, DiscFormat.Aad(pack, chunk));
        }
        catch (CryptographicException) { throw new InvalidDataException($"pack {pack}, chunk {chunk} is damaged"); }
    }

    /// <summary>wholePacks: reads pack `pack` from start to end (each chunk on its own, so an unreadable spot costs only its chunks).</summary>
    void Load(int pack, bool report = true)
    {
        loadedPlain ??= new byte[DiscFormat.PackData];
        loadedProblems = new string?[DiscFormat.ChunksPerPack];
        string? problem = null, sha = null;
        string path = Path.Combine(mount, DiscFormat.PackPath(pack));
        try
        {
            if (!File.Exists(path)) throw new InvalidDataException($"pack {pack} is missing ({DiscFormat.PackPath(pack)})");
            using var input = DiscRead.Open(path, noCache);
            if (input.Length != DiscFormat.PackFileSize) throw new InvalidDataException($"pack {pack} has the wrong size ({input.Length:N0} bytes)");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var header = new byte[DiscFormat.HeaderSize];
            if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) throw new InvalidDataException($"pack {pack} is cut short");
            hash.AppendData(header);
            UseHeader(pack, header);
            headerPack = 0;   // the chunk reader opens its own
            var sealedChunk = new byte[DiscFormat.ChunkSize + DiscFormat.TagSize];
            bool complete = true;
            for (int c = 0; c < DiscFormat.ChunksPerPack; c++)
            {
                Interrupt.Token.ThrowIfCancellationRequested();
                var target = loadedPlain.AsSpan(c * DiscFormat.ChunkSize, DiscFormat.ChunkSize);
                try
                {
                    input.Position = DiscFormat.HeaderSize + (long)c * sealedChunk.Length;
                    if (input.ReadAtLeast(sealedChunk, sealedChunk.Length, throwOnEndOfStream: false) != sealedChunk.Length)
                        throw new InvalidDataException($"pack {pack} is cut short");
                    hash.AppendData(sealedChunk);
                    Decrypt(pack, c, sealedChunk, target);
                }
                catch (IOException ex)
                {
                    complete = false;
                    loadedProblems[c] = $"pack {pack}, chunk {c} cannot be read ({ex.Message})";
                    problem ??= loadedProblems[c];
                }
                catch (InvalidDataException ex)
                {
                    loadedProblems[c] = ex.Message;
                    problem ??= ex.Message;
                }
            }
            if (complete) sha = Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (InvalidDataException ex)
        {
            Array.Fill(loadedProblems, ex.Message);
            problem = ex.Message;
        }
        catch (IOException ex)
        {
            Array.Fill(loadedProblems, $"pack {pack} cannot be read ({ex.Message})");
            problem = loadedProblems[0];
        }
        inMemory = pack;
        if (!report) return;
        loaded = pack;
        PackRead?.Invoke(pack, sha, problem);
    }

    public void Dispose()
    {
        file?.Dispose();
        aes?.Dispose();
    }

    /// <summary>A range of the data stream, read forward.</summary>
    sealed class RangeStream(PackReader reader, long at, long length) : Stream
    {
        long position;

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && position < length)
            {
                long abs = at + position;
                int pack = DiscFormat.PackOf(abs);
                long inPack = abs - (pack - 1) * DiscFormat.PackData;
                int chunk = (int)(inPack / DiscFormat.ChunkSize), offset = (int)(inPack % DiscFormat.ChunkSize);
                int n = (int)Math.Min(Math.Min(buffer.Length, DiscFormat.ChunkSize - offset), length - position);
                reader.Chunk(pack, chunk).Slice(offset, n).CopyTo(buffer);
                buffer = buffer[n..];
                position += n;
                total += n;
            }
            return total;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
