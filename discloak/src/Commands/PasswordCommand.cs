namespace Discloak;

public sealed class PasswordCommand : ICommand
{
    public string Name => "password";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Change the catalog's password (also with the recovery key), optionally with a new master key; check a password";
    public string Usage => "password change [--rotate-key] [--recovery] [--new-password-env VAR] [--kdf-memory <size>] [--kdf-passes N] | password check [<folder>] [--recovery]";
    public bool NeedsCatalog => false;   // unlocks discloak.key itself (with the password or the recovery key)
    public string Details => """
        Asks for the current password (or, with --recovery, the recovery key: DISCLOAK_RECOVERY_KEY or typed), then the
        new one twice (or from the variable named by --new-password-env, default DISCLOAK_NEW_PASSWORD). The Argon2id
        settings are measured again unless --kdf-memory / --kdf-passes are given.
        Without --rotate-key only discloak.key changes: the master key stays, nothing else is rewritten.
          --rotate-key   also start a new master key (for when the old password or key may have leaked). The catalog
                         is re-encrypted with it, and every new disc, catalog backup and session uses it. Older keys
                         stay in the catalog's key ring (in discloak.key, under the new password), so every disc stays
                         readable, appended sessions and 'restage' keep a disc's own key. The recovery key, if there
                         is one, is replaced too (shown once). What a leaked old password or key can still open is
                         only what was made before: the discs (and their _catalog backups) of that time.
        Discs keep the discloak.key they were staged with: they still open with the password of that time.
        The new password is typed twice: the second time copy it from where you saved it (password manager), so a
        wrongly saved entry shows up now and not when you need it. Then you confirm that it is saved.

        password check [<folder>]   does this password (or with --recovery: this recovery key) open the discloak.key in
                                    the catalog, or in <folder> (a disc, a catalog backup)? Changes nothing and never
                                    uses the agent. Also tells when that discloak.key's password was set, which helps to
                                    find the right entry (or entry history) in a password manager.
        """;

    public int Run(Ctx c)
    {
        if (c.Args.Count is 1 or 2 && c.Args[0].Equals("check", StringComparison.OrdinalIgnoreCase))
            return Check(c, c.Args.Count == 2 ? Path.GetFullPath(c.Args[1]) : c.Cat.Dir);
        if (c.Args.Count != 1 || !c.Args[0].Equals("change", StringComparison.OrdinalIgnoreCase))
            throw new UserException("Usage: " + Usage);
        string dir = c.Cat.Dir;
        var file = KeyFile.ReadFrom(dir) ?? throw new UserException($"No encrypted catalog in {dir}.");
        using var keys = c.Opt.Has("recovery") ? CatalogSetup.UnlockWithRecoveryKey(file) : CatalogSetup.Unlock(dir, c.Opt);
        Mirror.RequireInSync(dir, keys);   // discloak.key must reach the mirror too
        bool rotate = c.Opt.Has("rotate-key");

        // the catalog has to load with the keys it has now; a broken one is not re-encrypted under a new key
        Catalog? cat = null;
        if (rotate)
        {
            cat = Catalog.Load(dir, "password-rotate-key", keys);
            if (cat.Integrity.State == IntegrityState.Broken)
                throw new UserException("The catalog fails its integrity check ('catalog check'); fix that before rotating the key.");
        }

        string password = Ui.NewPassword(c.Opt, "new-password-env", "DISCLOAK_NEW_PASSWORD");
        var kdf = Kdf.FromOptions(c.Opt);
        string? recovery = null;
        if (rotate) keys.Add(MasterKey.CreateNew(keys.CatalogId));
        file.SetPassword(keys, password, kdf);
        if (rotate && file.Recovery != null) recovery = file.SetRecoveryKey(keys);   // the old one cannot wrap the new key

        // discloak.key first: the catalog reads files of any key in the ring, so a stop between the two steps loses nothing
        file.WriteTo(dir);
        if (rotate)
        {
            cat!.Save();   // re-encrypted with the new key (the version before goes to history, under the old key)
            if (AgentProtocol.Enabled) new AgentClient().Lock(keys.CatalogId);   // it holds the old ring: 'unlock' again
        }
        Mirror.Sync(dir);

        Console.WriteLine($"Password changed ({kdf}).");
        if (rotate)
        {
            Console.WriteLine($"New master key {keys.KeyId}; the catalog is encrypted with it, and so is everything staged from now on. " +
                              $"Older keys ({string.Join(", ", keys.All.SkipLast(1).Select(k => k.KeyId))}) stay in the key ring for the discs made with them.");
            if (recovery != null) CatalogSetup.ShowRecoveryKey(recovery);
            Console.WriteLine("If the catalog was unlocked ('unlock'), it is locked now; unlock it again with the new password.");
        }
        Console.WriteLine("Discs staged before now still open with the password they were staged with" +
                          (rotate ? ", and only reveal what was made with the older keys." : "."));
        return 0;
    }

    /// <summary>password check: one attempt, no agent, nothing changed. 0 = it opens, 1 = it does not.</summary>
    static int Check(Ctx c, string dir)
    {
        var file = KeyFile.ReadFrom(dir) ?? throw new UserException($"No {KeyFile.FileName} in {dir}.");
        bool recovery = c.Opt.Has("recovery");
        string secret = recovery ? "recovery key" : "password";
        var envelope = (recovery ? file.Recovery : file.Password) ?? throw new UserException($"{KeyFile.FileName} in {dir} has no {secret}.");

        KeyRing? keys;
        if (recovery)
        {
            try { keys = CatalogSetup.UnlockWithRecoveryKey(file); }
            catch (UserException ex) when (ex.Message.Contains("does not belong")) { keys = null; }
        }
        else
        {
            string? fromEnv = Ui.PasswordFromEnvironment(c.Opt, out string envName);
            if (fromEnv == null && Console.IsInputRedirected)
                throw new UserException($"Set the environment variable {envName}, or run this in an interactive terminal.");
            if (fromEnv != null) Console.WriteLine($"Checking the password in {envName}.");
            keys = file.TryUnlock(fromEnv ?? Ui.ReadSecret("Password to check: "));
        }

        string what = $"{KeyFile.FileName} in {dir} (catalog {file.CatalogId}; its {secret} was set {envelope.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
        if (keys == null)
        {
            Console.WriteLine($"NO: this {secret} does not open {what}.");
            if (!recovery)
                Console.WriteLine("Look for the entry that was current at that time (in KeePass also the entry's history).");
            return 1;
        }
        using (keys)
            Console.WriteLine($"YES: it opens {what}; {keys.All.Count} key(s) in its key ring, current key {keys.KeyId}.");
        return 0;
    }
}
