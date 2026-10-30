namespace Bdxl;

/// <summary>One wrapped copy of the master key: by the password (Argon2id) or by the recovery key (HKDF).</summary>
public sealed class KeyEnvelope
{
    public const string PasswordKind = "password";
    public const string RecoveryKind = "recovery";

    public string Kind { get; set; } = "";
    public string Kdf { get; set; } = "";          // "argon2id" or "hkdf-sha256"
    public long? MemoryKiB { get; set; }
    public int? Passes { get; set; }
    public int? Parallelism { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Wrapped { get; set; } = [];       // AES-256-GCM ciphertext of the master key + tag
    public DateTime CreatedUtc { get; set; }

    [JsonIgnore] public KdfParams Params => new(MemoryKiB ?? 0, Passes ?? 0);
}

/// <summary>
/// bdxl.key: the catalog id, the key id and the envelopes of the master key (see ENCRYPTION.md). Plain JSON, safe
/// to store openly: without the password or the recovery key it gives nothing away. Lives next to the catalog
/// (never in its history), in every catalog backup and on every disc.
/// </summary>
public sealed class KeyFile
{
    public const string FileName = "bdxl.key";
    public const string CurrentFormat = "bdxl-key-1";
    const int TagSize = 16;

    public string Format { get; set; } = CurrentFormat;
    public string CatalogId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<KeyEnvelope> Envelopes { get; set; } = new();

    [JsonIgnore] public KeyEnvelope? Password => Envelopes.FirstOrDefault(e => e.Kind == KeyEnvelope.PasswordKind);
    [JsonIgnore] public KeyEnvelope? Recovery => Envelopes.FirstOrDefault(e => e.Kind == KeyEnvelope.RecoveryKind);

    public static string PathIn(string dir) => System.IO.Path.Combine(dir, FileName);

    /// <summary>bdxl.key of a folder, or null if there is none. Throws InvalidDataException if it is not one.</summary>
    public static KeyFile? ReadFrom(string dir)
    {
        string path = PathIn(dir);
        if (!File.Exists(path)) return null;
        KeyFile? k;
        try { k = JsonStore.ReadObject<KeyFile>(path); }
        catch (JsonException ex) { throw new InvalidDataException($"{FileName} cannot be read: {ex.Message}"); }
        if (k == null || k.Format != CurrentFormat || k.CatalogId.Length != 32 || k.KeyId.Length != MasterKey.KeyIdSize * 2)
            throw new InvalidDataException($"{path} is not a bdxl key file this version understands");
        return k;
    }

