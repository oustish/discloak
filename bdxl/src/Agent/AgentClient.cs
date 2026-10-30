using System.IO.Pipes;

namespace Bdxl;

/// <summary>Talks to the agent. Every method quietly returns null / false when no agent is running.</summary>
public sealed class AgentClient
{
    readonly string pipeName;

    public AgentClient(string? pipeName = null) { this.pipeName = pipeName ?? AgentProtocol.PipeName; }

    /// <summary>The agent's answer, or null if there is no agent (or it did not answer in time).</summary>
    public AgentResponse? Send(AgentRequest request, int timeoutMs = 3000)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            try { pipe.Connect(0); }                                      // no agent: fails at once
            catch (TimeoutException)
            {
                if (!Exists()) return null;
                pipe.Connect(timeoutMs);                                  // there is one, but it is busy
            }
            AgentProtocol.Write(pipe, request);
            return AgentProtocol.Read<AgentResponse>(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
        finally
        {
            if (request.Key != null) CryptographicOperations.ZeroMemory(request.Key);
        }
    }

    bool Exists()
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The master key of this catalog if the agent holds it (and so keeps it unlocked a while longer).</summary>
    public KeyRing? TryGetKey(string catalogId, string keyId)
    {
        var r = Send(new AgentRequest { Op = "get", CatalogId = catalogId, KeyId = keyId });
        if (r is not { Ok: true, Key: { Length: > 0 } bytes } || r.KeyId != keyId) return null;   // rotated since: not the current ring
        try
        {
            var ring = KeyRing.Deserialize(catalogId, bytes);
            if (ring.KeyId == keyId) return ring;
            ring.Dispose();
            return null;
        }
        catch (InvalidDataException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public AgentResponse? Put(KeyRing key, string catalogDir, TimeSpan idle, TimeSpan max, bool lockOnSessionLock, bool lockOnSleep) =>
        Send(new AgentRequest
        {
            Op = "put", CatalogId = key.CatalogId, KeyId = key.KeyId, Key = key.Serialize(), CatalogDir = catalogDir,
            IdleSeconds = (long)idle.TotalSeconds, MaxSeconds = (long)max.TotalSeconds,
            LockOnSessionLock = lockOnSessionLock, LockOnSleep = lockOnSleep
        });

    public AgentResponse? Lock(string catalogId) => Send(new AgentRequest { Op = "lock", CatalogId = catalogId });
    public AgentResponse? LockAll() => Send(new AgentRequest { Op = "lock-all" });
    public AgentResponse? Status() => Send(new AgentRequest { Op = "status" });
    public AgentResponse? StopAgent() => Send(new AgentRequest { Op = "stop" });
}
