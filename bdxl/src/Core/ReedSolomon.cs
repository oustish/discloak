namespace Bdxl;

/// <summary>GF(256) arithmetic (polynomial 0x11D) with cached multiplication tables.</summary>
public static class Gf
{
    static readonly byte[] Exp = new byte[512];
    static readonly byte[] Log = new byte[256];
    static readonly byte[]?[] Tables = new byte[256][];

    static Gf()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    public static byte Inv(byte a) => Exp[255 - Log[a]];   // a must be non-zero

    public static byte[] Table(byte c)
    {
        var t = Tables[c];
        if (t != null) return t;
        t = new byte[256];
        for (int v = 0; v < 256; v++) t[v] = Mul(c, (byte)v);
        Tables[c] = t;
        return t;
    }
}

/// <summary>
/// Reed-Solomon erasure code (Cauchy matrix over GF(256)), PAR2-style:
/// a file is cut into k &lt;= 200 equal blocks, m parity blocks are added, and a SHA-256 per block tells which blocks
/// are damaged. Any combination of up to m damaged blocks (data or parity) can be rebuilt.
/// </summary>
public static class Rs
{
    const int MaxData = 200;

    static string Hex(ReadOnlySpan<byte> d) => Convert.ToHexString(SHA256.HashData(d));
    static byte Coef(int row, int col, int m) => Gf.Inv((byte)(row ^ (m + col)));   // row < m <= m + col, so never 0

