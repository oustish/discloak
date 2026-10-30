namespace Bdxl;

/// <summary>
/// Password rules for encrypting files: at least 12 characters and at least 3 of the 4 classes
/// lowercase / uppercase / digit / special character.
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

/// <summary>Derives (and remembers) the AES key for a password + salt, so thousands of files do not each pay for PBKDF2.</summary>
public sealed class KeyCache
{
    readonly string password;
    readonly Dictionary<string, byte[]> keys = new();

    public KeyCache(string password) { this.password = password; }

    public byte[] GetKey(byte[] salt, int iterations)
    {
        string id = Convert.ToHexString(salt) + ":" + iterations;
        if (!keys.TryGetValue(id, out var key))
        {
            key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            keys[id] = key;
        }
        return key;
    }
}

/// <summary>
/// File format "BDXLENC1" (no compression, no archive - one encrypted file per original file):
///   magic(8) | PBKDF2 iterations (int32) | salt(16) | nonce prefix(8) |
///   chunks: length(uint32) | AES-256-GCM ciphertext(length) | tag(16)
/// Chunks are 1 MiB of plaintext. The nonce is prefix + chunk number; the chunk number and a "last chunk" flag are
/// authenticated, so reordering, cutting off or extending a file is detected as well as a wrong password.
/// </summary>
public static class FileCrypto
{
    public const string Suffix = ".enc";
    const string Magic = "BDXLENC1";
    const int ChunkSize = 1 << 20;
    const int TagSize = 16;
    public const int DefaultIterations = 600_000;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

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
    public static string Encrypt(string srcPath, string dstPath, KeyCache keys, byte[] salt, long offset = 0, long length = -1,
                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        using var input = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        using var output = new FileStream(dstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
        input.Seek(offset, SeekOrigin.Begin);
        return Encrypt(input, output, keys, salt, length, ct);
    }

    /// <summary>Encrypts small metadata (manifest, catalog backup) without its plaintext ever touching a disk.</summary>
    public static byte[] EncryptBytes(byte[] plain, KeyCache keys, byte[] salt)
    {
        using var output = new MemoryStream();
        Encrypt(new MemoryStream(plain, writable: false), output, keys, salt);
        return output.ToArray();
    }

    /// <summary>Encrypts length bytes (-1 = to the end) from the current position of input. Returns the plaintext's SHA-256.</summary>
    public static string Encrypt(Stream input, Stream output, KeyCache keys, byte[] salt, long length = -1, CancellationToken ct = default)
    {
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = length < 0 ? long.MaxValue : length;
        int ReadNext(byte[] buffer)
        {
            int got = ReadFull(input, buffer, (int)Math.Min(ChunkSize, remaining));
            remaining -= got;
            return got;
        }

        byte[] key = keys.GetKey(salt, DefaultIterations);
        byte[] prefix = RandomNumberGenerator.GetBytes(8);
        output.Write(Encoding.ASCII.GetBytes(Magic));
        output.Write(BitConverter.GetBytes(DefaultIterations));
        output.Write(salt);
        output.Write(prefix);

        using var aes = new AesGcm(key, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        int currentLen = ReadNext(current);
        uint counter = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // only a full chunk can be followed by more data; read ahead so we know which chunk is the last one
            int nextLen = currentLen == ChunkSize ? ReadNext(next) : 0;
            bool last = nextLen == 0;

            plainHash.AppendData(current, 0, currentLen);
            var cipher = new byte[currentLen];
            var tag = new byte[TagSize];
            aes.Encrypt(Nonce(prefix, counter), current.AsSpan(0, currentLen), cipher, tag, Aad(counter, last));
            output.Write(BitConverter.GetBytes((uint)currentLen));
            output.Write(cipher);
            output.Write(tag);

            if (last) break;
            (current, next) = (next, current);
            currentLen = nextLen;
            counter++;
        }
        return Convert.ToHexString(plainHash.GetHashAndReset());
    }

    /// <summary>Decrypts src into dst (which must not exist). Returns the SHA-256 of the plaintext. Throws InvalidDataException on a wrong password or damage.</summary>
    public static string Decrypt(string srcPath, string dstPath, KeyCache keys, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        using var input = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        using var output = new FileStream(dstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
        return Decrypt(input, output, keys, firstChunkOnly: false, ct);
    }

    /// <summary>Decrypts small metadata in memory. Throws InvalidDataException on a wrong password or damage.</summary>
    public static byte[] DecryptBytes(byte[] stored, KeyCache keys)
    {
        using var output = new MemoryStream();
        Decrypt(new MemoryStream(stored, writable: false), output, keys);
        return output.ToArray();
    }

    /// <summary>Decrypts a whole BDXLENC1 stream (input must be seekable: the last chunk is recognised by its position).</summary>
    public static string Decrypt(Stream input, Stream output, KeyCache keys, CancellationToken ct = default) =>
        Decrypt(input, output, keys, firstChunkOnly: false, ct);

    /// <summary>True if the password opens this encrypted file (only its first chunk is decrypted, so this is quick).</summary>
    public static bool OpensWith(string path, KeyCache keys)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Decrypt(input, Stream.Null, keys, firstChunkOnly: true, CancellationToken.None);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    static string Decrypt(Stream input, Stream output, KeyCache keys, bool firstChunkOnly, CancellationToken ct)
    {
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var header = new byte[8 + 4 + 16 + 8];
        if (ReadFull(input, header, header.Length) != header.Length || Encoding.ASCII.GetString(header, 0, 8) != Magic)
            throw new InvalidDataException("not a bdxl-encrypted file");
        int iterations = BitConverter.ToInt32(header, 8);
        if (iterations < 1000 || iterations > 100_000_000) throw new InvalidDataException("unreasonable key-derivation settings in the header");
        var salt = header.AsSpan(12, 16).ToArray();
        var prefix = header.AsSpan(28, 8).ToArray();

        using var aes = new AesGcm(keys.GetKey(salt, iterations), TagSize);
        var lengthBytes = new byte[4];
        var tag = new byte[TagSize];
        uint counter = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFull(input, lengthBytes, 4) != 4) throw new InvalidDataException("file is truncated");
            uint length = BitConverter.ToUInt32(lengthBytes, 0);
            if (length > ChunkSize) throw new InvalidDataException("corrupt chunk length");

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
                throw new InvalidDataException("wrong password, or the file is damaged");
            }
            plainHash.AppendData(plain);
            output.Write(plain);

            if (last || firstChunkOnly) break;
            counter++;
        }
        return Convert.ToHexString(plainHash.GetHashAndReset());
    }

    /// <summary>
    /// A value kept in the catalog to recognise a disc's password later: "iterations:salt:check", where check is
    /// derived from a key made with its own salt. Testing a guess against it costs a full PBKDF2 run - no less
    /// than testing it against any .enc file on the disc.
    /// </summary>
    public static string NewKeyCheck(KeyCache keys)
    {
        byte[] salt = NewSalt();
        return $"{DefaultIterations}:{Convert.ToHexString(salt)}:{Convert.ToHexString(CheckBytes(keys.GetKey(salt, DefaultIterations)))}";
    }

    public static bool MatchesKeyCheck(string keyCheck, KeyCache keys)
    {
        string[] parts = keyCheck.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) ||
            iterations < 1000 || iterations > 100_000_000)
            throw new InvalidDataException("unreadable password check value");
        byte[] salt = Convert.FromHexString(parts[1]), expected = Convert.FromHexString(parts[2]);
        return CryptographicOperations.FixedTimeEquals(CheckBytes(keys.GetKey(salt, iterations)), expected);
    }

    static byte[] CheckBytes(byte[] key) => HMACSHA256.HashData(key, "bdxl password check"u8)[..16];

    // ---- BDXLENC2: objects encrypted with a catalog's master key (ENCRYPTION.md) -------------------------------------

    const string Magic2 = "BDXLENC2";
    const int Header2Size = 8 + MasterKey.KeyIdSize + 32 + 8;
    const byte ModeStored = 0;   // 1 = zstd, reserved for compression

    /// <summary>
    /// Encrypts length bytes (-1 = to the end) from the current position of input with an object key derived from the
    /// master key: magic | key id | salt(32) | nonce prefix | chunks. Each chunk's plaintext is mode(1) + up to 1 MiB.
    /// Returns the SHA-256 of the data that was read.
    /// </summary>
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
        output.Write(Encoding.ASCII.GetBytes(Magic2));
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

    /// <summary>Decrypts a whole BDXLENC2 stream (seekable). Returns the plaintext's SHA-256; InvalidDataException on another key, damage or tampering.</summary>
    public static string Decrypt(Stream input, Stream output, MasterKey key, string purpose, CancellationToken ct = default)
    {
        var header = new byte[Header2Size];
        if (ReadFull(input, header, header.Length) != header.Length || Encoding.ASCII.GetString(header, 0, 8) != Magic2)
            throw new InvalidDataException("not a BDXLENC2 file");
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

    /// <summary>Bytes an encrypted copy of a file of this size occupies on the disc (header + 20 bytes per chunk).</summary>
    public static long StoredSize(long plainSize)
    {
        long chunks = Math.Max(1, (plainSize + ChunkSize - 1) / ChunkSize);
        return 36 + plainSize + chunks * (4 + TagSize);
    }

    /// <summary>Largest plaintext whose encrypted copy still fits in storedSize bytes (the inverse of StoredSize).</summary>
    public static long MaxPlainSize(long storedSize)
    {
        long plain = storedSize - 36 - Math.Max(1, (storedSize + ChunkSize - 1) / ChunkSize) * (4 + TagSize);
        return Math.Max(0, plain);
    }
}
