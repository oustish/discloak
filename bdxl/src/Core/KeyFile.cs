namespace Bdxl;

/// <summary>
/// One wrapped copy of the catalog's keys: by the password (Argon2id) or by the recovery key (HKDF). A ring envelope
/// (bdxl-key-2) holds every master key so far (KeyRing.Serialize); an envelope of bdxl-key-1 holds a single key.
/// </summary>
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
    public byte[] Wrapped { get; set; } = [];       // AES-256-GCM ciphertext of the key(s) + tag
    public DateTime CreatedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ring { get; set; }                  // holds a key ring (else one key: bdxl-key-1)
    public string? KeyId { get; set; }              // ring: the current key's id when it was sealed (else the file's KeyId)

    [JsonIgnore] public KdfParams Params => new(MemoryKiB ?? 0, Passes ?? 0);
}

/// <summary>
/// bdxl.key: the catalog id, the current key id and the envelopes of the catalog's keys (see ENCRYPTION.md). Plain
/// JSON, safe to store openly: without the password or the recovery key it gives nothing away. Lives next to the
/// catalog (never in its history), in every catalog backup and on every disc. Written as bdxl-key-2 (key rings);
/// bdxl-key-1 files (one key) are read as rings of one.
/// </summary>
public sealed class KeyFile
{
    public const string FileName = "bdxl.key";
    public const string Format1 = "bdxl-key-1";
    public const string CurrentFormat = "bdxl-key-2";
    const int TagSize = 16;

    public string Format { get; set; } = CurrentFormat;
    public string CatalogId { get; set; } = "";
    public string KeyId { get; set; } = "";          // the current key's id
    public DateTime CreatedUtc { get; set; }
    public List<KeyEnvelope> Envelopes { get; set; } = new();

    [JsonIgnore] public KeyEnvelope? Password => Envelopes.FirstOrDefault(e => e.Kind == KeyEnvelope.PasswordKind);
    [JsonIgnore] public KeyEnvelope? Recovery => Envelopes.FirstOrDefault(e => e.Kind == KeyEnvelope.RecoveryKind);

    public static string PathIn(string dir) => System.IO.Path.Combine(dir, FileName);

    /// <summary>bdxl.key of a folder, or null if there is none. Throws InvalidDataException if it is not one.</summary>
    public static KeyFile? ReadFrom(string dir)
    {
        string path = PathIn(dir);
        return File.Exists(path) ? Parse(File.ReadAllBytes(path), path) : null;
    }

