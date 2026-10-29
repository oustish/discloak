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

    public static List<T> ReadLines<T>(string path)
    {
        var result = new List<T>();
        if (!File.Exists(path)) return result;
        int lineNo = 0;
        foreach (string line in File.ReadLines(path))
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
                throw new JsonException($"{Path.GetFileName(path)} line {lineNo}: {ex.Message}");
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
