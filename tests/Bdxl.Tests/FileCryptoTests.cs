namespace Bdxl.Tests;

/// <summary>BDXLENC2 objects - the catalog's files and a disc's manifests (see also MasterKeyTests for the key file).</summary>
public class FileCryptoTests
{
    const int Chunk = 1 << 20;
    static readonly MasterKey Key = MasterKey.CreateNew();

    static byte[] Encrypt(byte[] plain, long length = -1, int offset = 0)
    {
        using var input = new MemoryStream(plain);
        input.Position = offset;
        using var output = new MemoryStream();
        FileCrypto.Encrypt(input, output, Key, KeyPurpose.Manifest, length);
        return output.ToArray();
    }

    static (byte[] Plain, string Hash) Decrypt(byte[] stored, MasterKey? key = null)
    {
        using var output = new MemoryStream();
        string hash = FileCrypto.Decrypt(new MemoryStream(stored), output, key ?? Key, KeyPurpose.Manifest);
        return (output.ToArray(), hash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(2 * Chunk + 12345)]
    public void Round_trip(int size)
    {
        byte[] plain = new byte[size];
        new Random(size).NextBytes(plain);
        byte[] stored = Encrypt(plain);
        Assert.Equal(FileCrypto.StoredSize(size), stored.Length);

        var (back, hash) = Decrypt(stored);
        Assert.Equal(plain, back);
        Assert.Equal(Hashing.HashBytes(plain), hash);
    }

    [Fact]
    public void A_byte_range_encrypts_just_that_part()
    {
        byte[] plain = new byte[Chunk + 5000];
        new Random(3).NextBytes(plain);
        Assert.Equal(plain.AsSpan(1000, Chunk).ToArray(), Decrypt(Encrypt(plain, length: Chunk, offset: 1000)).Plain);
    }

    [Fact]
    public void Another_key_is_named_as_such()
    {
        using var other = MasterKey.CreateNew();
        var ex = Assert.Throws<InvalidDataException>(() => Decrypt(Encrypt(new byte[100]), other));
        Assert.Contains("another key", ex.Message);
    }

    [Fact]
    public void A_flipped_byte_is_detected()
    {
        byte[] stored = Encrypt(new byte[5000]);
        stored[100] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decrypt(stored));
    }

    [Fact]
    public void Truncation_inside_a_chunk_is_detected() =>
        Assert.Throws<InvalidDataException>(() => Decrypt(Encrypt(new byte[5000])[..^10]));

    [Fact]
    public void Cutting_off_whole_chunks_is_detected()
    {
        // the remaining chunk was not encrypted as the last one, so the authenticated "last" flag no longer matches
        byte[] stored = Encrypt(new byte[Chunk + 100]);
        Assert.Throws<InvalidDataException>(() => Decrypt(stored[..(FileCrypto.HeaderSize + 4 + 1 + Chunk + 16)]));
    }

    [Fact]
    public void Not_an_encrypted_file()
    {
        Assert.Throws<InvalidDataException>(() => Decrypt(Encoding.ASCII.GetBytes("just some text, not BDXLENC2 at all")));
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("plain.txt"), "just some text");
        Assert.Null(FileCrypto.KeyIdOf(tmp.File("plain.txt")));
    }

    [Theory]
    [InlineData("Abcdefghijk1", true)]          // lower + upper + digit
    [InlineData("abcdefghij1!", true)]          // lower + digit + special
    [InlineData("Abc1!", false)]                // too short
    [InlineData("abcdefghijklmnop", false)]     // one class
    [InlineData("abcdefghijk12345", false)]     // two classes
    public void Password_policy(string password, bool ok)
    {
        Assert.Equal(ok, PasswordPolicy.Check(password, out string problem));
        Assert.Equal(ok, problem.Length == 0);
    }
}
