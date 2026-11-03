namespace Discloak.Tests;

public class ReedSolomonTests
{
    static byte[] Data(int size, int seed = 1)
    {
        var d = new byte[size];
        new Random(seed).NextBytes(d);
        return d;
    }

    static void Damage(byte[] buffer, int block, int blockSize) => buffer[block * blockSize + 17] ^= 0xFF;

    [Fact]
    public void Gf_multiplication_and_inverse()
    {
        for (int a = 1; a < 256; a++)
        {
            Assert.Equal(1, Gf.Mul((byte)a, Gf.Inv((byte)a)));
            Assert.Equal(0, Gf.Mul((byte)a, 0));
        }
    }

    [Fact]
    public void Intact_data_is_ok()
    {
        byte[] data = Data(200_000);
        var meta = Rs.Protect("files.jsonl", data, 25, out byte[] parity);
        var r = Rs.Check(meta, data, parity);
        Assert.Equal(RsStatus.Ok, r.Status);
        Assert.Equal(data, r.Data);
    }

    [Fact]
    public void Up_to_m_damaged_blocks_are_rebuilt_data_and_parity_mixed()
    {
        byte[] original = Data(200_000);
        var meta = Rs.Protect("files.jsonl", original, 25, out byte[] parity);
        int m = meta.ParityBlocks;
        Assert.True(m >= 4);

        byte[] data = (byte[])original.Clone();
        byte[] par = (byte[])parity.Clone();
        for (int i = 0; i < m - 2; i++) Damage(data, i * 3, meta.BlockSize);   // m-2 data blocks spread around
        Damage(par, 0, meta.BlockSize);
        Damage(par, m - 1, meta.BlockSize);                                     // + 2 parity blocks = m in total

        var r = Rs.Check(meta, data, par);
        Assert.Equal(RsStatus.Repaired, r.Status);
        Assert.Equal(original, r.Data);
        Assert.Equal(m - 2, r.BadData);
        Assert.Equal(2, r.BadParity);
        Assert.Equal(parity, r.Parity);   // damaged repair data is regenerated
    }

    [Fact]
    public void More_than_m_damaged_blocks_is_unrecoverable()
    {
        byte[] data = Data(200_000);
        var meta = Rs.Protect("files.jsonl", data, 25, out byte[] parity);
        for (int i = 0; i <= meta.ParityBlocks; i++) Damage(data, i, meta.BlockSize);
        Assert.Equal(RsStatus.Unrecoverable, Rs.Check(meta, data, parity).Status);
    }

    [Fact]
    public void A_truncated_file_is_rebuilt()
    {
        byte[] original = Data(50_000);
        var meta = Rs.Protect("discs.jsonl", original, 25, out byte[] parity);
        var r = Rs.Check(meta, original[..40_000], parity);
        Assert.Equal(RsStatus.Repaired, r.Status);
        Assert.Equal(original, r.Data);
    }

    [Fact]
    public void A_missing_small_file_comes_back_from_parity_alone()
    {
        byte[] original = Data(3000);   // one block, at least two parity blocks
        var meta = Rs.Protect("settings.json", original, 25, out byte[] parity);
        var r = Rs.Check(meta, null, parity);
        Assert.Equal(RsStatus.Repaired, r.Status);
        Assert.Equal(original, r.Data);
    }

    [Fact]
    public void Empty_file()
    {
        var meta = Rs.Protect("blanks.jsonl", [], 25, out byte[] parity);
        Assert.Empty(parity);
        Assert.Equal(RsStatus.Ok, Rs.Check(meta, [], parity).Status);
        Assert.Equal(RsStatus.Repaired, Rs.Check(meta, null, parity).Status);
    }
}
