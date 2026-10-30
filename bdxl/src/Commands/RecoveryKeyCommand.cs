namespace Bdxl;

public sealed class RecoveryKeyCommand : ICommand
{
    public string Name => "recovery-key";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Create, replace or remove the catalog's recovery key";
    public string Usage => "recovery-key create | remove";
    public string Details => """
        The recovery key is a second way to open the master key, for when the password is lost:
        'password change --recovery' sets a new password with it. It is 32 random bytes shown once as text; bdxl
        keeps only a copy of the master key locked by it (in bdxl.key).
          recovery-key create    make one (replaces the old one: that one stops opening the catalog)
          recovery-key remove    the password becomes the only way in
        Discs staged earlier keep the bdxl.key of their time, with or without the recovery key it had then.
        """;

    public int Run(Ctx c)
    {
        string sub = c.Arg(0, Usage).ToLowerInvariant();
        string dir = c.Cat.Dir;
        var file = KeyFile.ReadFrom(dir)!;
        switch (sub)
        {
            case "create":
                if (file.Recovery != null && !Ui.Confirm(c.Opt, "This catalog has a recovery key. Replace it (the old one stops working)?"))
                {
                    Console.WriteLine("Cancelled.");
                    return 1;
                }
                string text = file.SetRecoveryKey(c.Keys);
                file.WriteTo(dir);
                CatalogSetup.ShowRecoveryKey(text);
                return 0;
            case "remove":
                if (file.Recovery == null) { Console.WriteLine("This catalog has no recovery key."); return 0; }
                if (!Ui.Confirm(c.Opt, "Remove the recovery key? Without the password nothing can be decrypted then.")) { Console.WriteLine("Cancelled."); return 1; }
                file.RemoveRecoveryKey();
                file.WriteTo(dir);
                Console.WriteLine("Recovery key removed.");
                return 0;
            default:
                throw new UserException("Usage: " + Usage);
        }
    }
}
