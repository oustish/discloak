namespace Discloak;

/// <summary>workflow - prints a sample session from first scan to a verified, burned disc.</summary>
public sealed class WorkflowCommand : ICommand
{
    public string Name => "workflow";
    public string Group => CommandGroup.Help;
    public string Summary => "Show a sample workflow from first scan to a verified disc";
    public string Usage => "workflow";
    public string Details => "Prints example commands only; nothing is executed.";
    public bool AllowBrokenCatalog => true;
    public bool NeedsCatalog => false;

    public int Run(Ctx c)
    {
        Console.WriteLine("""
            SAMPLE WORKFLOW  (nothing here is executed - copy the commands you need)
            Put the catalog on a safe disk of its own:  --catalog D:\discloak-catalog   (default: ./catalog)

            1. One-time setup
                 discloak init --recovery-key                      encrypted catalog: choose a password, write down the recovery key
                 discloak unlock                                   then no password per command until it locks (lock to end it)
                 discloak settings set label-prefix DISC-          disc names: DISC-0001, DISC-0002 ... (counter never resets)
                 discloak settings set currency PLN
                 discloak ignore add "node_modules/" "*.tmp" "Thumbs.db"     .gitignore syntax, kept in the catalog
                 discloak blanks add 5 --size 96882130944 --reserve 200MB --cost 45

            2. Index your data (hashes every file; --threads speeds it up on SSDs / several disks)
                 discloak scan D:\Photos --name Photos --threads 4
                 discloak scan E:\Documents --name Docs
                 discloak compression set Docs auto                 compress what compresses (zstd); scan again to count it
               Later, to pick up changes in ALL known locations at once:
                 discloak scan --threads 4

            3. See what would go where - without changing anything
                 discloak plan
               It tells you when the data does not fit on the blank discs you registered.

            4. Prepare a disc folder (the files go into encrypted 64 MiB packs: no names, sizes or count show)
                 discloak stage E:\Staging --max-discs 1 --min-fill 0.9 --with-catalog
               Burn the folder E:\Staging\DISC-0001 with your burning software (UDF 2.50 file system).

            5. Check the burned disc and record it
                 discloak verify DISC-0001 G:\                      the packs' hashes; --deep also decrypts and checks every file
                 discloak burned DISC-0001 --date 2026-10-29
                 discloak cost report                               cost per disc, per GB and per TB
               A disc that was not full and not finalized can take more later, as a new session:
                 discloak plan --append DISC-0001
                 discloak stage E:\Staging --append DISC-0001 --mount G:\ --with-catalog
               Burn E:\Staging\DISC-0001.s2 as the next session (continue the disc's file system), then verify + burned.

            6. Day-to-day
                 discloak status        discloak find holiday        discloak discs
                 discloak restore D:\Recovered "holiday" --mount G:\       copy files back (decrypted, checked by hash)

            7. When things go wrong
                 discloak lost DISC-0003 --reason "left on the train"     its files become pending again
                 discloak restage DISC-0003 E:\Staging --from G:\           rebuild the same disc (intact packs taken from G:)
                 discloak catalog check                                    is the catalog intact?
                 discloak catalog history   /   discloak catalog rollback <id>   older catalog versions (one per command)
                 discloak catalog backup create F:\safe                    catalog copy with repair data
                 discloak catalog backup verify F:\safe\catalog-... --repair
                 discloak catalog export F:\plain   /   catalog import F:\plain     read or fix the catalog as plain JSON
                 discloak password change                                  new password (--recovery if it was lost)
                 discloak password change --rotate-key                     it may have leaked: a new key for everything new
                 discloak init --key-from G:\ --catalog D:\new-catalog      catalog lost: start again with a disc's key ...
                 discloak scandisc G:\ --catalog D:\new-catalog            ... and read every disc back into it

            8. Housekeeping
                 discloak purge --dry-run          forget files that are gone and were never burned
                 discloak alias rename Docs Documents
            """);
        return 0;
    }
}
