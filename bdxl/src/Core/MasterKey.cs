namespace Bdxl;

/// <summary>What an object encrypted with the master key is: part of its key derivation, so one kind cannot pass for another.</summary>
public static class KeyPurpose
{
    public const string Catalog = "catalog";     // catalog files, their history and backups
    public const string Manifest = "manifest";   // a disc's manifest
    public const string Pack = "pack";           // a pack of a disc's data stream
}

/// <summary>
/// The random 256-bit key that encrypts a whole catalog and its discs (see ENCRYPTION.md). Never stored in plaintext:
/// bdxl.key holds it wrapped by the password (and optionally by a recovery key). Every encrypted object gets its own
/// key from it via HKDF with the object's salt. Kept in pinned memory and wiped on Dispose.
/// </summary>
public sealed class MasterKey : IDisposable
{
    public const int Size = 32;
    public const int KeyIdSize = 8;

    readonly byte[] key = GC.AllocateArray<byte>(Size, pinned: true);

    public string CatalogId { get; }
    public string KeyId { get; }
    public byte[] KeyIdBytes { get; }

    public MasterKey(string catalogId, string keyId, ReadOnlySpan<byte> keyBytes)
    {
        if (keyBytes.Length != Size) throw new ArgumentException("A master key has 32 bytes.");
        keyBytes.CopyTo(key);
        CatalogId = catalogId;
        KeyId = keyId;
        KeyIdBytes = Convert.FromHexString(keyId);
        if (KeyIdBytes.Length != KeyIdSize) throw new ArgumentException("A key id has 8 bytes.");
    }

    /// <summary>A new random master key: for a new catalog, or (with its catalog id) the next key of a rotation.</summary>
    public static MasterKey CreateNew(string? catalogId = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(Size);
        try
        {
            return new MasterKey(catalogId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                                 Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyIdSize)), bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>The raw key, only for wrapping it (bdxl.key) or handing it to the agent.</summary>
    public ReadOnlySpan<byte> Bytes => key;

    /// <summary>Key of one encrypted object: HKDF-SHA256(master key, salt = the object's salt, info = "bdxl-enc2 " + purpose).</summary>
    public byte[] ObjectKey(ReadOnlySpan<byte> salt, string purpose)
    {
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, result, salt, Encoding.ASCII.GetBytes("bdxl-enc2 " + purpose));
        return result;
    }

    /// <summary>HMAC key of catalog.hash.json.</summary>
    public byte[] IntegrityKey()
    {
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, result, ReadOnlySpan<byte>.Empty, "bdxl catalog integrity"u8);
        return result;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

/// <summary>
/// Every master key a catalog has had, oldest first; the last one is current and encrypts everything new. A key
/// rotation ('password change --rotate-key') adds one; keys are never removed, so every disc stays readable. What
/// was encrypted is found by the key id in its header (FileCrypto) or record (DiscRecord.KeyId).
/// </summary>
public sealed class KeyRing : IDisposable
{
    const int EntrySize = MasterKey.KeyIdSize + MasterKey.Size;
    readonly List<MasterKey> keys;

    public KeyRing(IEnumerable<MasterKey> keys)
    {
        this.keys = keys.ToList();
        if (this.keys.Count == 0) throw new ArgumentException("A key ring holds at least one key.");
        if (this.keys.Select(k => k.CatalogId).Distinct().Count() != 1) throw new ArgumentException("All keys of a ring belong to one catalog.");
    }

    /// <summary>A ring of a single key (the key is owned by the ring from now on).</summary>
    public static KeyRing Of(MasterKey key) => new([key]);

    public MasterKey Current => keys[^1];
    public IReadOnlyList<MasterKey> All => keys;
    public string CatalogId => Current.CatalogId;
    public string KeyId => Current.KeyId;

    public MasterKey? Find(string? keyId) =>
        keyId == null ? null : keys.FirstOrDefault(k => k.KeyId.Equals(keyId, StringComparison.OrdinalIgnoreCase));

    public MasterKey? Find(ReadOnlySpan<byte> keyId)
    {
        foreach (var k in keys) if (keyId.SequenceEqual(k.KeyIdBytes)) return k;
        return null;
    }

    public bool Contains(string? keyId) => Find(keyId) != null;

    /// <summary>The key with this id (null = the current one); a key this catalog does not have is an error.</summary>
    public MasterKey For(string? keyId) => keyId == null ? Current
        : Find(keyId) ?? throw new UserException($"This catalog has no key with the id {keyId} (its keys: {string.Join(", ", keys.Select(k => k.KeyId))}).");

    /// <summary>Adds the next key; it becomes the current one.</summary>
    public void Add(MasterKey key)
    {
        if (key.CatalogId != CatalogId) throw new ArgumentException("The new key belongs to another catalog.");
        keys.Add(key);
    }

    /// <summary>key id (8) + key (32) for each key, oldest first: what an envelope of bdxl-key-2 encrypts.</summary>
    public byte[] Serialize()
    {
        var bytes = GC.AllocateArray<byte>(EntrySize * keys.Count, pinned: true);
        for (int i = 0; i < keys.Count; i++)
        {
            keys[i].KeyIdBytes.CopyTo(bytes, i * EntrySize);
            keys[i].Bytes.CopyTo(bytes.AsSpan(i * EntrySize + MasterKey.KeyIdSize));
        }
        return bytes;
    }

    public static KeyRing Deserialize(string catalogId, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % EntrySize != 0) throw new InvalidDataException("damaged key ring");
        var list = new List<MasterKey>();
        for (int i = 0; i < bytes.Length; i += EntrySize)
            list.Add(new MasterKey(catalogId, Convert.ToHexString(bytes.Slice(i, MasterKey.KeyIdSize)),
                                   bytes.Slice(i + MasterKey.KeyIdSize, MasterKey.Size)));
        return new KeyRing(list);
    }

    public void Dispose()
    {
        foreach (var k in keys) k.Dispose();
    }
}
