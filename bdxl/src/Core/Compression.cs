namespace Bdxl;

/// <summary>A compression setting: mode off | auto | on, and the zstd level.</summary>
public sealed class CompressionSetting
{
    public string Mode { get; set; } = "off";
    public int Level { get; set; } = Compression.DefaultLevel;

    public override string ToString() => Mode == "off" ? "off" : $"{Mode}, level {Level}";
}

/// <summary>
/// Compression of staged copies (ENCRYPTION.md, phase 4): every 1 MiB chunk of a file is zstd-compressed inside the
/// encryption and kept compressed only if that made it smaller. Per alias (with a default for all):
///   off   never;   on   every file;   auto   every file except those whose extension says they are compressed already.
/// 'scan' counts the stored size each file would have (FileRecord.CompressedSize, valid for CompressedWith), so the
/// planner fills discs exactly; parts of split files are never compressed (their layout is fixed in plain bytes).
/// </summary>
public static class Compression
{
    public const int DefaultLevel = 3;
    public const int MinLevel = 1, MaxLevel = 19;
    public static readonly string[] Modes = ["off", "auto", "on"];

    /// <summary>Extensions 'auto' does not even try: their content is compressed already.</summary>
    public static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mts", ".m2ts", ".3gp",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz4", ".cab", ".jar", ".apk", ".msi",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
    };

    /// <summary>Changes with the zstd library, whose output (and so the counted sizes) may differ between versions.</summary>
    static readonly string LibraryVersion = typeof(ZstdSharp.Compressor).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>What a counted size is valid for: the level and the library.</summary>
    public static string Signature(int level) => $"zstd{level}/{LibraryVersion}";

    /// <summary>The setting of an alias: its own, or the default.</summary>
    public static CompressionSetting For(Settings s, string alias) =>
        s.CompressionByAlias.FirstOrDefault(kv => kv.Key.Equals(alias, StringComparison.OrdinalIgnoreCase)).Value ?? s.Compression;

    /// <summary>The zstd level a file is stored with; 0 = not compressed.</summary>
    public static int LevelFor(Settings s, FileRecord f) => LevelFor(s, f.RootName, f.RelPath);

    public static int LevelFor(Settings s, string alias, string relPath)
    {
        var setting = For(s, alias);
        return setting.Mode switch
        {
            "on" => setting.Level,
            "auto" => CompressedExtensions.Contains(Path.GetExtension(relPath)) ? 0 : setting.Level,
            _ => 0
        };
    }

    /// <summary>The stored size the planner counts for a whole file: the counted one if it is valid for today's setting, else the uncompressed size.</summary>
    public static long PlannedStoredSize(Settings s, FileRecord f)
    {
        int level = LevelFor(s, f);
        return level > 0 && f.CompressedSize is long counted && f.CompressedWith == Signature(level)
            ? counted
            : FileCrypto.StoredSize(f.Size);
    }

    /// <summary>True if 'scan' has to (re)count this file's compressed size.</summary>
    public static bool NeedsCounting(Settings s, FileRecord f)
    {
        int level = LevelFor(s, f);
        return level > 0 && f.CompressedWith != Signature(level);
    }

    public static void Validate(string mode, int level)
    {
        if (!Modes.Contains(mode)) throw new UserException($"Compression is one of: {string.Join(", ", Modes)}.");
        if (level < MinLevel || level > MaxLevel) throw new UserException($"The zstd level is {MinLevel}-{MaxLevel} (default {DefaultLevel}).");
    }
}
