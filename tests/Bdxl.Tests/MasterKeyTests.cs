using System.Security.Cryptography;
using System.Text.Json;

namespace Bdxl.Tests;

public class MasterKeyTests
{
    static readonly KdfParams Fast = new(Kdf.MinKiB, 1);
    const string Pw = "Correct-Horse-42!";

    static byte[] Data(int size, int seed = 1)
    {
        var d = new byte[size];
        new Random(seed).NextBytes(d);
        return d;
    }

    static KeyRing NewRing() => KeyRing.Of(MasterKey.CreateNew());
    static byte[] Raw(KeyRing r) => r.Current.Bytes.ToArray();

    [Fact]
    public void The_password_opens_the_master_key_and_a_wrong_one_does_not()
    {
        using var key = NewRing();
        var file = KeyFile.Create(key, Pw, Fast);

        using var back = file.TryUnlock(Pw);
        Assert.NotNull(back);
        Assert.Equal(Raw(key), Raw(back));
        Assert.Equal(key.KeyId, back.KeyId);
        Assert.Null(file.TryUnlock("Wrong-Horse-42!"));
    }

    [Fact]
    public void A_password_change_keeps_the_master_key_and_drops_the_old_password()
    {
        using var key = NewRing();
        var file = KeyFile.Create(key, Pw, Fast);
        var oldFile = JsonSerializer.Deserialize<KeyFile>(JsonSerializer.Serialize(file))!;   // e.g. on a burned disc

        file.SetPassword(key, "New-Password-77?", Fast);
        Assert.Null(file.TryUnlock(Pw));
        using var back = file.TryUnlock("New-Password-77?");
        Assert.Equal(Raw(key), Raw(back!));

        using var fromOld = oldFile.TryUnlock(Pw);   // the old envelope still opens the same master key
        Assert.Equal(Raw(key), Raw(fromOld!));
    }

    [Fact]
    public void A_rotation_adds_a_key_and_an_old_envelope_opens_only_the_old_ones()
    {
        using var ring = NewRing();
        var file = KeyFile.Create(ring, Pw, Fast);
        var beforeRotation = JsonSerializer.Deserialize<KeyFile>(JsonSerializer.Serialize(file))!;   // on a disc staged before
        string oldId = ring.KeyId;

        ring.Add(MasterKey.CreateNew(ring.CatalogId));
        file.SetPassword(ring, "New-Password-77?", Fast);
        Assert.NotEqual(oldId, file.KeyId);

        using var now = file.TryUnlock("New-Password-77?")!;
        Assert.Equal([oldId, ring.KeyId], now.All.Select(k => k.KeyId));
        Assert.Equal(Raw(ring), Raw(now));
        using var old = beforeRotation.TryUnlock(Pw)!;
        Assert.Equal([oldId], old.All.Select(k => k.KeyId));      // a leaked old password + old disc: only the old key

        // a file of the old key opens with the new ring, one of the new key not with the old ring
        var oldData = FileCrypto.Seal(Data(100), ring.All[0], KeyPurpose.Manifest);
        var newData = FileCrypto.Seal(Data(100), ring.Current, KeyPurpose.Manifest);
        Assert.Equal(Data(100), FileCrypto.Open(oldData, now, KeyPurpose.Manifest));
        Assert.Equal(Data(100), FileCrypto.Open(newData, now, KeyPurpose.Manifest));
        Assert.Contains("another key", Assert.Throws<InvalidDataException>(() => FileCrypto.Open(newData, old, KeyPurpose.Manifest)).Message);
        Assert.Throws<ArgumentException>(() => ring.Add(MasterKey.CreateNew()));   // another catalog's key never joins
    }

    [Fact]
    public void A_key_file_of_format_1_still_opens_and_is_written_as_a_ring_afterwards()
    {
        using var key = MasterKey.CreateNew();
        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var wrapped = new byte[48];
        using (var aes = new AesGcm(Kdf.Derive(Pw, salt, Fast), 16))
            aes.Encrypt(nonce, key.Bytes, wrapped.AsSpan(0, 32), wrapped.AsSpan(32),
                        Encoding.ASCII.GetBytes($"bdxl-key-1|{key.CatalogId}|{key.KeyId}|password"));
        var file = new KeyFile
        {
            Format = "bdxl-key-1", CatalogId = key.CatalogId, KeyId = key.KeyId,
            Envelopes = [new KeyEnvelope { Kind = "password", Kdf = "argon2id", MemoryKiB = Fast.MemoryKiB, Passes = Fast.Passes, Parallelism = 1, Salt = salt, Nonce = nonce, Wrapped = wrapped }]
        };

        using var ring = file.TryUnlock(Pw)!;
        Assert.Equal(key.Bytes.ToArray(), Raw(ring));
        file.SetPassword(ring, Pw, Fast);
        Assert.Equal(KeyFile.CurrentFormat, file.Format);
        Assert.True(file.Password!.Ring);
        Assert.Equal(key.Bytes.ToArray(), Raw(file.TryUnlock(Pw)!));
    }