    /// <summary>A bdxl.key from its bytes (read by the caller from path). Throws InvalidDataException if it is not one.</summary>
    public static KeyFile Parse(byte[] utf8, string path)
    {
        KeyFile? k;
        try { k = JsonStore.ParseObject<KeyFile>(utf8, FileName); }
        catch (JsonException ex) { throw new InvalidDataException($"{FileName} cannot be read: {ex.Message}"); }
        if (k == null || k.Format is not (Format1 or CurrentFormat) || k.CatalogId.Length != 32 || k.KeyId.Length != MasterKey.KeyIdSize * 2)
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

    /// <summary>A new key file for a new catalog's key(s), protected by password.</summary>
    public static KeyFile Create(KeyRing keys, string password, KdfParams kdf)
    {
        var file = new KeyFile { CatalogId = keys.CatalogId, KeyId = keys.KeyId, CreatedUtc = DateTime.UtcNow };
        file.SetPassword(keys, password, kdf);
        return file;
    }

    /// <summary>The catalog's keys, or null if the password is wrong.</summary>
    public KeyRing? TryUnlock(string password)
    {
        var e = Password ?? throw new InvalidDataException($"{FileName} has no password envelope");
        if (e.Kdf != "argon2id" || e.Parallelism is not (null or 1) || e.Salt.Length != Kdf.SaltSize)
            throw new InvalidDataException($"{FileName}: unknown password key derivation '{e.Kdf}'");
        var wrapping = Bdxl.Kdf.Derive(password, e.Salt, e.Params);
        try { return Open(e, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>The catalog's keys, or null if the recovery key is not this catalog's.</summary>
    public KeyRing? TryUnlockWithRecoveryKey(ReadOnlySpan<byte> recoveryKey)
    {
        var e = Recovery ?? throw new UserException("This catalog has no recovery key.");
        var wrapping = RecoveryWrappingKey(recoveryKey, e.Salt);
        try { return Open(e, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }

    /// <summary>Replaces the password envelope with one of the whole ring (password change, key rotation).</summary>
    public void SetPassword(KeyRing keys, string password, KdfParams kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var wrapping = Bdxl.Kdf.Derive(password, salt, kdf);
        var e = new KeyEnvelope
        {
            Kind = KeyEnvelope.PasswordKind, Kdf = "argon2id", MemoryKiB = kdf.MemoryKiB, Passes = kdf.Passes, Parallelism = 1,
            Salt = salt, CreatedUtc = DateTime.UtcNow
        };
        try { Seal(e, keys, wrapping); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
        Replace(e, keys);
    }

    /// <summary>Adds (or replaces) the recovery envelope; returns the recovery key as text, to be shown once.</summary>
    public string SetRecoveryKey(KeyRing keys)
    {
        var recovery = RecoveryKey.New();
        try
        {
            var salt = RandomNumberGenerator.GetBytes(32);
            var e = new KeyEnvelope { Kind = KeyEnvelope.RecoveryKind, Kdf = "hkdf-sha256", Salt = salt, CreatedUtc = DateTime.UtcNow };
            var wrapping = RecoveryWrappingKey(recovery, salt);
            try { Seal(e, keys, wrapping); }
            finally { CryptographicOperations.ZeroMemory(wrapping); }
            Replace(e, keys);
            return RecoveryKey.Format(recovery);
        }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }

    public bool RemoveRecoveryKey() => Envelopes.RemoveAll(e => e.Kind == KeyEnvelope.RecoveryKind) > 0;

    void Replace(KeyEnvelope e, KeyRing keys)
    {
        Envelopes.RemoveAll(x => x.Kind == e.Kind);
        Envelopes.Add(e);
        Envelopes.Sort((a, b) => string.CompareOrdinal(a.Kind, b.Kind));   // "password" before "recovery"
        KeyId = keys.KeyId;
        Format = CurrentFormat;
    }

    static byte[] RecoveryWrappingKey(ReadOnlySpan<byte> recoveryKey, ReadOnlySpan<byte> salt)
    {
        if (recoveryKey.Length != RecoveryKey.Size) throw new ArgumentException("A recovery key has 32 bytes.");
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, result, salt, "bdxl recovery key"u8);
        return result;
    }

    /// <summary>bdxl-key-1|catalog|key id|kind for a single key; bdxl-key-2|catalog|current key id|kind for a ring.</summary>
    byte[] Aad(KeyEnvelope e) =>
        Encoding.ASCII.GetBytes($"{(e.Ring ? CurrentFormat : Format1)}|{CatalogId}|{e.KeyId ?? KeyId}|{e.Kind}");

    void Seal(KeyEnvelope e, KeyRing keys, byte[] wrapping)
    {
        e.Ring = true;
        e.KeyId = keys.KeyId;
        e.Nonce = RandomNumberGenerator.GetBytes(12);
        var plain = keys.Serialize();
        try
        {
            var wrapped = new byte[plain.Length + TagSize];
            using (var aes = new AesGcm(wrapping, TagSize))
                aes.Encrypt(e.Nonce, plain, wrapped.AsSpan(0, plain.Length), wrapped.AsSpan(plain.Length), Aad(e));
            e.Wrapped = wrapped;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }

        using var back = Open(e, wrapping);   // never hand out an envelope that does not open
        if (back == null || back.All.Count != keys.All.Count || back.KeyId != keys.KeyId ||
            !back.All.Zip(keys.All).All(p => p.First.Bytes.SequenceEqual(p.Second.Bytes)))
            throw new CryptographicException("A new key envelope did not open again.");
    }

    KeyRing? Open(KeyEnvelope e, byte[] wrapping)
    {
        if (e.Nonce.Length != 12 || e.Wrapped.Length <= TagSize) throw new InvalidDataException($"{FileName}: damaged {e.Kind} envelope");
        int n = e.Wrapped.Length - TagSize;
        if (!e.Ring && n != MasterKey.Size) throw new InvalidDataException($"{FileName}: damaged {e.Kind} envelope");
        var plain = GC.AllocateArray<byte>(n, pinned: true);
        try
        {
            try
            {
                using var aes = new AesGcm(wrapping, TagSize);
                aes.Decrypt(e.Nonce, e.Wrapped.AsSpan(0, n), e.Wrapped.AsSpan(n), plain, Aad(e));
            }
            catch (CryptographicException) { return null; }

            if (!e.Ring) return KeyRing.Of(new MasterKey(CatalogId, KeyId, plain));
            var ring = KeyRing.Deserialize(CatalogId, plain);
            if (ring.KeyId != (e.KeyId ?? KeyId)) { ring.Dispose(); throw new InvalidDataException($"{FileName}: the {e.Kind} envelope's current key is not the one it names"); }
            return ring;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
