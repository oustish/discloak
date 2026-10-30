namespace Bdxl;

/// <summary>What an object encrypted with the master key is: part of its key derivation, so one kind cannot pass for another.</summary>
public static class KeyPurpose
{
    public const string Catalog = "catalog";     // catalog files, their history and backups
    public const string Manifest = "manifest";   // a disc's manifest
    public const string File = "file";           // staged data
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

    /// <summary>A new random master key for a new catalog.</summary>
    public static MasterKey CreateNew()
    {
        var bytes = RandomNumberGenerator.GetBytes(Size);
        try
        {
            return new MasterKey(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
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
