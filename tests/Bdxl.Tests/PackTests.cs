namespace Bdxl.Tests;

/// <summary>A disc's data stream in packs (DiscFormat, PackWriter, PackReader) and the stored form of a file (StoredForm).</summary>
public class PackTests : IDisposable
{
    static readonly MasterKey Key = MasterKey.CreateNew();
    static KeyRing Ring => KeyRing.Of(Key);
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static byte[] Random(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    static byte[] Text(int size)
    {
        var sb = new StringBuilder();
        var rnd = new Random(5);
        string[] words = ["invoice ", "total ", "2024 ", "amount ", "\r\n"];
        while (sb.Length < size) sb.Append(words[rnd.Next(words.Length)]);
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    /// <summary>Writes the pieces one after another into packs in dir; returns where each one starts.</summary>
    List<long> WritePacks(string dir, params byte[][] pieces)
    {
        var at = new List<long>();
        using var writer = new PackWriter(dir, 1, Key);
        foreach (var p in pieces)
        {
            at.Add(writer.Position);
            writer.Write(p);
        }
        writer.Finish();
        return at;
    }

    static byte[] ReadBack(PackReader reader, long at, int length)
    {
        using var s = reader.Open(at, length);
        var result = new byte[length];
        s.ReadExactly(result);
        return result;
    }

    [Fact]
    public void The_real_geometry_is_64_MiB_of_data_per_pack()
    {
        Assert.Equal(67_109_944, DiscFormat.FileSizeFor(DiscFormat.DefaultChunkSize, DiscFormat.DefaultChunksPerPack));
        Assert.Equal("data/000/000001.pack", DiscFormat.PackPath(1));
        Assert.Equal("data/001/001000.pack", DiscFormat.PackPath(1000));
    }

    [Fact]
    public void Every_pack_has_the_same_size_and_any_range_reads_back()
    {
        var a = Random(100_000, 1);
        var b = Random(10, 2);
        var c = Random(3 * TestKeys.ChunkSize + 7, 3);
        var at = WritePacks(tmp.Path, a, b, c);

        var packs = TestKeys.Packs(tmp.Path);
        Assert.Equal(DiscFormat.PacksFor(a.Length + b.Length + c.Length), packs.Count);
        Assert.All(packs, p => Assert.Equal(DiscFormat.PackFileSize, new FileInfo(tmp.File(p)).Length));
        Assert.All(packs, p => Assert.Equal(Key.KeyId, DiscFormat.KeyIdOf(tmp.File(p))));

        using var reader = new PackReader(tmp.Path, Ring);
        Assert.Equal(a, ReadBack(reader, at[0], a.Length));
        Assert.Equal(c, ReadBack(reader, at[2], c.Length));   // across chunk and pack boundaries
        Assert.Equal(b, ReadBack(reader, at[1], b.Length));   // and back
    }

    [Fact]
    public void A_damaged_chunk_fails_only_the_ranges_that_touch_it()
    {
        var a = Random(TestKeys.ChunkSize, 1);         // chunk 0
        var b = Random(TestKeys.ChunkSize, 2);         // chunk 1
        var at = WritePacks(tmp.Path, a, b);
        string pack = tmp.File(DiscFormat.PackPath(1));
        byte[] bytes = File.ReadAllBytes(pack);
        bytes[DiscFormat.HeaderSize + 100] ^= 1;      // in chunk 0
        File.WriteAllBytes(pack, bytes);

        using var reader = new PackReader(tmp.Path, Ring);
        Assert.Contains("chunk 0 is damaged", Assert.Throws<InvalidDataException>(() => ReadBack(reader, at[0], a.Length)).Message);
        Assert.Equal(b, ReadBack(reader, at[1], b.Length));
    }

    [Fact]
    public void A_pack_or_chunk_moved_to_another_place_does_not_decrypt()
    {
        var at = WritePacks(tmp.Path, Random((int)DiscFormat.PackData * 2, 1));
        File.Move(tmp.File(DiscFormat.PackPath(1)), tmp.File("one"));
        File.Move(tmp.File(DiscFormat.PackPath(2)), tmp.File(DiscFormat.PackPath(1)));
        File.Move(tmp.File("one"), tmp.File(DiscFormat.PackPath(2)));

        using var reader = new PackReader(tmp.Path, Ring);
        Assert.Throws<InvalidDataException>(() => ReadBack(reader, at[0], 10));
        using var other = MasterKey.CreateNew();
        using var stranger = new PackReader(tmp.Path, KeyRing.Of(other));
        Assert.Contains("another key", Assert.Throws<InvalidDataException>(() => ReadBack(stranger, at[0], 10)).Message);
    }

    [Fact]
    public void Whole_pack_reading_hashes_every_pack_once_and_names_what_is_wrong()
    {
        var at = WritePacks(tmp.Path, Random((int)DiscFormat.PackData * 2 + 5, 1));
        var expected = TestKeys.Packs(tmp.Path).Select(p => Hashing.HashFile(tmp.File(p))).ToList();
        File.Delete(tmp.File(DiscFormat.PackPath(3)));

        var seen = new List<(int Pack, string? Sha, string? Problem)>();
        using var reader = new PackReader(tmp.Path, Ring, wholePacks: true) { PackRead = (p, sha, problem) => seen.Add((p, sha, problem)) };
        Assert.Equal(10, ReadBack(reader, at[0] + DiscFormat.PackData + 3, 10).Length);   // pack 2: pack 1 is read on the way
        reader.ReadThrough(3);
        Assert.Equal([1, 2, 3], seen.Select(s => s.Pack));
        Assert.Equal(expected[0], seen[0].Sha);
        Assert.Equal(expected[1], seen[1].Sha);
        Assert.Null(seen[2].Sha);
        Assert.Contains("missing", seen[2].Problem);
    }

    public static IEnumerable<object[]> Contents() =>
    [
        ["empty", Array.Empty<byte>()],
        ["text", Text(3 * StoredForm.Block + 777)],
        ["exactly one block of text", Text(StoredForm.Block)],
        ["random", Random(StoredForm.Block + 5000)],
        ["random, then text", Random(StoredForm.Block).Concat(Text(StoredForm.Block + 10)).ToArray()],
    ];

    [Theory]
    [MemberData(nameof(Contents))]
    public void The_stored_form_round_trips_and_scan_counts_its_size_exactly(string what, byte[] plain)
    {
        File.WriteAllBytes(tmp.File("src"), plain);
        foreach (int level in new[] { 0, 3 })
        {
            using var stored = new MemoryStream();
            var (hash, size) = StoredForm.Write(new MemoryStream(plain), plain.Length, level, b => stored.Write(b), long.MaxValue);
            Assert.Equal(Hashing.HashBytes(plain), hash);
            Assert.Equal(stored.Length, size);
            if (level == 0) Assert.Equal(plain.Length, size);
            else
            {
                Assert.Equal((hash, size), StoredForm.HashAndMeasure(tmp.File("src"), level, _ => { }));
                if (what is "text" or "exactly one block of text") Assert.True(size < plain.Length / 2, what);
            }

            stored.Position = 0;
            using var back = new MemoryStream();
            Assert.Equal(hash, StoredForm.Read(stored, level, Compression.CodecOf(level, null), back));
            Assert.Equal(plain, back.ToArray());
        }
    }

    [Fact]
    public void The_stored_form_never_takes_more_than_planned()
    {
        var plain = Random(3 * StoredForm.Block);
        Assert.Throws<StoredForm.TooLargeException>(() =>
            StoredForm.Write(new MemoryStream(plain), plain.Length, 3, _ => { }, limit: plain.Length));   // random data grows a little in zstd
        var (_, size) = StoredForm.Write(new MemoryStream(plain), plain.Length, 0, _ => { }, limit: plain.Length);
        Assert.Equal(plain.Length, size);
    }

    [Fact]
    public void A_pack_that_cannot_be_written_stops_everything_instead_of_leaving_one_file_out()
    {
        Directory.CreateDirectory(tmp.File(DiscFormat.PackPath(1)));   // in the way of pack 1
        using var writer = new PackWriter(tmp.Path, 1, Key);
        var ex = Assert.Throws<UserException>(() => writer.Write(new byte[10]));   // not an IOException: stage would only skip the file
        Assert.Contains("Nothing was recorded", ex.Message);
    }

    [Fact]
    public void Room_leaves_space_for_the_last_pack_s_padding_and_the_manifest()
    {
        Assert.Equal(0, DiscFormat.Room(DiscFormat.PackOnDisc));
        long space = 50 * DiscFormat.PackOnDisc;
        long room = DiscFormat.Room(space);
        Assert.True(room > 46 * DiscFormat.PackData && room < 48 * DiscFormat.PackData);
        // what fills the room, in packs, plus the manifest's padding, fits into the space
        Assert.True(DiscFormat.PacksFor(room) * DiscFormat.PackOnDisc + DiscFormat.ManifestOnDisc(0) <= space);
    }
}
