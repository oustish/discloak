using System.Diagnostics;
using NSec.Cryptography;

namespace Bdxl;

/// <summary>Argon2id settings: memory in KiB and number of passes (parallelism is always 1, libsodium's only choice).</summary>
public readonly record struct KdfParams(long MemoryKiB, int Passes)
{
    public override string ToString() => $"Argon2id, {MemoryKiB / 1024:N0} MiB of memory, {Passes} pass(es)";
}

/// <summary>Turns a password into the key that unwraps the master key (Argon2id, RFC 9106, via libsodium).</summary>
public static class Kdf
{
    public const int SaltSize = 16;
    public const long RecommendedMinKiB = 256 * 1024;   // below this 'init' / 'password change' warn
    public const long MinKiB = 8 * 1024;                // the least that is accepted at all (tests use it)
    public const long MaxKiB = 4L * 1024 * 1024;        // a bdxl.key asking for more is not believed
    public const int MaxPasses = 100;
    static readonly TimeSpan Target = TimeSpan.FromSeconds(2);

    public static bool Valid(KdfParams p) => p.MemoryKiB >= MinKiB && p.MemoryKiB <= MaxKiB && p.Passes >= 1 && p.Passes <= MaxPasses;

    public static byte[] Derive(string password, ReadOnlySpan<byte> salt, KdfParams p)
    {
        if (!Valid(p)) throw new InvalidDataException($"unreasonable key derivation settings ({p.MemoryKiB} KiB, {p.Passes} passes)");
        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(
            new Argon2Parameters { DegreeOfParallelism = 1, MemorySize = p.MemoryKiB, NumberOfPasses = p.Passes });
        return algorithm.DeriveBytes(password, salt, 32);
    }

    /// <summary>
    /// Settings that take about 2 seconds here: 1 GiB of memory (less if this machine is slow or short of memory,
    /// never under 256 MiB) and as many passes as fit.
    /// </summary>
    public static KdfParams Calibrate()
    {
        var salt = new byte[SaltSize];
        long memory = 1024 * 1024;
        while (true)
        {
            double onePass, threePasses;
            try
            {
                Time(salt, new KdfParams(memory, 1));   // warm-up: the very first run also pays for one-time setup
                onePass = Time(salt, new KdfParams(memory, 1));
                threePasses = Time(salt, new KdfParams(memory, 3));
            }
            catch (Exception ex) when (ex is OutOfMemoryException or CryptographicException or InvalidOperationException)
            {
                if (memory <= RecommendedMinKiB) throw;
                memory /= 2;
                continue;
            }
            // the first pass also pays for filling the memory: time = fixed + passes * perPass
            double perPass = Math.Max((threePasses - onePass) / 2, 0.01), fixedCost = Math.Max(onePass - perPass, 0);
            if (onePass > Target.TotalSeconds * 0.75 && memory > RecommendedMinKiB) { memory /= 2; continue; }
            int passes = (int)Math.Round((Target.TotalSeconds - fixedCost) / perPass);
            return new KdfParams(memory, Math.Clamp(passes, 1, 10));
        }
    }

    static double Time(byte[] salt, KdfParams p)
    {
        var watch = Stopwatch.StartNew();
        Derive("calibration", salt, p);
        return watch.Elapsed.TotalSeconds;
    }

    /// <summary>Settings from --kdf-memory / --kdf-passes, or calibrated ones. Warns about weak settings.</summary>
    public static KdfParams FromOptions(Options opt)
    {
        if (!opt.Has("kdf-memory") && !opt.Has("kdf-passes"))
        {
            Console.Error.WriteLine("Measuring how much key derivation this computer does in about 2 seconds...");
            var calibrated = Calibrate();
            Console.Error.WriteLine($"Password protection: {calibrated}.");
            return calibrated;
        }
        long bytes = opt.GetSize("kdf-memory", 1L << 30);
        var p = new KdfParams(bytes / 1024, opt.GetInt("kdf-passes", 3));
        if (!Valid(p))
            throw new UserException($"--kdf-memory must be {MinKiB / 1024}MiB-{MaxKiB / 1024}MiB and --kdf-passes 1-{MaxPasses}.");
        if (p.MemoryKiB < RecommendedMinKiB)
            Console.Error.WriteLine($"Warning: {p.MemoryKiB / 1024:N0} MiB of memory makes guessing the password much cheaper " +
                                    $"than the recommended {RecommendedMinKiB / 1024:N0} MiB or more.");
        return p;
    }
}