    /// <summary>
    /// Writes bdxl.key into dir atomically: a temp file that must read back identically, then a rename. (Every
    /// envelope was already checked to open when it was made.)
    /// </summary>
    public void WriteTo(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = PathIn(dir), tmp = path + ".tmp";
        JsonStore.WriteObject(tmp, this);
        var back = JsonStore.ReadObject<KeyFile>(tmp);
        if (back == null || JsonSerializer.Serialize(back) != JsonSerializer.Serialize(this))
        {
            File.Delete(tmp);
            throw new IOException($"The new {FileName} did not read back correctly; nothing was changed.");
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A new key file for a new master key, protected by password.</summary>
    public static KeyFile Create(MasterKey key, string password, KdfParams kdf)
    {
        var file = new KeyFile { CatalogId = key.CatalogId, KeyId = key.KeyId, CreatedUtc = DateTime.UtcNow };
        file.SetPassword(key, password, kdf);
        return file;
    }

    /// <summary>The master key, or null if the password is wrong.</summary>
    public MasterKey? TryUnlock(string password)
    {
        var e = Password ?? throw new InvalidDataException($"{FileName} has no password envelope");
        if (e.Kdf != "argon2id" || e.Parallelism is not (null or 1) || e.Salt.Length != Kdf.SaltSize)
            throw new InvalidDataException($"{FileName}: unknown password key derivation '{e.Kdf}'");
        var wrapping = Bdxl.Kdf.Derive(password, e.Salt, e.Params);
        try { return Open(e, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>The master key, or null if the recovery key is not this catalog's.</summary>
    public MasterKey? TryUnlockWithRecoveryKey(ReadOnlySpan<byte> recoveryKey)
    {
        var e = Recovery ?? throw new UserException("This catalog has no recovery key.");
        var wrapping = RecoveryWrappingKey(recoveryKey, e.Salt);
        try { return Open(e, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>Replaces the password envelope (password change); the master key stays the same.</summary>
    public void SetPassword(MasterKey key, string password, KdfParams kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var wrapping = Bdxl.Kdf.Derive(password, salt, kdf);
        var e = new KeyEnvelope
        {
            Kind = KeyEnvelope.PasswordKind, Kdf = "argon2id", MemoryKiB = kdf.MemoryKiB, Passes = kdf.Passes, Parallelism = 1,
            Salt = salt, CreatedUtc = DateTime.UtcNow
        };
        try { Seal(e, key, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
        Replace(e);
    }

    /// <summary>Adds (or replaces) the recovery envelope; returns the recovery key as text, to be shown once.</summary>
    public string SetRecoveryKey(MasterKey key)
    {
        var recovery = RecoveryKey.New();
        try
        {
            var salt = RandomNumberGenerator.GetBytes(32);
            var e = new KeyEnvelope { Kind = KeyEnvelope.RecoveryKind, Kdf = "hkdf-sha256", Salt = salt, CreatedUtc = DateTime.UtcNow };
            var wrapping = RecoveryWrappingKey(recovery, salt);
            try { Seal(e, key, wrapping); }
            finally { CryptographicOperations.ZeroMemory(wrapping); }
            Replace(e);
            return RecoveryKey.Format(recovery);
        }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }

    public bool RemoveRecoveryKey() => Envelopes.RemoveAll(e => e.Kind == KeyEnvelope.RecoveryKind) > 0;

    void Replace(KeyEnvelope e)
    {
        Envelopes.RemoveAll(x => x.Kind == e.Kind);
        Envelopes.Add(e);
        Envelopes.Sort((a, b) => string.CompareOrdinal(a.Kind, b.Kind));   // "password" before "recovery"
    }

    static byte[] RecoveryWrappingKey(ReadOnlySpan<byte> recoveryKey, ReadOnlySpan<byte> salt)
    {
        if (recoveryKey.Length != RecoveryKey.Size) throw new ArgumentException("A recovery key has 32 bytes.");
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, result, salt, "bdxl recovery key"u8);
        return result;
    }

    byte[] Aad(string kind) => Encoding.ASCII.GetBytes($"{CurrentFormat}|{CatalogId}|{KeyId}|{kind}");

    void Seal(KeyEnvelope e, MasterKey key, byte[] wrapping)
    {
        e.Nonce = RandomNumberGenerator.GetBytes(12);
        var wrapped = new byte[MasterKey.Size + TagSize];
        using var aes = new AesGcm(wrapping, TagSize);
        aes.Encrypt(e.Nonce, key.Bytes, wrapped.AsSpan(0, MasterKey.Size), wrapped.AsSpan(MasterKey.Size), Aad(e.Kind));
        e.Wrapped = wrapped;
        var back = Unwrap(e, wrapping);   // never hand out an envelope that does not open
        bool same = back != null && back.AsSpan().SequenceEqual(key.Bytes);
        if (back != null) CryptographicOperations.ZeroMemory(back);
        if (!same) throw new CryptographicException("A new key envelope did not open again.");
    }

    byte[]? Unwrap(KeyEnvelope e, byte[] wrapping)
    {
        if (e.Nonce.Length != 12 || e.Wrapped.Length != MasterKey.Size + TagSize) throw new InvalidDataException($"{FileName}: damaged {e.Kind} envelope");
        var plain = GC.AllocateArray<byte>(MasterKey.Size, pinned: true);
        try
        {
            using var aes = new AesGcm(wrapping, TagSize);
            aes.Decrypt(e.Nonce, e.Wrapped.AsSpan(0, MasterKey.Size), e.Wrapped.AsSpan(MasterKey.Size), plain, Aad(e.Kind));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    MasterKey? Open(KeyEnvelope e, byte[] wrapping)
    {
        var plain = Unwrap(e, wrapping);
        if (plain == null) return null;
        try { return new MasterKey(CatalogId, KeyId, plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
