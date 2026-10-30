namespace Bdxl.Tests;

/// <summary>
/// The agent's server runs inside the test process on a pipe of its own; the clock is the test's, so idle and
/// maximum times can be crossed without waiting. Non-parallel: one test turns the agent on through BDXL_AGENT.
/// </summary>
[Collection(nameof(InterruptTests))]
public class AgentTests : IDisposable
{
    readonly string pipe = "bdxl-agent-test-" + Guid.NewGuid().ToString("N");
    readonly List<string> log = new();
    DateTime now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    readonly AgentServer server;
    readonly Task running;
    readonly AgentClient client;
    readonly TempDir tmp = new();

    public AgentTests()
    {
        server = new AgentServer(pipe, line => { lock (log) log.Add(line); }) { ExitWhenEmpty = false, Clock = () => now };
        running = Task.Run(server.Run);
        client = new AgentClient(pipe);
        for (int i = 0; i < 100 && client.Status() == null; i++) Thread.Sleep(20);
    }

    public void Dispose()
    {
        server.Stop();
        running.Wait(TimeSpan.FromSeconds(5));
        server.Dispose();
        tmp.Dispose();
    }

    AgentResponse? Put(MasterKey key, int idleMinutes = 30, int maxMinutes = 480, bool onLock = true, bool onSleep = true) =>
        client.Put(key, @"C:\cat-" + key.CatalogId[..4], TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromMinutes(maxMinutes), onLock, onSleep);

    [Fact]
    public void A_key_handed_over_comes_back_until_it_is_locked()
    {
        using var key = MasterKey.CreateNew();
        using var other = MasterKey.CreateNew();
        Assert.True(Put(key)!.Ok);
        Assert.True(Put(other)!.Ok);

        using (var back = client.TryGetKey(key.CatalogId, key.KeyId))
            Assert.Equal(key.Bytes.ToArray(), back!.Bytes.ToArray());
        Assert.Null(client.TryGetKey(key.CatalogId, "0000000000000000"));   // another key id: not this key
        Assert.Equal(2, client.Status()!.Entries!.Count);

        client.Lock(key.CatalogId);
        Assert.Null(client.TryGetKey(key.CatalogId, key.KeyId));
        Assert.NotNull(client.TryGetKey(other.CatalogId, other.KeyId));
        client.LockAll();
        Assert.Equal(0, server.KeyCount);
        Assert.DoesNotContain(log, l => l.Contains(Convert.ToHexString(key.Bytes)) || l.Contains(Convert.ToBase64String(key.Bytes)));
    }

    [Fact]
    public void Idle_time_restarts_with_every_use_and_the_maximum_ends_it_anyway()
    {
        using var key = MasterKey.CreateNew();
        Put(key, idleMinutes: 30, maxMinutes: 60, onSleep: false);   // (a test clock that jumps looks like sleep)

        now += TimeSpan.FromMinutes(25);
        Assert.NotNull(client.TryGetKey(key.CatalogId, key.KeyId));      // used: 30 more minutes
        now += TimeSpan.FromMinutes(25);
        Assert.NotNull(client.TryGetKey(key.CatalogId, key.KeyId));
        now += TimeSpan.FromMinutes(11);                                 // 61 minutes after unlocking
        Assert.Null(client.TryGetKey(key.CatalogId, key.KeyId));
        Assert.Contains(log, l => l.Contains("maximum time"));

        Put(key, idleMinutes: 30, maxMinutes: 60, onSleep: false);
        now += TimeSpan.FromMinutes(31);
        server.Sweep();
        Assert.Equal(0, server.KeyCount);
        Assert.Contains(log, l => l.Contains("not used for"));
    }

    [Fact]
    public void Windows_lock_and_sleep_lock_the_catalogs_that_asked_for_it()
    {
        using var a = MasterKey.CreateNew();
        using var b = MasterKey.CreateNew();
        Put(a, onLock: true, onSleep: true);
        Put(b, onLock: false, onSleep: false);

        server.LockOnEvent(sessionLock: true, "test Win+L");
        Assert.Null(client.TryGetKey(a.CatalogId, a.KeyId));
        Assert.NotNull(client.TryGetKey(b.CatalogId, b.KeyId));

        Put(a);
        server.Sweep();
        now += TimeSpan.FromMinutes(10);                                  // a sweep 10 minutes late: the computer slept
        server.Sweep();
        Assert.Null(client.TryGetKey(a.CatalogId, a.KeyId));
        Assert.NotNull(client.TryGetKey(b.CatalogId, b.KeyId));
    }

    [Fact]
    public void Another_protocol_version_gets_no_key()
    {
        using var key = MasterKey.CreateNew();
        Put(key);
        var r = server.Handle(new AgentRequest { Protocol = AgentProtocol.Version + 1, Op = "get", CatalogId = key.CatalogId });
        Assert.False(r.Ok);
        Assert.Null(r.Key);
        Assert.True(server.Handle(new AgentRequest { Protocol = AgentProtocol.Version + 1, Op = "status" }).Ok);
    }

    [Fact]
    public void A_second_agent_on_the_same_pipe_does_not_start()
    {
        using var second = new AgentServer(pipe, _ => { });
        var ex = Record.Exception(second.Run);
        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name);
    }

    [Fact]
    public void Commands_take_the_key_from_the_agent_instead_of_the_password()
    {
        string dir = TestKeys.Init(tmp.File("cat"));
        using (var key = TestKeys.Key(dir)) Put(key);

        Environment.SetEnvironmentVariable("BDXL_AGENT", "on");
        Environment.SetEnvironmentVariable("BDXL_AGENT_PIPE", pipe);
        try
        {
            Assert.Equal(0, Program.Main(["status", "--catalog", dir, "--password-env", "BDXL_TEST_NO_PASSWORD"]));
            Assert.Equal(0, Program.Main(["lock", "--catalog", dir]));
            Assert.Equal(1, Program.Main(["status", "--catalog", dir, "--password-env", "BDXL_TEST_NO_PASSWORD"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BDXL_AGENT", "off");
            Environment.SetEnvironmentVariable("BDXL_AGENT_PIPE", null);
        }
    }
}
