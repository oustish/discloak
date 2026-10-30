namespace Bdxl;

public sealed class PasswordCommand : ICommand
{
    public string Name => "password";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Change the catalog's password (also with the recovery key)";
    public string Usage => "password change [--recovery] [--new-password-env VAR] [--kdf-memory <size>] [--kdf-passes N]";
    public bool NeedsCatalog => false;   // unlocks bdxl.key itself (with the password or the recovery key)
    public string Details => """
        Only bdxl.key changes: the master key stays the same, so nothing else is rewritten and older catalog versions
        keep working. Asks for the current password (or, with --recovery, the recovery key: BDXL_RECOVERY_KEY or
        typed), then the new one twice (or from the variable named by --new-password-env, default BDXL_NEW_PASSWORD).
        The Argon2id settings are measured again unless --kdf-memory / --kdf-passes are given.
        Discs keep the bdxl.key they were staged with: they still open with the password of that time (and the
        catalog opens them with any password). A password change does not lock anyone out of an existing disc.
        """;

    public int Run(Ctx c)
    {
        if (c.Args.Count != 1 || !c.Args[0].Equals("change", StringComparison.OrdinalIgnoreCase))
            throw new UserException("Usage: " + Usage);
        string dir = c.Cat.Dir;
        var file = KeyFile.ReadFrom(dir) ?? throw new UserException($"No encrypted catalog in {dir}.");
        using var key = c.Opt.Has("recovery") ? CatalogSetup.UnlockWithRecoveryKey(file) : CatalogSetup.Unlock(dir, c.Opt);

        string password = Ui.NewPassword(c.Opt, "new-password-env", "BDXL_NEW_PASSWORD");
        var kdf = Kdf.FromOptions(c.Opt);
        file.SetPassword(key, password, kdf);
        file.WriteTo(dir);
        Console.WriteLine($"Password changed ({kdf}).");
        Console.WriteLine("Discs staged before now still open with the password they were staged with.");
        return 0;
    }
}
