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
/// A chunk (or a pack's header) that cannot be read, or does not decrypt, is read again up to Retries times, from the
/// disc itself (past the Windows file cache): a drive or its connection can fail a read once, or hand over wrong bytes,
/// and get it right the next time. Reread names every read that only came right on a later attempt.
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
    Stream? file;
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

    /// <summary>How often a failed read is tried again (0: never).</summary>
    public int Retries { get; init; } = DefaultRetries;
    public const int DefaultRetries = 2;

    /// <summary>A read that came right only on a later attempt (what was wrong the first time).</summary>
    public Action<string>? Reread { get; init; }

    /// <summary>Opens a pack file (path, noCache); tests put a stream there that fails on purpose.</summary>
    public Func<string, bool, Stream> OpenFile { get; init; } = DiscRead.Open;

    /// <summary>wholePacks: the highest pack read and reported so far.</summary>
    public int Loaded => loaded;

    /// <summary>The stored bytes [at, at + length) as a stream.</summary>
    public Stream Open(long at, long length) => new RangeStream(this, at, length);

    /// <summary>wholePacks: reads every pack up to and including `last` that was not read yet.</summary>
    public void ReadThrough(int last)
    {
        while (loaded < last) Load(loaded + 1);
    }

    /// <summary>wholePacks: reads and reports pack `pack` alone, skipping the packs before it (verify --packs).</summary>
    public void ReadPack(int pack)
    {
        if (pack > loaded) Load(pack);
    }

    /// <summary>
    /// SHA-256 of a whole pack file, nothing decrypted (verify without --deep). A read that fails, or gives another hash
    /// than `expected`, is tried again up to `retries` times from the disc itself. Returns the hash of the last read
    /// (null if it failed) and what is wrong (null: the hash is the expected one, or nothing was expected).
    /// </summary>
    public static (string? Sha, string? Problem) HashPack(string path, int pack, string? expected, bool noCache, int retries,
                                                          Action<string>? reread = null, Func<string, bool, Stream>? open = null)
    {
        open ??= DiscRead.Open;
        string? first = null;
        for (int attempt = 0; ; attempt++)
        {
            string? sha = null, problem;
            try
            {
                using (var input = open(path, noCache || attempt > 0)) sha = Hashing.Hash(input, Interrupt.Token);
                problem = expected == null || Hashing.SameHash(sha, expected) ? null : $"pack {pack} differs from the catalog";
            }
            catch (IOException ex) { problem = $"pack {pack} cannot be read ({ex.Message})"; }
            if (problem == null)
            {
                if (first != null) reread?.Invoke($"pack {pack} read correctly on attempt {attempt + 1} (attempt 1: {first})");
                return (sha, null);
            }
            first ??= problem;
            if (attempt >= retries) return (sha, problem);
        }
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
        if (ReadChunk(pack, chunk, file!, sealedChunk, cached, out _) is { } wrong) throw new InvalidDataException(wrong);
        (cachedPack, cachedChunk) = (pack, chunk);
        return cached;
    }

    string PathOf(int pack) => Path.Combine(mount, DiscFormat.PackPath(pack));

    static void ReadAt(Stream input, long offset, byte[] buffer, int pack)
    {
        input.Position = offset;
        if (input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length) throw new InvalidDataException($"pack {pack} is cut short");
    }

    /// <summary>
    /// Reads sealed chunk `chunk` of pack `pack` (the first attempt from `input`, any further one from the disc itself)
    /// and decrypts it into `output`. Returns null, or what was wrong with the last attempt; `read`: its bytes are in
    /// sealedChunk (they were read, whether they decrypt or not).
    /// </summary>
    string? ReadChunk(int pack, int chunk, Stream input, byte[] sealedChunk, Span<byte> output, out bool read)
    {
        long offset = DiscFormat.HeaderSize + (long)chunk * sealedChunk.Length;
        string? first = null;
        for (int attempt = 0; ; attempt++)
        {
            string problem;
            read = false;
            try
            {
                if (attempt == 0) ReadAt(input, offset, sealedChunk, pack);
                else
                {
                    using var again = OpenFile(PathOf(pack), true);
                    ReadAt(again, offset, sealedChunk, pack);
                }
                read = true;
                Decrypt(pack, chunk, sealedChunk, output);
                if (first != null) Reread?.Invoke($"pack {pack}, chunk {chunk} read correctly on attempt {attempt + 1} (attempt 1: {first})");
                return null;
            }
            catch (IOException ex) { problem = $"pack {pack}, chunk {chunk} cannot be read ({ex.Message})"; }
            catch (InvalidDataException ex) { problem = ex.Message; }
            first ??= problem;
            if (attempt >= Retries) return problem;
            Interrupt.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Reads and checks a pack's header (retried like a chunk); adds it to `hash` once it is right.</summary>
    void ReadHeader(int pack, Stream input, IncrementalHash? hash)
    {
        var header = new byte[DiscFormat.HeaderSize];
        string? first = null;
        for (int attempt = 0; ; attempt++)
        {
            string problem;
            try
            {
                if (attempt == 0) ReadAt(input, 0, header, pack);
                else
                {
                    using var again = OpenFile(PathOf(pack), true);
                    ReadAt(again, 0, header, pack);
                }
                UseHeader(pack, header);
                if (first != null) Reread?.Invoke($"pack {pack}, its header read correctly on attempt {attempt + 1} (attempt 1: {first})");
                hash?.AppendData(header);
                return;
            }
            catch (IOException ex) { problem = $"pack {pack} cannot be read ({ex.Message})"; }
            catch (InvalidDataException ex) { problem = ex.Message; }
            first ??= problem;
            if (attempt >= Retries) throw new InvalidDataException(problem);
        }
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
            file = OpenFile(path, false);
            if (file.Length != DiscFormat.PackFileSize) throw new InvalidDataException($"pack {pack} has the wrong size ({file.Length:N0} bytes)");
            ReadHeader(pack, file, null);
        }
        catch (IOException ex)
        {
            file?.Dispose();
            file = null;
            throw new InvalidDataException($"pack {pack} cannot be read ({ex.Message})");
        }
        catch (InvalidDataException)
        {
            file?.Dispose();
            file = null;
            throw;
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
            using var input = OpenFile(path, noCache);
            if (input.Length != DiscFormat.PackFileSize) throw new InvalidDataException($"pack {pack} has the wrong size ({input.Length:N0} bytes)");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ReadHeader(pack, input, hash);
            headerPack = 0;   // the chunk reader opens its own
            var sealedChunk = new byte[DiscFormat.ChunkSize + DiscFormat.TagSize];
            bool complete = true;
            for (int c = 0; c < DiscFormat.ChunksPerPack; c++)
            {
                Interrupt.Token.ThrowIfCancellationRequested();
                var target = loadedPlain.AsSpan(c * DiscFormat.ChunkSize, DiscFormat.ChunkSize);
                // the hash takes the bytes of the last attempt: right bytes that do not decrypt (another key) still match
                string? wrong = ReadChunk(pack, c, input, sealedChunk, target, out bool read);
                if (read) hash.AppendData(sealedChunk);
                else complete = false;
                if (wrong != null)
                {
                    loadedProblems[c] = wrong;
                    problem ??= wrong;
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
