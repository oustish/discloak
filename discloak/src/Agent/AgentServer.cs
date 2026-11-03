using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Discloak;

/// <summary>
/// The agent: holds master keys in memory, one per catalog id, and hands them to discloak commands of the same Windows
/// user over a named pipe. A key is locked (wiped) after its idle time without use, after its maximum lifetime, when
/// Windows is locked (Win+L) or goes to sleep (each switchable per catalog), or on request. The agent stops when it
/// holds no key any more. Nothing is written to disk but the log, which never contains key material.
/// </summary>
public sealed class AgentServer : IDisposable
{
    sealed class Entry
    {
        public required KeyRing Key;
        public string? CatalogDir;
        public TimeSpan Idle, Max;
        public DateTime UnlockedUtc, LastUsedUtc;
        public bool LockOnSessionLock, LockOnSleep;
        public DateTime IdleExpiresUtc => LastUsedUtc + Idle;
        public DateTime MaxExpiresUtc => UnlockedUtc + Max;
    }

    static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SleepGap = TimeSpan.FromMinutes(1);       // a sweep this late means the computer slept
    static readonly TimeSpan FirstKeyWithin = TimeSpan.FromMinutes(1); // started but never given a key: stop

    readonly string pipeName;
    readonly Action<string> log;
    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    DateTime? startedUtc, lastSweepUtc;   // by Clock, from the first sweep
    bool hadKeys;

    /// <summary>Stop when the last key is locked (the real agent; tests keep the server running).</summary>
    public bool ExitWhenEmpty { get; init; } = true;
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public AgentServer(string pipeName, Action<string> log)
    {
        this.pipeName = pipeName;
        this.log = log;
    }

    public int KeyCount { get { lock (gate) return entries.Count; } }

