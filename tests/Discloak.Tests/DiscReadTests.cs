namespace Discloak.Tests;

/// <summary>UncachedFile ('verify --no-cache'): reads past the Windows file cache give the file's bytes, from anywhere.</summary>
public class DiscReadTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(1_048_579)]
    [InlineData(3 * 1_048_576 + 5)]
    public void Uncached_reads_give_the_files_bytes_from_anywhere(int size)
    {
        var data = tmp.RandomFile("f.bin", size, seed: size);
        string path = tmp.File("f.bin");
        Assert.Equal(data, DiscRead.AllBytes(path, noCache: true));
        Assert.Equal(data, DiscRead.AllBytes(path, noCache: false));

        using var file = new UncachedFile(path, blockSize: 8192);
        var random = new Random(size);
        for (int i = 0; i < 50 && size > 0; i++)   // any place, any length, across blocks
        {
            int at = random.Next(size), n = Math.Min(random.Next(1, 20_000), size - at);
            file.Position = at;
            var got = new byte[n];
            file.ReadExactly(got);
            Assert.Equal(data.AsSpan(at, n).ToArray(), got);
        }
        file.Position = size;
        Assert.Equal(0, file.Read(new byte[10]));
    }

    [Fact]
    public void The_handle_bypasses_the_cache_so_it_refuses_a_read_that_is_not_whole_sectors()
    {
        tmp.RandomFile("f.bin", 100_000, seed: 1);
        using var handle = UncachedFile.OpenHandle(tmp.File("f.bin"));
        var buffer = GC.AllocateArray<byte>(2 * UncachedFile.Alignment, pinned: true);
        Assert.ThrowsAny<Exception>(() => RandomAccess.Read(handle, buffer.AsSpan(0, 100), 1));   // a cached handle reads this
        Assert.Throws<ArgumentOutOfRangeException>(() => new UncachedFile(tmp.File("f.bin"), blockSize: 1000));
    }
}
