namespace Bdxl;

/// <summary>
/// Password rules: at least 12 characters and at least 3 of the 4 classes lowercase / uppercase / digit / special character.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    public static bool Check(string password, out string problem)
    {
        if (password.Length < MinLength)
        {
            problem = $"The password must be at least {MinLength} characters long (it has {password.Length}).";
            return false;
        }

        int classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(ch => !char.IsLetterOrDigit(ch))) classes++;
        if (classes < 3)
        {
            problem = "The password must use at least 3 of: lowercase letters, uppercase letters, digits, special characters.";
            return false;
        }
        problem = "";
        return true;
    }
}

/// <summary>
/// Format "BDXLENC2" (see ENCRYPTION.md) - one encrypted object per file, keyed by the catalog's master key:
///   magic "BDXLENC2"(8) | key id(8) | object salt(32) | nonce prefix(8) |
///   chunks: length(uint32) | AES-256-GCM ciphertext(length) | tag(16)
/// Key of the object = HKDF(master key, object salt, "bdxl-enc2 " + purpose). Each chunk's plaintext is mode(1) + up to
/// 1 MiB of data (mode 0 = stored; 1 = zstd, reserved for compression). The nonce is prefix + chunk number; the chunk
/// number and a "last chunk" flag are authenticated, so reordering, cutting off or extending is detected.
/// </summary>
public static class FileCrypto
{
    public const string Suffix = ".enc";
    const string Magic = "BDXLENC2";
    const int ChunkSize = 1 << 20;
    const int TagSize = 16;
    public const int HeaderSize = 8 + MasterKey.KeyIdSize + 32 + 8;
    const int ChunkOverhead = 4 + 1 + TagSize;
    const byte ModeStored = 0;

