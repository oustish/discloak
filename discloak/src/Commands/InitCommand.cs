namespace Discloak;

public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Create an encrypted catalog (or encrypt an existing plaintext one)";
    public string Usage => "init [--recovery-key] [--kdf-memory <size>] [--kdf-passes N] [--force] | init --key-from <disc or folder> [--recovery]";
    public bool NeedsCatalog => false;
    public string Details => """
        Creates the catalog folder (--catalog, default ./catalog) with a new random master key that encrypts the
        catalog and, later, the discs. The key is stored in discloak.key, wrapped by your password (typed twice, or
        DISCLOAK_PASSWORD / --password-env VAR; at least 12 characters, 3 of: lower, upper, digit, special).
          --recovery-key        also make a recovery key (shown once) that can replace a lost password
          --kdf-memory/-passes  Argon2id settings; by default they are measured so unlocking takes about 2 seconds
                                (less than 256MB of memory is allowed, with a warning)
        On a folder that holds a catalog from before encryption, the catalog and its history are encrypted and the
        plaintext files deleted (--force: even if the old catalog fails its own integrity check). Any other command
        offers to do the same when it finds such a catalog.
          --key-from <path>     the catalog is lost: start a new one with the master key from discloak.key on an encrypted
                                disc (or in a catalog backup), so it can read every disc made with that key. Asks for
                                the password that key file was written with (--recovery: the recovery key instead).
                                Then restore the newest catalog backup into it, or scandisc each disc.
        """;

    public int Run(Ctx c)
    {
        string dir = c.Cat.Dir;
        CatalogSetup.RecoverInterruptedMigration(dir);
        switch (Catalog.Detect(dir))
        {
            case CatalogKind.Encrypted:
                throw new UserException($"{dir} already holds an encrypted catalog. To change its password: password change");
            case CatalogKind.Plain:
                if (c.Opt.Has("key-from")) throw new UserException($"{dir} holds a catalog already; --key-from needs an empty folder.");
                using (CatalogSetup.Migrate(dir, c.Opt)) { }
                return 0;
            case CatalogKind.None when c.Opt.Get("key-from") is { } source:
                using (CatalogSetup.CreateFromKeyFile(dir, source, c.Opt)) { }
                return 0;
            default:
                using (CatalogSetup.CreateNew(dir, c.Opt)) { }
                Console.WriteLine("Next: scan a folder (discloak scan <folder>) - see 'discloak workflow'.");
                return 0;
        }
    }
}
