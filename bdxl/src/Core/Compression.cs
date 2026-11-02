namespace Bdxl;

/// <summary>A compression setting: mode off | auto | on, and the zstd level.</summary>
public sealed class CompressionSetting
{
    public string Mode { get; set; } = "off";
    public int Level { get; set; } = Compression.DefaultLevel;

    public override string ToString() => Mode == "off" ? "off" : $"{Mode}, level {Level}";
}

/// <summary>
/// Compression of staged copies: a file is stored as zstd frames, one per 1 MiB (see StoredForm), inside the disc's
/// encrypted packs. Per alias (with a default for all):
///   off   never;   on   every file;   auto   every file except those whose extension says they are compressed already.
/// 'scan' counts the stored size each file would have (FileRecord.CompressedSize, valid for CompressedWith), so the
/// planner fills discs exactly; a file whose size was not counted for today's setting is stored uncompressed, and
/// parts of split files are never compressed (their layout is fixed in plain bytes).
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

    /// <summary>What a counted size is valid for: the stored form (frames of 1 MiB), the level and the library.</summary>
    public static string Signature(int level) => $"zstd{level}-frames/{LibraryVersion}";

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

    /// <summary>The level a whole file is staged with: today's setting if scan counted its size for it, else 0 (stored as it is).</summary>
    public static int StoredLevel(Settings s, FileRecord f)
    {
        int level = LevelFor(s, f);
        return level > 0 && f.CompressedSize != null && f.CompressedWith == Signature(level) ? level : 0;
    }

    /// <summary>The stored size of a whole file: the counted one if it is valid for today's setting, else its size.</summary>
    public static long PlannedStoredSize(Settings s, FileRecord f) =>
        StoredLevel(s, f) > 0 ? f.CompressedSize!.Value : f.Size;

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