    static byte[] Nonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[12];
        Buffer.BlockCopy(prefix, 0, nonce, 0, 8);
        BitConverter.GetBytes(counter).CopyTo(nonce, 8);
        return nonce;
    }

    static byte[] Aad(uint counter, bool last)
    {
        var aad = new byte[5];
        BitConverter.GetBytes(counter).CopyTo(aad, 0);
        aad[4] = last ? (byte)1 : (byte)0;
        return aad;
    }

    static int ReadFull(Stream s, byte[] buffer, int count) => ReadFull(s, buffer, 0, count);

    static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Encrypts src (or length bytes from offset - one part of a split file) into dst (which must not exist).
    /// Returns the SHA-256 of the PLAINTEXT that was read. length -1 = to the end.
    /// </summary>
    public static string Encrypt(string srcPath, string dstPath, MasterKey key, string purpose, long offset = 0, long length = -1,
                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        using var input = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        using var output = new FileStream(dstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
        input.Seek(offset, SeekOrigin.Begin);
        return Encrypt(input, output, key, purpose, length, ct);
    }

    /// <summary>Encrypts length bytes (-1 = to the end) from the current position of input. Returns the SHA-256 of the data read.</summary>
    public static string Encrypt(Stream input, Stream output, MasterKey key, string purpose, long length = -1, CancellationToken ct = default)
    {
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = length < 0 ? long.MaxValue : length;
        int ReadNext(byte[] buffer)
        {
            int got = ReadFull(input, buffer, 1, (int)Math.Min(ChunkSize, remaining));
            remaining -= got;
            return got;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(32), prefix = RandomNumberGenerator.GetBytes(8);
        output.Write(Encoding.ASCII.GetBytes(Magic));
        output.Write(key.KeyIdBytes);
        output.Write(salt);
        output.Write(prefix);

        byte[] objectKey = key.ObjectKey(salt, purpose);
        using var aes = new AesGcm(objectKey, TagSize);
        CryptographicOperations.ZeroMemory(objectKey);
        var current = new byte[1 + ChunkSize];
        var next = new byte[1 + ChunkSize];
        int currentLen = ReadNext(current);
        uint counter = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // only a full chunk can be followed by more data; read ahead so we know which chunk is the last one
            int nextLen = currentLen == ChunkSize ? ReadNext(next) : 0;
            bool last = nextLen == 0;

            plainHash.AppendData(current, 1, currentLen);
            current[0] = ModeStored;
            var cipher = new byte[1 + currentLen];
            var tag = new byte[TagSize];
            aes.Encrypt(Nonce(prefix, counter), current.AsSpan(0, 1 + currentLen), cipher, tag, Aad(counter, last));
            output.Write(BitConverter.GetBytes((uint)cipher.Length));
            output.Write(cipher);
            output.Write(tag);

            if (last) break;
            (current, next) = (next, current);
            currentLen = nextLen;
            counter++;
        }
        return Convert.ToHexString(plainHash.GetHashAndReset());
    }

    /// <summary>Decrypts src into dst (which must not exist). Returns the plaintext's SHA-256; InvalidDataException on another key, damage or tampering.</summary>
    public static string Decrypt(string srcPath, string dstPath, MasterKey key, string purpose, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        using var input = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        using var output = new FileStream(dstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
        return Decrypt(input, output, key, purpose, ct);
    }

    /// <summary>Decrypts a whole BDXLENC2 stream (seekable: the last chunk is recognised by its position).</summary>
    public static string Decrypt(Stream input, Stream output, MasterKey key, string purpose, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (ReadFull(input, header, header.Length) != header.Length || Encoding.ASCII.GetString(header, 0, 8) != Magic)
            throw new InvalidDataException("not a bdxl-encrypted file");
        var keyId = header.AsSpan(8, MasterKey.KeyIdSize);
        if (!keyId.SequenceEqual(key.KeyIdBytes))
            throw new InvalidDataException($"encrypted with another key (key id {Convert.ToHexString(keyId)}, not {key.KeyId})");
        var salt = header.AsSpan(16, 32);
        var prefix = header.AsSpan(48, 8).ToArray();

        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] objectKey = key.ObjectKey(salt, purpose);
        using var aes = new AesGcm(objectKey, TagSize);
        CryptographicOperations.ZeroMemory(objectKey);
        var lengthBytes = new byte[4];
        var tag = new byte[TagSize];
        uint counter = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFull(input, lengthBytes, 4) != 4) throw new InvalidDataException("file is truncated");
            uint length = BitConverter.ToUInt32(lengthBytes, 0);
            if (length < 1 || length > 1 + ChunkSize) throw new InvalidDataException("corrupt chunk length");

            var cipher = new byte[length];
            if (ReadFull(input, cipher, (int)length) != (int)length || ReadFull(input, tag, TagSize) != TagSize)
                throw new InvalidDataException("file is truncated");
            bool last = input.Position == input.Length;

            var plain = new byte[length];
            try
            {
                aes.Decrypt(Nonce(prefix, counter), cipher, tag, plain, Aad(counter, last));
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("the file is damaged or was altered");
            }
            if (plain[0] != ModeStored)
                throw new InvalidDataException($"chunk {counter} is stored in mode {plain[0]}, which this version of bdxl cannot read");
            plainHash.AppendData(plain, 1, plain.Length - 1);
            output.Write(plain, 1, plain.Length - 1);

            if (last) break;
            counter++;
        }
        return Convert.ToHexString(plainHash.GetHashAndReset());
    }

    /// <summary>Encrypts small data (catalog files, manifests) in memory, so its plaintext never touches a disk.</summary>
    public static byte[] Seal(byte[] plain, MasterKey key, string purpose)
    {
        using var output = new MemoryStream();
        Encrypt(new MemoryStream(plain, writable: false), output, key, purpose);
        return output.ToArray();
    }

    /// <summary>The plaintext of Seal's output. InvalidDataException on another key, damage or tampering.</summary>
    public static byte[] Open(byte[] stored, MasterKey key, string purpose)
    {
        using var output = new MemoryStream();
        Decrypt(new MemoryStream(stored, writable: false), output, key, purpose);
        return output.ToArray();
    }

    /// <summary>The key id in an encrypted file's header, or null if it is not a BDXLENC2 file.</summary>
    public static string? KeyIdOf(string path)
    {
        var header = new byte[16];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (ReadFull(input, header, 16) != 16 || Encoding.ASCII.GetString(header, 0, 8) != Magic) return null;
        return Convert.ToHexString(header, 8, MasterKey.KeyIdSize);
    }

    /// <summary>Bytes an encrypted copy of a file of this size occupies on the disc (header + 21 bytes per 1 MiB chunk).</summary>
    public static long StoredSize(long plainSize)
    {
        long chunks = Math.Max(1, (plainSize + ChunkSize - 1) / ChunkSize);
        return HeaderSize + plainSize + chunks * ChunkOverhead;
    }

    /// <summary>Largest plaintext whose encrypted copy still fits in storedSize bytes (the inverse of StoredSize).</summary>
    public static long MaxPlainSize(long storedSize)
    {
        if (storedSize < StoredSize(0)) return 0;
        long chunks = Math.Max(1, (storedSize - HeaderSize + ChunkSize + ChunkOverhead - 1) / (ChunkSize + ChunkOverhead));
        long plain = Math.Min(storedSize - HeaderSize - chunks * ChunkOverhead, chunks * ChunkSize);
        while (plain > 0 && StoredSize(plain) > storedSize) plain--;
        return Math.Max(0, plain);
    }
}