    [Fact]
    public void The_recovery_key_opens_the_master_key()
    {
        using var key = NewRing();
        var file = KeyFile.Create(key, Pw, Fast);
        string text = file.SetRecoveryKey(key);

        var parsed = RecoveryKey.Parse(text.ToLowerInvariant().Replace('-', ' '), out _)!;
        using var back = file.TryUnlockWithRecoveryKey(parsed);
        Assert.Equal(Raw(key), Raw(back!));
        Assert.Null(file.TryUnlockWithRecoveryKey(RecoveryKey.New()));

        Assert.True(file.RemoveRecoveryKey());
        Assert.Throws<UserException>(() => file.TryUnlockWithRecoveryKey(parsed));
        Assert.NotNull(file.TryUnlock(Pw));
    }

    [Fact]
    public void An_envelope_does_not_open_in_another_catalogs_key_file()
    {
        using var a = NewRing();
        using var b = NewRing();
        var fa = KeyFile.Create(a, Pw, Fast);
        var fb = KeyFile.Create(b, Pw, Fast);
        fb.Envelopes = fa.Envelopes;    // same password, envelope moved over
        Assert.Null(fb.TryUnlock(Pw));
    }

    [Fact]
    public void The_key_file_survives_a_write_and_read()
    {
        using var tmp = new TempDir();
        using var key = NewRing();
        var file = KeyFile.Create(key, Pw, Fast);
        file.SetRecoveryKey(key);
        file.WriteTo(tmp.Path);

        var back = KeyFile.ReadFrom(tmp.Path)!;
        Assert.Equal(key.CatalogId, back.CatalogId);
        Assert.Equal(2, back.Envelopes.Count);
        Assert.Equal(KeyEnvelope.PasswordKind, back.Envelopes[0].Kind);
        using var opened = back.TryUnlock(Pw);
        Assert.Equal(Raw(key), Raw(opened!));
        Assert.Null(KeyFile.ReadFrom(tmp.File("nothing-here")));
    }

    [Fact]
    public void Unreasonable_kdf_settings_in_a_key_file_are_refused()
    {
        using var key = NewRing();
        var file = KeyFile.Create(key, Pw, Fast);
        file.Password!.MemoryKiB = 64L * 1024 * 1024;   // 64 GiB: would just exhaust memory
        Assert.Throws<InvalidDataException>(() => file.TryUnlock(Pw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1 << 20)]
    [InlineData((1 << 20) + 1)]
    [InlineData(3 * (1 << 20) - 7)]
    public void Enc2_round_trips_around_chunk_boundaries(int size)
    {
        using var key = MasterKey.CreateNew();
        var data = Data(size);
        var sealedBytes = FileCrypto.Seal(data, key, KeyPurpose.Catalog);
        Assert.Equal(data, FileCrypto.Open(sealedBytes, key, KeyPurpose.Catalog));
        int chunks = Math.Max(1, (size + (1 << 20) - 1) / (1 << 20));
        Assert.Equal(56 + size + chunks * 21, sealedBytes.Length);
    }

    [Fact]
    public void Enc2_refuses_another_key_another_purpose_and_any_change()
    {
        using var key = MasterKey.CreateNew();
        using var other = MasterKey.CreateNew();
        var data = Data((1 << 20) + 100);
        var s = FileCrypto.Seal(data, key, KeyPurpose.Manifest);

        Assert.Contains("another key", Assert.Throws<InvalidDataException>(() => FileCrypto.Open(s, other, KeyPurpose.Manifest)).Message);
        Assert.Throws<InvalidDataException>(() => FileCrypto.Open(s, key, KeyPurpose.Catalog));

        var flipped = (byte[])s.Clone();
        flipped[100] ^= 1;
        Assert.Throws<InvalidDataException>(() => FileCrypto.Open(flipped, key, KeyPurpose.Manifest));

        int firstChunk = 56 + 4 + 1 + (1 << 20) + 16;   // cut after the first chunk: the "last" flag no longer matches
        Assert.Throws<InvalidDataException>(() => FileCrypto.Open(s[..firstChunk], key, KeyPurpose.Manifest));
        Assert.Throws<InvalidDataException>(() => FileCrypto.Open(s[..^1], key, KeyPurpose.Manifest));
    }

    [Fact]
    public void A_recovery_key_typo_is_caught_by_the_checksum()
    {
        var key = RecoveryKey.New();
        string text = RecoveryKey.Format(key);
        Assert.Equal(11, text.Split('-').Length);
        Assert.Equal(key, RecoveryKey.Parse(text.Replace('0', 'O').Replace('1', 'L'), out _));

        char wrong = text[3] == 'A' ? 'B' : 'A';
        string typo = text[..3] + wrong + text[4..];
        Assert.Null(RecoveryKey.Parse(typo, out string problem));
        Assert.Contains("mistyped", problem);
        Assert.Null(RecoveryKey.Parse(text[..^2], out _));
    }
}
