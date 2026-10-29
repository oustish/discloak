namespace Bdxl.Tests;

public class FileCryptoTests : IDisposable
{
    const int Chunk = 1 << 20;
    const string Password = "Correct-Horse-42!";

    // PBKDF2 with 600,000 iterations is slow on purpose: derive the key once for the whole class
    static readonly KeyCache Keys = new(Password);
    static readonly byte[] Salt = FileCrypto.NewSalt();

    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    string Encrypt(byte[] plain, string name, long offset = 0, long length = -1)
    {
        string src = tmp.File(name);
        File.WriteAllBytes(src, plain);
        FileCrypto.Encrypt(src, src + ".enc", Keys, Salt, offset, length);
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

        string plainHash = FileCrypto.Encrypt(src, src + ".enc", Keys, Salt);
        Assert.Equal(Hashing.HashBytes(plain), plainHash);
        Assert.Equal(FileCrypto.StoredSize(size), new FileInfo(src + ".enc").Length);

        string back = tmp.File($"back{size}.bin");
        Assert.Equal(plainHash, FileCrypto.Decrypt(src + ".enc", back, Keys));
        Assert.Equal(plain, File.ReadAllBytes(back));
    }

    [Fact]
    public void In_memory_round_trip_for_metadata()
    {
        byte[] plain = Encoding.UTF8.GetBytes("{\"path\":\"Zdjęcia/łódź.jpg\"}\n");
        byte[] stored = FileCrypto.EncryptBytes(plain, Keys, Salt);
        Assert.Equal(FileCrypto.StoredSize(plain.Length), stored.Length);
        Assert.Equal(plain, FileCrypto.DecryptBytes(stored, Keys));
        Assert.Throws<InvalidDataException>(() => FileCrypto.DecryptBytes(stored[..^1], Keys));
    }

    [Fact]
    public void The_key_check_recognises_only_the_right_password()
    {
        string check = FileCrypto.NewKeyCheck(Keys);
        Assert.True(FileCrypto.MatchesKeyCheck(check, Keys));
        Assert.False(FileCrypto.MatchesKeyCheck(check, new KeyCache("Wrong-Password-1")));
        Assert.NotEqual(check, FileCrypto.NewKeyCheck(Keys));   // own salt every time
        Assert.Throws<InvalidDataException>(() => FileCrypto.MatchesKeyCheck("garbage", Keys));
    }

    [Fact]
    public void OpensWith_tries_the_password_on_the_first_chunk_only()
    {
        string enc = Encrypt(new byte[3 * Chunk], "big.bin");
        Assert.True(FileCrypto.OpensWith(enc, Keys));
        Assert.False(FileCrypto.OpensWith(enc, new KeyCache("Wrong-Password-1")));

        string tiny = Encrypt(new byte[10], "tiny.bin");   // a single chunk is also the last one
        Assert.True(FileCrypto.OpensWith(tiny, Keys));
    }

    [Fact]
    public void A_byte_range_encrypts_just_that_part()
    {
        byte[] plain = new byte[Chunk + 5000];
        new Random(3).NextBytes(plain);
        string enc = Encrypt(plain, "whole.bin", offset: 1000, length: Chunk);

        string back = tmp.File("part.bin");
        FileCrypto.Decrypt(enc, back, Keys);
        Assert.Equal(plain.AsSpan(1000, Chunk).ToArray(), File.ReadAllBytes(back));
    }

    [Fact]
    public void Wrong_password_is_detected()
    {
        string enc = Encrypt(new byte[100], "a.bin");
        var ex = Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), new KeyCache("Wrong-Password-1")));
        Assert.Contains("wrong password", ex.Message);
    }

    [Fact]
    public void A_flipped_byte_is_detected()
    {
        string enc = Encrypt(new byte[5000], "a.bin");
        byte[] stored = File.ReadAllBytes(enc);
        stored[100] ^= 1;
        File.WriteAllBytes(enc, stored);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Keys));
    }

    [Fact]
    public void Truncation_inside_a_chunk_is_detected()
    {
        string enc = Encrypt(new byte[5000], "a.bin");
        using (var fs = new FileStream(enc, FileMode.Open)) fs.SetLength(fs.Length - 10);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Keys));
    }

    [Fact]
    public void Cutting_off_whole_chunks_is_detected()
    {
        // the remaining chunk was not encrypted as the last one, so the authenticated "last" flag no longer matches
        string enc = Encrypt(new byte[Chunk + 100], "a.bin");
        using (var fs = new FileStream(enc, FileMode.Open)) fs.SetLength(36 + 4 + Chunk + 16);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(enc, tmp.File("out"), Keys));
    }

    [Fact]
    public void Not_an_encrypted_file()
    {
        string path = tmp.File("plain.txt");
        File.WriteAllText(path, "just some text, not BDXLENC1 at all");
        Assert.Throws<InvalidDataException>(() => FileCrypto.Decrypt(path, tmp.File("out"), Keys));
    }

    [Theory]
    [InlineData(57L)]
    [InlineData(1000L)]
    [InlineData(Chunk - 1L)]
    [InlineData(Chunk + 56L)]
    [InlineData(Chunk + 57L)]
    [InlineData(3L * Chunk)]
    [InlineData(96_882_130_944L)]     // a 100 GB BDXL disc
    [InlineData(128_001_769_472L)]    // a 128 GB BDXL disc
    public void MaxPlainSize_always_fits_and_wastes_almost_nothing(long stored)
    {
        // chunks are counted from the stored size, which slightly overestimates the overhead: a few unused bytes
        // (40 on a 128 GB disc) are the price for a formula without a search
        long plain = FileCrypto.MaxPlainSize(stored);
        Assert.True(FileCrypto.StoredSize(plain) <= stored);
        Assert.InRange(stored - FileCrypto.StoredSize(plain), 0, 100);
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
