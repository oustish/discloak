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
    public static byte[] LinesToBytes<T>(IEnumerable<T> items) =>
        Encoding.UTF8.GetBytes(string.Concat(items.Select(i => JsonSerializer.Serialize(i, Compact) + Environment.NewLine)));

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
