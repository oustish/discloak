namespace Bdxl;

/// <summary>
/// Creating, unlocking and migrating encrypted catalogs (see ENCRYPTION.md).
/// Migration of a plaintext catalog is crash-safe: the encrypted catalog is built and checked in .encrypting/, the
/// plaintext files are moved to .plaintext/, the encrypted ones moved in with bdxl.key last. bdxl.key in the folder
/// means the catalog is encrypted; without it, a half-done migration is undone (see RecoverInterruptedMigration).
/// </summary>
public static class CatalogSetup
{
    const string BuildFolder = ".encrypting";
    const string PlainFolder = ".plaintext";
    public const string RecoveryKeyEnv = "BDXL_RECOVERY_KEY";

    // ---- unlocking ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The master key of the catalog in dir: from the agent if it holds it ('unlock'), otherwise from the password
    /// (environment, or typed: 3 attempts).
    /// </summary>
    public static KeyRing Unlock(string dir, Options opt)
    {
        var file = KeyFile.ReadFrom(dir) ?? throw new UserException($"No catalog in {dir}.");
        if (AgentProtocol.Enabled && new AgentClient().TryGetKey(file.CatalogId, file.KeyId) is { } fromAgent) return fromAgent;
        return UnlockWithPassword(file, opt, $"the catalog in {dir}");
    }

    /// <summary>The master key of a key file from the password (environment, or typed: 3 attempts). what names it in messages.</summary>
    public static KeyRing UnlockWithPassword(KeyFile file, Options opt, string what)
    {
        string? fromEnv = Ui.PasswordFromEnvironment(opt, out string envName);
        if (fromEnv != null)
            return file.TryUnlock(fromEnv) ?? throw new UserException($"The password in {envName} does not open {what}.");
        if (Console.IsInputRedirected)
            throw new UserException($"{char.ToUpperInvariant(what[0])}{what[1..]} is encrypted: set the environment variable {envName}, or run this in an interactive terminal.");
        for (int attempt = 1; ; attempt++)
        {
            var key = file.TryUnlock(Ui.ReadSecret("Password: "));
            if (key != null) return key;
            if (attempt == 3) throw new UserException("Wrong password (3 attempts).");
            Console.Error.WriteLine("Wrong password, try again.");
        }
    }

