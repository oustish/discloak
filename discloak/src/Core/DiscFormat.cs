namespace Discloak;

/// <summary>
/// The layout of a discloak disc (format "discloak-pack-1"; "bdxl-pack-1" before 0.8.0). Everything a disc stores - whole files, compressed or not, and
/// parts of files - is laid end to end in ONE stream of data, cut into packs of equal size (64 MiB of data each), and
/// each pack is encrypted on its own as data/NNN/NNNNNN.pack. From outside only the number of packs shows, never the
/// sizes, number or names of the files. Where a file lies in the stream (its position "at" and stored size) is in the
/// catalog and in the disc's encrypted manifest. The last pack of a session is filled up with zeros.
///   pack file: "DCLKPAK1" (8; "BDXLPAK1" before 0.8.0) | key id (8) | salt (32) | nonce prefix (8) |
///              ChunksPerPack x [ciphertext (ChunkSize) | tag (16)]
///   key of the pack = HKDF(master key, salt, "bdxl-enc2 pack"); chunk c (from 0): AES-256-GCM, nonce = prefix + c (uint32 LE),
///   associated data = pack number (uint32 LE) + c (uint32 LE), so a pack or a chunk cannot be moved to another place.
/// Pack n (from 1) holds the stream's bytes from (n - 1) x PackData; chunk c of it starts at HeaderSize + c x (ChunkSize + 16).
/// </summary>
public static class DiscFormat
{
    public const string Name = "discloak-pack-1";
    public const string Magic = "DCLKPAK1";

    // The names before the program was renamed from bdxl to Discloak (0.8.0). Discs staged before then carry them -
    // "bdxl-pack-1" in disc.json, "BDXLPAK1" at the start of every pack - and a burned disc can never change, so they
    // are read as exactly the same format, forever. Never written again. (A disc may hold both: packs of an older
    // session with the old magic, packs of a later one with the new. Each pack is read by its own.)
    public const string LegacyName = "bdxl-pack-1";
    public const string LegacyMagic = "BDXLPAK1";

    /// <summary>disc.json's "format" names this layout (by its name today, or by the one before 0.8.0).</summary>
    public static bool IsName(string? format) => format is Name or LegacyName;

    /// <summary>The first 8 bytes of a pack: this layout's magic (by today's name, or by the one before 0.8.0).</summary>
    public static bool IsMagic(ReadOnlySpan<byte> first8) => Encoding.ASCII.GetString(first8) is Magic or LegacyMagic;
    public const string PackSuffix = ".pack";
    public const int HeaderSize = 8 + MasterKey.KeyIdSize + 32 + 8;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 1 << 20, DefaultChunksPerPack = 64;

    /// <summary>Plaintext bytes per chunk (1 MiB).</summary>
    public static int ChunkSize { get; private set; } = DefaultChunkSize;
    /// <summary>Chunks per pack (64: 64 MiB of data).</summary>
    public static int ChunksPerPack { get; private set; } = DefaultChunksPerPack;

    /// <summary>Only for tests: smaller packs keep test discs small. The program always uses the defaults.</summary>
    public static void UseGeometryForTests(int chunkSize, int chunksPerPack) => (ChunkSize, ChunksPerPack) = (chunkSize, chunksPerPack);

    /// <summary>Bytes of the data stream per pack.</summary>
    public static long PackData => (long)ChunkSize * ChunksPerPack;

    /// <summary>Size of every pack file.</summary>
    public static long PackFileSize => FileSizeFor(ChunkSize, ChunksPerPack);

    public static long FileSizeFor(int chunkSize, int chunksPerPack) => HeaderSize + (long)chunksPerPack * (chunkSize + TagSize);

    /// <summary>A pack's line in packs.sha256 ("hash *data/NNN/NNNNNN.pack"), generously.</summary>
    const int PackListLine = 64 + 2 + 32 + 2;

    /// <summary>Space one pack takes on the disc: its sectors, its file system entries and its line in packs.sha256.</summary>
    public static long PackOnDisc => DiscSpace.OnDisc(PackFileSize) + PackListLine;

    /// <summary>Stored path of pack n (from 1): 1000 per folder.</summary>
    public static string PackPath(int n) => $"data/{n / 1000:D3}/{n:D6}{PackSuffix}";

    /// <summary>The pack (from 1) holding byte `at` of the stream.</summary>
    public static int PackOf(long at) => (int)(at / PackData) + 1;

    /// <summary>Packs needed for a stream of this many bytes.</summary>
    public static int PacksFor(long bytes) => (int)((bytes + PackData - 1) / PackData);

    /// <summary>Space the manifest of a session takes: its lines padded to whole packs of data, encrypted.</summary>
    public static long ManifestOnDisc(long lineBytes) => DiscSpace.OnDisc(FileCrypto.StoredSize(PaddedManifestSize(lineBytes)));

    /// <summary>The manifest's plaintext is padded to whole packs of data (at least one), so its size says nothing about the number of files.</summary>
    public static long PaddedManifestSize(long lineBytes) => Math.Max(1, (lineBytes + PackData - 1) / PackData) * PackData;

    /// <summary>
    /// Bytes of stored data (files and their manifest lines) that fit into `space` bytes of a disc: the space less the
    /// padding of the last pack and of the manifest, scaled by what a pack takes on the disc beyond its data.
    /// </summary>
    public static long Room(long space)
    {
        // the last pack's padding, the manifest's padding (its lines are counted with the files), packs.sha256 itself,
        // and the data folders
        long fixedCost = PackOnDisc + DiscSpace.OnDisc(FileCrypto.StoredSize(PackData)) + DiscSpace.OnDisc(0) + 4 * DiscSpace.Sector;
        if (space <= fixedCost) return 0;
        return (long)((Int128)(space - fixedCost) * PackData / PackOnDisc);
    }

    /// <summary>The key id in a pack's header, or null if it is not a pack.</summary>
    public static string? KeyIdOf(string path)
    {
        var header = new byte[16];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.ReadAtLeast(header, 16, throwOnEndOfStream: false) != 16 || !IsMagic(header.AsSpan(0, 8))) return null;
        return Convert.ToHexString(header, 8, MasterKey.KeyIdSize);
    }

    internal static byte[] Nonce(ReadOnlySpan<byte> prefix, int chunk)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce);
        BitConverter.TryWriteBytes(nonce.AsSpan(8), (uint)chunk);
        return nonce;
    }

    internal static byte[] Aad(int pack, int chunk)
    {
        var aad = new byte[8];
        BitConverter.TryWriteBytes(aad.AsSpan(0, 4), (uint)pack);
        BitConverter.TryWriteBytes(aad.AsSpan(4), (uint)chunk);
        return aad;
    }
}
