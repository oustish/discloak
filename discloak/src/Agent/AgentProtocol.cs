namespace Discloak;

/// <summary>What a client asks the agent. Op: get, put, lock, lock-all, status, stop.</summary>
public sealed class AgentRequest
{
    public int Protocol { get; set; } = AgentProtocol.Version;
    public string Op { get; set; } = "";
    public string? CatalogId { get; set; }
    public string? KeyId { get; set; }
    public byte[]? Key { get; set; }   // the key ring (KeyRing.Serialize)
    public string? CatalogDir { get; set; }          // only shown by 'unlock --status'
    public long? IdleSeconds { get; set; }
    public long? MaxSeconds { get; set; }
    public bool? LockOnSessionLock { get; set; }
    public bool? LockOnSleep { get; set; }
}

public sealed class AgentResponse
{
    public int Protocol { get; set; } = AgentProtocol.Version;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public byte[]? Key { get; set; }   // the key ring (KeyRing.Serialize)
    public string? KeyId { get; set; }
    public int Pid { get; set; }
    public string? Build { get; set; }
    public List<AgentEntryInfo>? Entries { get; set; }
}

public sealed class AgentEntryInfo
{
    public string CatalogId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string? CatalogDir { get; set; }
    public DateTime UnlockedUtc { get; set; }
    public DateTime IdleExpiresUtc { get; set; }
    public DateTime MaxExpiresUtc { get; set; }
}

/// <summary>
/// Messages between discloak and its agent over a named pipe: a 4-byte little-endian length, then UTF-8 JSON. The pipe is
/// created with PipeOptions.CurrentUserOnly (only this user can open it; the client checks the server's owner).
/// </summary>
public static class AgentProtocol
{
    public const int Version = 2;   // 2: Key holds the catalog's key ring (KeyRing.Serialize), KeyId its current key
    const int MaxMessage = 64 * 1024;

    /// <summary>Pipe name: DISCLOAK_AGENT_PIPE (tests), otherwise one per Windows user.</summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable("DISCLOAK_AGENT_PIPE") is { Length: > 0 } name
            ? name
            : "discloak-agent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                  (Environment.UserDomainName + "\\" + Environment.UserName).ToLowerInvariant())))[..16];

    /// <summary>Changes with every build: the agent's program folder is named after it.</summary>
    public static string Build => typeof(AgentProtocol).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    /// <summary>DISCLOAK_AGENT=off: never ask or start an agent (tests, scripts).</summary>
    public static bool Enabled => !string.Equals(Environment.GetEnvironmentVariable("DISCLOAK_AGENT"), "off", StringComparison.OrdinalIgnoreCase);

    public static void Write<T>(Stream s, T message)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            s.Write(BitConverter.GetBytes(json.Length));
            s.Write(json);
            s.Flush();
        }
        finally { CryptographicOperations.ZeroMemory(json); }   // may hold a key
    }

    public static T? Read<T>(Stream s)
    {
        var lengthBytes = new byte[4];
        if (!ReadFull(s, lengthBytes)) return default;
        int length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaxMessage) throw new InvalidDataException("bad agent message length");
        var json = new byte[length];
        try
        {
            if (!ReadFull(s, json)) return default;
            return JsonSerializer.Deserialize<T>(json);
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    static bool ReadFull(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}