    static byte[][] Split(byte[] data, int k, int blockSize)
    {
        var blocks = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            var blk = new byte[blockSize];
            long off = (long)i * blockSize;
            int n = (int)Math.Max(0, Math.Min(blockSize, data.Length - off));
            if (n > 0) Buffer.BlockCopy(data, (int)off, blk, 0, n);
            blocks[i] = blk;
        }
        return blocks;
    }

    static byte[] Encode(byte[][] blocks, int m, int blockSize)
    {
        var parity = new byte[m * blockSize];
        for (int j = 0; j < m; j++)
        {
            int off = j * blockSize;
            for (int i = 0; i < blocks.Length; i++)
            {
                var t = Gf.Table(Coef(j, i, m));
                var src = blocks[i];
                for (int x = 0; x < blockSize; x++) parity[off + x] ^= t[src[x]];
            }
        }
        return parity;
    }

    public static RepairMeta Protect(string name, byte[] data, int pct, out byte[] parity)
    {
        string fileHash = Hex(data);
        if (data.Length == 0)
        {
            parity = Array.Empty<byte>();
            return new RepairMeta(name, 0, fileHash, 0, 0, 0, Array.Empty<string>());
        }

        long raw = Math.Max(4096, (data.Length + MaxData - 1L) / MaxData);
        int blockSize = (int)((raw + 4095) / 4096 * 4096);
        int k = (int)((data.Length + (long)blockSize - 1) / blockSize);          // <= 200
        int m = Math.Clamp((int)Math.Ceiling(k * pct / 100.0), 2, 255 - k);       // k + m <= 255

        var blocks = Split(data, k, blockSize);
        parity = Encode(blocks, m, blockSize);

        var hashes = new string[k + m];
        for (int i = 0; i < k; i++) hashes[i] = Hex(blocks[i]);
        for (int j = 0; j < m; j++) hashes[k + j] = Hex(parity.AsSpan(j * blockSize, blockSize));
        return new RepairMeta(name, data.Length, fileHash, blockSize, k, m, hashes);
    }

    /// <summary>Parity is non-null only when the repair data itself had damage and has been regenerated.</summary>
    public static (RsStatus Status, byte[] Data, byte[]? Parity, int BadData, int BadParity) Check(RepairMeta meta, byte[]? data, byte[]? par)
    {
        int k = meta.DataBlocks, m = meta.ParityBlocks, B = meta.BlockSize;

        if (k == 0)   // empty file
        {
            if (data != null && data.Length == 0) return (RsStatus.Ok, data, null, 0, 0);
            return (RsStatus.Repaired, Array.Empty<byte>(), null, 1, 0);
        }

        var blocks = Split(data ?? Array.Empty<byte>(), k, B);
        var badD = new List<int>();
        for (int i = 0; i < k; i++)
            if (Hex(blocks[i]) != meta.BlockSha256[i]) badD.Add(i);

        var pblk = new byte[m][];
        var badP = new HashSet<int>();
        for (int j = 0; j < m; j++)
        {
            var blk = new byte[B];
            long off = (long)j * B;
            if (par != null && par.Length >= off + B) Buffer.BlockCopy(par, (int)off, blk, 0, B);
            pblk[j] = blk;
            if (Hex(blk) != meta.BlockSha256[k + j]) badP.Add(j);
        }

        if (data != null && data.Length == meta.Length && badD.Count == 0 && badP.Count == 0 && Hex(data) == meta.FileSha256)
            return (RsStatus.Ok, data, null, 0, 0);

        var unrecoverable = (RsStatus.Unrecoverable, data ?? Array.Empty<byte>(), (byte[]?)null, badD.Count, badP.Count);
        var goodP = Enumerable.Range(0, m).Where(j => !badP.Contains(j)).ToList();
        if (badD.Count > goodP.Count) return unrecoverable;

        if (badD.Count > 0)
        {
            int d = badD.Count;
            var damagedSet = badD.ToHashSet();
            var rows = goodP.Take(d).ToArray();

            var a = new byte[d][];
            for (int r = 0; r < d; r++)
            {
                a[r] = new byte[d];
                for (int c = 0; c < d; c++) a[r][c] = Coef(rows[r], badD[c], m);
            }
            var inv = Invert(a, d);
            if (inv == null) return unrecoverable;

            // rhs[r] = parity_row - contribution of all intact data blocks = sum over damaged blocks
            var rhs = new byte[d][];
            for (int r = 0; r < d; r++)
            {
                var acc = (byte[])pblk[rows[r]].Clone();
                for (int i = 0; i < k; i++)
                {
                    if (damagedSet.Contains(i)) continue;
                    var t = Gf.Table(Coef(rows[r], i, m));
                    var src = blocks[i];
                    for (int x = 0; x < B; x++) acc[x] ^= t[src[x]];
                }
                rhs[r] = acc;
            }

            for (int c = 0; c < d; c++)
            {
                var rebuilt = new byte[B];
                for (int r = 0; r < d; r++)
                {
                    byte coef = inv[c][r];
                    if (coef == 0) continue;
                    var t = Gf.Table(coef);
                    var src = rhs[r];
                    for (int x = 0; x < B; x++) rebuilt[x] ^= t[src[x]];
                }
                blocks[badD[c]] = rebuilt;
            }
        }

        for (int i = 0; i < k; i++)
            if (Hex(blocks[i]) != meta.BlockSha256[i]) return unrecoverable;

        var fixedData = new byte[meta.Length];
        for (int i = 0; i < k; i++)
        {
            long off = (long)i * B;
            Buffer.BlockCopy(blocks[i], 0, fixedData, (int)off, (int)Math.Min(B, meta.Length - off));
        }
        if (Hex(fixedData) != meta.FileSha256) return unrecoverable;

        byte[]? newParity = badP.Count > 0 ? Encode(blocks, m, B) : null;
        return (RsStatus.Repaired, fixedData, newParity, badD.Count, badP.Count);
    }

    /// <summary>Gauss-Jordan inverse over GF(256); null if singular.</summary>
    static byte[][]? Invert(byte[][] a, int n)
    {
        var w = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            w[r] = new byte[2 * n];
            Array.Copy(a[r], w[r], n);
            w[r][n + r] = 1;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = -1;
            for (int r = col; r < n; r++)
                if (w[r][col] != 0) { piv = r; break; }
            if (piv < 0) return null;
            (w[col], w[piv]) = (w[piv], w[col]);

            byte iv = Gf.Inv(w[col][col]);
            for (int x = 0; x < 2 * n; x++) w[col][x] = Gf.Mul(w[col][x], iv);
            for (int r = 0; r < n; r++)
            {
                if (r == col || w[r][col] == 0) continue;
                byte f = w[r][col];
                for (int x = 0; x < 2 * n; x++) w[r][x] ^= Gf.Mul(f, w[col][x]);
            }
        }
        var inv = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            inv[r] = new byte[n];
            Array.Copy(w[r], n, inv[r], 0, n);
        }
        return inv;
    }
}
