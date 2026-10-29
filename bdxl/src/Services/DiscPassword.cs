namespace Bdxl;

/// <summary>
/// The password of an existing encrypted disc, checked before anything is encrypted with it - so a disc never ends
/// up with files (or a manifest) under two different passwords.
/// </summary>
public static class DiscPassword
{
    /// <summary>
    /// Asks for the password and checks it against the disc's KeyCheck. A disc without one (imported, or made before
    /// it existed) is checked against the first existing file in probes (encrypted files from that disc), and gets a
    /// KeyCheck. With nothing to check against: allowUnchecked = the password is asked twice and recorded, otherwise
    /// an error.
    /// </summary>
    public static KeyCache Get(Ctx c, DiscRecord disc, IEnumerable<string> probes, bool allowUnchecked)
    {
        if (disc.KeyCheck != null)
        {
            var keys = new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: false));
            if (!FileCrypto.MatchesKeyCheck(disc.KeyCheck, keys))
                throw new UserException($"That is not the password of {disc.Label}.");
            return keys;
        }

        string? probe = probes.FirstOrDefault(File.Exists);
        if (probe == null && !allowUnchecked)
            throw new UserException($"The catalog cannot check the password of {disc.Label} yet. Put the disc in the drive and add " +
                                    "--mount <path>, so it is checked against a file on the disc.");
        var k = new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: probe == null));
        if (probe != null && !FileCrypto.OpensWith(probe, k))
            throw new UserException($"That password does not open {probe} (wrong password, or the file is damaged).");
        if (probe == null)
            Console.WriteLine($"Note: there was nothing to check the password of {disc.Label} against; it is recorded as the disc's password now.");
        disc.KeyCheck = FileCrypto.NewKeyCheck(k);
        return k;
    }
}