    /// <summary>The master key from the recovery key (BDXL_RECOVERY_KEY, or typed).</summary>
    public static KeyRing UnlockWithRecoveryKey(KeyFile file)
    {
        string? text = Environment.GetEnvironmentVariable(RecoveryKeyEnv);
        if (string.IsNullOrEmpty(text))
        {
            if (Console.IsInputRedirected) throw new UserException($"Set {RecoveryKeyEnv}, or run this in an interactive terminal.");
            text = Ui.ReadSecret("Recovery key: ");
        }
        var bytes = RecoveryKey.Parse(text, out string problem) ?? throw new UserException($"Not a recovery key: {problem}.");
        try { return file.TryUnlockWithRecoveryKey(bytes) ?? throw new UserException("That recovery key does not belong to this catalog."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // ---- creating -------------------------------------------------------------------------------------------------

    /// <summary>A new master key and its key file (password typed twice or from the environment; --recovery-key adds one).</summary>
    static (KeyRing Key, KeyFile File, string? Recovery) NewKey(Options opt)
    {
        string password = Ui.NewPassword(opt, "password-env");
        var kdf = Kdf.FromOptions(opt);
        var key = KeyRing.Of(MasterKey.CreateNew());
        var file = KeyFile.Create(key, password, kdf);
        string? recovery = opt.Has("recovery-key") ? file.SetRecoveryKey(key) : null;
        return (key, file, recovery);
    }

    /// <summary>Creates a new, empty encrypted catalog in dir.</summary>
    public static KeyRing CreateNew(string dir, Options opt)
    {
        var (key, file, recovery) = NewKey(opt);
        Directory.CreateDirectory(dir);
        Catalog.CreateEmpty(dir, "init", key).Save();
        file.WriteTo(dir);
        Console.WriteLine($"Created an encrypted catalog in {dir}.");
        AfterCreate(recovery);
        return key;
    }

    /// <summary>
    /// A new, empty catalog in dir with the master key of an existing bdxl.key (from a disc, a catalog backup or
    /// another catalog folder): it can read every disc made with that key. The password (or --recovery) is the one
    /// that key file was written with.
    /// </summary>
    public static KeyRing CreateFromKeyFile(string dir, string source, Options opt)
    {
        string from = Path.GetFullPath(source);
        var file = KeyFile.ReadFrom(from) ?? throw new UserException($"No {KeyFile.FileName} in {from}.");
        var key = opt.Has("recovery") ? UnlockWithRecoveryKey(file) : UnlockWithPassword(file, opt, $"{KeyFile.FileName} in {from}");
        Directory.CreateDirectory(dir);
        Catalog.CreateEmpty(dir, "init", key).Save();
        file.WriteTo(dir);
        Console.WriteLine($"Created an encrypted catalog in {dir} with the key from {from} (catalog id {file.CatalogId}).");
        Console.WriteLine("It is empty. Fill it from a catalog backup or from the discs:");
        Console.WriteLine($"  bdxl catalog backup restore <disc>\\_catalog --force --catalog \"{dir}\"    (the newest backup you have)");
        Console.WriteLine($"  bdxl scandisc <disc> --catalog \"{dir}\"                                    (each disc)");
        if (file.Password?.CreatedUtc is { } changed)
            Console.WriteLine($"Its password is the one of {changed:yyyy-MM-dd}; change it with 'password change' if you like.");
        return key;
    }

    static void AfterCreate(string? recovery)
    {
        if (recovery != null) ShowRecoveryKey(recovery);
        else Console.WriteLine("No recovery key: if the password is lost, nothing can be decrypted. Add one any time: recovery-key create");
    }

    public static void ShowRecoveryKey(string text)
    {
        Console.WriteLine();
        Console.WriteLine("RECOVERY KEY - shown only now. Write it down or print it, and keep it away from this computer:");
        Console.WriteLine();
        Console.WriteLine("    " + text);
        Console.WriteLine();
        Console.WriteLine("It opens the catalog and every disc staged from now on if the password is lost ('password change --recovery').");
    }

    // ---- migration of a plaintext catalog -------------------------------------------------------------------------

    /// <summary>Encrypts the plaintext catalog in dir (see the class summary) and its history. Returns the new master key.</summary>
    public static KeyRing Migrate(string dir, Options opt)
    {
        var problems = PlainProblems(dir);
        if (problems.Count > 0 && !opt.Has("force"))
            throw new UserException("The plaintext catalog fails its own integrity check, so it is not encrypted as it is:\n  " +
                                    string.Join("\n  ", problems) + "\nAdd --force to encrypt it anyway.");
        var files = JsonStore.ReadLines<FileRecord>(Path.Combine(dir, "files.jsonl"));
        var discs = JsonStore.ReadLines<DiscRecord>(Path.Combine(dir, "discs.jsonl"));
        var blanks = JsonStore.ReadLines<BlankDisc>(Path.Combine(dir, "blanks.jsonl"));
        var settings = JsonStore.ReadObject<Settings>(Path.Combine(dir, "settings.json")) ?? new Settings();
        Console.WriteLine($"Encrypting the catalog in {dir}: {files.Count:N0} files, {discs.Count:N0} discs, {blanks.Count:N0} blank discs.");

        var (key, keyFile, recovery) = NewKey(opt);

        // 1. build and check the encrypted catalog on the side
        string build = Path.Combine(dir, BuildFolder);
        if (Directory.Exists(build)) Directory.Delete(build, true);
        var cat = Catalog.CreateEmpty(build, "init", key);
        cat.ReplaceContents(files, discs, blanks, settings);
        cat.Save();
        var check = Catalog.Load(build, "init", key);
        if (check.Integrity.State != IntegrityState.Ok || check.Files.Count != files.Count || check.Discs.Count != discs.Count)
            throw new InvalidOperationException("The encrypted catalog did not read back correctly; nothing was changed.");
        keyFile.WriteTo(build);

        // 2. swap: plaintext out, encrypted in, bdxl.key last
        string plain = Path.Combine(dir, PlainFolder);
        Directory.CreateDirectory(plain);
        foreach (string name in Catalog.CoveredFiles.Append(Catalog.IntegrityFile))
            if (File.Exists(Path.Combine(dir, name))) File.Move(Path.Combine(dir, name), Path.Combine(plain, name), overwrite: true);
        foreach (string name in Catalog.StoredFiles.Append(Catalog.IntegrityFile))
            if (File.Exists(Path.Combine(build, name))) File.Move(Path.Combine(build, name), Path.Combine(dir, name), overwrite: true);
        File.Move(KeyFile.PathIn(build), KeyFile.PathIn(dir));

        // 3. history and the plaintext leftovers
        FinishMigration(dir, key);
        Console.WriteLine("Done. The plaintext files were deleted (on an SSD, deleted data may stay recoverable for a while).");
        AfterCreate(recovery);
        return key;
    }

    /// <summary>A migration that stopped before bdxl.key was moved in is undone, so the plaintext catalog is whole again.</summary>
    public static void RecoverInterruptedMigration(string dir)
    {
        if (File.Exists(KeyFile.PathIn(dir))) return;
        string plain = Path.Combine(dir, PlainFolder), build = Path.Combine(dir, BuildFolder);
        if (Directory.Exists(plain))
        {
            foreach (string name in Catalog.StoredFiles.Append(Catalog.IntegrityFile))
                if (File.Exists(Path.Combine(dir, name))) File.Delete(Path.Combine(dir, name));
            foreach (string path in Directory.GetFiles(plain))
                File.Move(path, Path.Combine(dir, Path.GetFileName(path)), overwrite: true);
            Directory.Delete(plain, true);
            Console.Error.WriteLine("Note: an earlier encryption of this catalog was interrupted; the plaintext catalog was put back.");
        }
        if (Directory.Exists(build)) Directory.Delete(build, true);
    }

    /// <summary>True if a migration left plaintext behind (history snapshots or the moved-away files).</summary>
    public static bool HasPlaintextLeftovers(string dir) =>
        Directory.Exists(Path.Combine(dir, PlainFolder)) || Directory.Exists(Path.Combine(dir, BuildFolder)) ||
        Catalog.ListHistory(dir).Any(id => Catalog.CoveredFiles.Any(n => File.Exists(Path.Combine(dir, Catalog.HistoryFolder, id, n))));

    /// <summary>Encrypts plaintext history snapshots and deletes the plaintext left by a migration.</summary>
    public static void FinishMigration(string dir, KeyRing key)
    {
        int converted = 0;
        foreach (string id in Catalog.ListHistory(dir))
        {
            string snap = Path.Combine(dir, Catalog.HistoryFolder, id);
            var plainNames = Catalog.CoveredFiles.Where(n => File.Exists(Path.Combine(snap, n))).ToList();
            if (plainNames.Count == 0) continue;
            var old = File.Exists(Path.Combine(snap, Catalog.IntegrityFile)) ? JsonStore.ReadObject<IntegrityManifest>(Path.Combine(snap, Catalog.IntegrityFile)) : null;
            if (old?.KeyId == null)   // not converted yet (otherwise only the plaintext deletion was cut off)
            {
                bool damaged = PlainProblems(snap).Count > 0;
                foreach (string name in plainNames)
                    File.WriteAllBytes(Path.Combine(snap, name + Catalog.Suffix), FileCrypto.Seal(File.ReadAllBytes(Path.Combine(snap, name)), key.Current, KeyPurpose.Catalog));
                Catalog.WriteIntegrity(snap, key.Current, 0, unverified: damaged);
                converted++;
            }
            foreach (string name in plainNames) File.Delete(Path.Combine(snap, name));
        }
        if (converted > 0) Console.WriteLine($"Encrypted {converted} older catalog version(s) in history/.");
        foreach (string folder in new[] { PlainFolder, BuildFolder })
            if (Directory.Exists(Path.Combine(dir, folder))) Directory.Delete(Path.Combine(dir, folder), true);
    }

    /// <summary>Problems of a plaintext catalog (or snapshot) against its old SHA-256 hash file; none if it has no hash file.</summary>
    static List<string> PlainProblems(string dir)
    {
        var problems = new List<string>();
        string manifestPath = Path.Combine(dir, Catalog.IntegrityFile);
        if (!File.Exists(manifestPath)) return problems;
        IntegrityManifest? manifest;
        try { manifest = JsonStore.ReadObject<IntegrityManifest>(manifestPath); }
        catch (JsonException ex) { return [$"{Catalog.IntegrityFile} cannot be read: {ex.Message}"]; }
        if (manifest == null) return [$"{Catalog.IntegrityFile} is empty"];
        foreach (var (name, hash) in manifest.Files)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) problems.Add($"{name}: missing");
            else if (!Hashing.SameHash(Hashing.HashFile(path), hash)) problems.Add($"{name}: contents do not match the recorded hash");
        }
        foreach (string name in Catalog.CoveredFiles.Where(n => File.Exists(Path.Combine(dir, n)) && !manifest.Files.ContainsKey(n)))
            problems.Add($"{name}: exists but is not covered by {Catalog.IntegrityFile}");
        return problems;
    }
}
