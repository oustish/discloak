namespace Bdxl.Tests;

/// <summary>BDXLENC2 files (see also MasterKeyTests for the in-memory form and the key file).</summary>
public class FileCryptoTests : IDisposable
{
    const int Chunk = 1 << 20;
    static readonly MasterKey Key = MasterKey.CreateNew();

    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    string Encrypt(byte[] plain, string name, long offset = 0, long length = -1)
    {
        string src = tmp.File(name);
        File.WriteAllBytes(src, plain);
        FileCrypto.Encrypt(src, src + ".enc", Key, KeyPurpose.File, offset, length);
        return src + ".enc";
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
        string src = tmp.File($"p{size}.bin");
        File.WriteAllBytes(src, plain);

        string plainHash = FileCrypto.Encrypt(src, src + ".enc", Key, KeyPurpose.File);
        Assert.Equal(Hashing.HashBytes(plain), plainHash);
        Assert.Equal(FileCrypto.StoredSize(size), new FileInfo(src + ".enc").Length);
        Assert.Equal(Key.KeyId, FileCrypto.KeyIdOf(src + ".enc"));

        string back = tmp.File($"back{size}.bin");
        Assert.Equal(plainHash, FileCrypto.Decrypt(src + ".enc", back, Key, KeyPurpose.File));
        Assert.Equal(plain, File.ReadAllBytes(back));
    }

    [Fact]
    public void A_byte_range_encrypts_just_that_part()
    {
        byte[] plain = new byte[Chunk + 5000];
        new Random(3).NextBytes(plain);
        string enc = Encrypt(plain, "whole.bin", offset: 1000, length: Chunk);

        string back = tmp.File("part.bin");
        FileCrypto.Decrypt(enc, back, Key, KeyPurpose.File);
        Assert.Equal(plain.AsSpan(1000, Chunk).ToArray(), File.ReadAllBytes(back));
    }

    [Fact]
    public void Another_key_is_named_as_such()
    {
        string enc = Encrypt(new byte[100], "a.bin");
        using var other = MasterKey.CreateNew();
        var ex = Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), other, KeyPurpose.File));
        Assert.Contains("another key", ex.Message);
    }

    [Fact]
    public void A_flipped_byte_is_detected()
    {
        string enc = Encrypt(new byte[5000], "a.bin");
        byte[] stored = File.ReadAllBytes(enc);
        stored[100] ^= 1;
        File.WriteAllBytes(enc, stored);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Key, KeyPurpose.File));
    }

    [Fact]
    public void Truncation_inside_a_chunk_is_detected()
    {
        string enc = Encrypt(new byte[5000], "a.bin");
        using (var fs = new FileStream(enc, FileMode.Open)) fs.SetLength(fs.Length - 10);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Key, KeyPurpose.File));
    }

    [Fact]
    public void Cutting_off_whole_chunks_is_detected()
    {
        // the remaining chunk was not encrypted as the last one, so the authenticated "last" flag no longer matches
        string enc = Encrypt(new byte[Chunk + 100], "a.bin");
        using (var fs = new FileStream(enc, FileMode.Open)) fs.SetLength(FileCrypto.HeaderSize + 4 + 1 + Chunk + 16);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Key, KeyPurpose.File));
    }

    [Fact]
    public void Not_an_encrypted_file()
    {
        string path = tmp.File("plain.txt");
        File.WriteAllText(path, "just some text, not BDXLENC2 at all");
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(path, tmp.File("out"), Key, KeyPurpose.File));
        Assert.Null(FileCrypto.KeyIdOf(path));
    }

    [Theory]
    [InlineData(77L)]
    [InlineData(1000L)]
    [InlineData(Chunk - 1L)]
    [InlineData(Chunk + 76L)]
    [InlineData(Chunk + 77L)]
    [InlineData(Chunk + 98L)]
    [InlineData(3L * Chunk)]
    [InlineData(96_882_130_944L)]     // a 100 GB BDXL disc
    [InlineData(128_001_769_472L)]    // a 128 GB BDXL disc
    public void MaxPlainSize_always_fits_and_wastes_almost_nothing(long stored)
    {
        long plain = FileCrypto.MaxPlainSize(stored);
        Assert.True(FileCrypto.StoredSize(plain) <= stored);
        Assert.InRange(stored - FileCrypto.StoredSize(plain), 0, 21);
    }

    [Fact]
    public void MaxPlainSize_of_too_little_space_is_zero() => Assert.Equal(0, FileCrypto.MaxPlainSize(10));

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
