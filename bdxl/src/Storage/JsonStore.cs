using System.Text.Encodings.Web;

namespace Bdxl;

/// <summary>Human readable storage: JSON lines for lists, indented JSON for single objects. Writes are atomic.</summary>
public static class JsonStore
{
    static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,          // keep ą, ł, ż... readable
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };

    public static void WriteLines<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, items.Select(i => JsonSerializer.Serialize(i, Compact)));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The same JSON lines WriteLines would write, as UTF-8 bytes (for content that is encrypted before it is stored).</summary>
    public static byte[] LinesToBytes<T>(IEnumerable<T> items)
    {
        using var ms = new MemoryStream();
        WriteLinesTo(ms, items);
        return ms.ToArray();
    }

    /// <summary>JSON lines into a stream (no temp file: for content that is encrypted before it is stored).</summary>
    public static void WriteLinesTo<T>(Stream output, IEnumerable<T> items)
    {
        byte[] newline = Encoding.UTF8.GetBytes(Environment.NewLine);
        foreach (var item in items)
        {
            JsonSerializer.Serialize(output, item, Compact);
            output.Write(newline);
        }
    }

    /// <summary>Indented JSON of one object as UTF-8 bytes.</summary>
    public static byte[] ObjectToBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Pretty);

    /// <summary>One object from UTF-8 JSON; name is only used in error messages.</summary>
    public static T? ParseObject<T>(ReadOnlySpan<byte> utf8, string name)
    {
        try { return JsonSerializer.Deserialize<T>(utf8, Compact); }
        catch (JsonException ex) { throw new JsonException($"{name}: {ex.Message}"); }
    }

    /// <summary>JSON lines from a stream of UTF-8 text; name is only used in error messages.</summary>
    public static List<T> ParseLines<T>(Stream utf8, string name)
    {
        if (utf8 is MemoryStream ms && ms.TryGetBuffer(out var buffer))
            return ParseLines<T>(buffer.AsSpan((int)ms.Position), name);
        using var reader = new StreamReader(utf8, Encoding.UTF8);
        return ParseLines<T>(ReadAllLines(reader), name);
    }

    /// <summary>One JSON line (UTF-8) with the catalog's options.</summary>
    public static T? DeserializeLine<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Compact);

    /// <summary>JSON lines straight from UTF-8 bytes (no string per line).</summary>
    public static List<T> ParseLines<T>(ReadOnlySpan<byte> utf8, string name)
    {
        var result = new List<T>();
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];   // a byte order mark
        int lineNo = 0;
        while (utf8.Length > 0)
        {
            int end = utf8.IndexOf((byte)'\n');
            var line = end < 0 ? utf8 : utf8[..end];
            utf8 = end < 0 ? default : utf8[(end + 1)..];
            lineNo++;
            if (line.Trim((ReadOnlySpan<byte>)[(byte)' ', (byte)'\t', (byte)'\r']).IsEmpty) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, Compact);
                if (item != null) result.Add(item);
            }
            catch (JsonException ex)
            {
                throw new JsonException($"{name} line {lineNo}: {ex.Message}");
            }
        }
        return result;
    }

    static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    public static List<T> ReadLines<T>(string path) =>
        File.Exists(path) ? ParseLines<T>(File.ReadLines(path), Path.GetFileName(path)) : new List<T>();

    /// <summary>Parses JSON lines; name is only used in error messages.</summary>
    public static List<T> ParseLines<T>(IEnumerable<string> lines, string name)
    {
        var result = new List<T>();
        int lineNo = 0;
        foreach (string line in lines)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, Compact);
                if (item != null) result.Add(item);
            }
            catch (JsonException ex)
            {
                throw new JsonException($"{name} line {lineNo}: {ex.Message}");
            }
        }
        return result;
    }

    public static void WriteObject<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Pretty));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Plain write without the temp file - for removable media where renaming may not be possible.</summary>
    public static void WriteObjectDirect<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Pretty));
    }

    public static T? ReadObject<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Compact);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