    /// <summary>Serves until stopped. Throws IOException/UnauthorizedAccessException if the pipe already exists (another agent).</summary>
    public void Run()
    {
        var listening = Create(first: true);   // fails if someone else already holds the name
        Sweep();   // starts the clocks
        log($"agent started (pid {Environment.ProcessId}, build {AgentProtocol.Build}, pipe {pipeName})");
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        using var sweeper = new Timer(_ => Sweep(), null, SweepEvery, SweepEvery);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { listening.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                var connected = listening;
                listening = Create(first: false);   // the name stays held while this client is served
                _ = Task.Run(() => Serve(connected));
            }
        }
        finally
        {
            listening.Dispose();
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            LockAll("agent stopped");
            log("agent stopped");
        }
    }

    public void Stop() => stop.Cancel();

    NamedPipeServerStream Create(bool first) =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (timeout.Token.Register(pipe.Dispose))   // a client that never finishes its request is dropped
        {
            try
            {
                var request = AgentProtocol.Read<AgentRequest>(pipe);
                if (request == null) return;
                var response = Handle(request);
                try { AgentProtocol.Write(pipe, response); }
                finally { if (response.Key != null) CryptographicOperations.ZeroMemory(response.Key); }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or JsonException)
            {
                log("dropped a request: " + ex.Message);
            }
        }
    }

    /// <summary>One request (also called directly by tests).</summary>
    public AgentResponse Handle(AgentRequest r)
    {
        var response = new AgentResponse { Pid = Environment.ProcessId, Build = AgentProtocol.Build };
        if (r.Protocol != AgentProtocol.Version && r.Op is not ("stop" or "status"))
            return Fail(response, $"protocol {r.Protocol} is not this agent's ({AgentProtocol.Version})");
        Sweep();
        var now = Clock();
        lock (gate)
        {
            switch (r.Op)
            {
                case "get":
                    if (r.CatalogId != null && entries.TryGetValue(r.CatalogId, out var e) && (r.KeyId == null || e.Key.KeyId == r.KeyId))
                    {
                        e.LastUsedUtc = now;
                        response.Key = e.Key.Serialize();   // zeroed once it is sent
                        response.KeyId = e.Key.KeyId;
                    }
                    response.Ok = true;
                    return response;

                case "put":
                    if (r.CatalogId == null || r.KeyId == null || r.Key is not { Length: > 0 })
                        return Fail(response, "put needs a catalog id, key id and key ring");
                    KeyRing ring;
                    try { ring = KeyRing.Deserialize(r.CatalogId, r.Key); }
                    catch (InvalidDataException) { return Fail(response, "damaged key ring"); }
                    finally { CryptographicOperations.ZeroMemory(r.Key); }
                    if (ring.KeyId != r.KeyId) { ring.Dispose(); return Fail(response, "the key ring's current key is not the one named"); }
                    if (entries.Remove(r.CatalogId, out var old)) old.Key.Dispose();
                    entries[r.CatalogId] = new Entry
                    {
                        Key = ring,
                        CatalogDir = r.CatalogDir,
                        Idle = TimeSpan.FromSeconds(Math.Max(60, r.IdleSeconds ?? 1800)),
                        Max = TimeSpan.FromSeconds(Math.Max(60, r.MaxSeconds ?? 8 * 3600)),
                        UnlockedUtc = now, LastUsedUtc = now,
                        LockOnSessionLock = r.LockOnSessionLock ?? true, LockOnSleep = r.LockOnSleep ?? true
                    };
                    hadKeys = true;
                    log($"unlocked {r.CatalogId} ({r.CatalogDir})");
                    response.Ok = true;
                    return response;

                case "lock":
                    if (r.CatalogId != null) LockLocked(r.CatalogId, "lock command");
                    response.Ok = true;
                    break;

                case "lock-all":
                    foreach (string id in entries.Keys.ToList()) LockLocked(id, "lock --all");
                    response.Ok = true;
                    break;

                case "status":
                    response.Ok = true;
                    break;

                case "stop":
                    foreach (string id in entries.Keys.ToList()) LockLocked(id, "agent asked to stop");
                    response.Ok = true;
                    stop.Cancel();
                    break;

                default:
                    return Fail(response, $"unknown request '{r.Op}'");
            }
            response.Entries = entries.Select(x => new AgentEntryInfo
            {
                CatalogId = x.Key, KeyId = x.Value.Key.KeyId, CatalogDir = x.Value.CatalogDir, UnlockedUtc = x.Value.UnlockedUtc,
                IdleExpiresUtc = x.Value.IdleExpiresUtc, MaxExpiresUtc = x.Value.MaxExpiresUtc
            }).OrderBy(x => x.CatalogDir, StringComparer.OrdinalIgnoreCase).ToList();
        }
        StopIfEmpty();
        return response;
    }

    static AgentResponse Fail(AgentResponse response, string error)
    {
        response.Ok = false;
        response.Error = error;
        return response;
    }

    /// <summary>Locks what has expired; a sweep that comes far too late means the computer slept.</summary>
    public void Sweep()
    {
        var now = Clock();
        lock (gate)
        {
            startedUtc ??= now;
            bool slept = lastSweepUtc != null && now - lastSweepUtc > SweepEvery + SleepGap;
            lastSweepUtc = now;
            foreach (var (id, e) in entries.ToList())
            {
                if (now >= e.MaxExpiresUtc) LockLocked(id, "maximum time reached");
                else if (now >= e.IdleExpiresUtc) LockLocked(id, "not used for " + e.Idle);
                else if (slept && e.LockOnSleep) LockLocked(id, "the computer was asleep");
            }
        }
        if (!hadKeys && now - startedUtc > FirstKeyWithin && ExitWhenEmpty) { log("no key was given to the agent"); stop.Cancel(); }
        StopIfEmpty();
    }

    void StopIfEmpty()
    {
        if (!ExitWhenEmpty || !hadKeys) return;
        lock (gate) if (entries.Count > 0) return;
        stop.Cancel();
    }

    public void LockAll(string reason)
    {
        lock (gate) foreach (string id in entries.Keys.ToList()) LockLocked(id, reason);
        StopIfEmpty();
    }

    /// <summary>Locks the keys whose catalogs asked for it on this event (Win+L, sleep).</summary>
    public void LockOnEvent(bool sessionLock, string reason)
    {
        lock (gate)
            foreach (var (id, e) in entries.ToList())
                if (sessionLock ? e.LockOnSessionLock : e.LockOnSleep) LockLocked(id, reason);
        StopIfEmpty();
    }

    void LockLocked(string id, string reason)
    {
        if (!entries.Remove(id, out var e)) return;
        e.Key.Dispose();
        log($"locked {id} ({e.CatalogDir}): {reason}");
    }

    [SupportedOSPlatform("windows")]
    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)
            LockOnEvent(sessionLock: true, "Windows was locked (" + e.Reason + ")");
    }

    [SupportedOSPlatform("windows")]
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) LockOnEvent(sessionLock: false, "the computer is going to sleep");
    }

    public void Dispose()
    {
        stop.Cancel();
        lock (gate)
        {
            foreach (var e in entries.Values) e.Key.Dispose();
            entries.Clear();
        }
    }
}
