namespace Bdxl;

/// <summary>workflow - prints a sample session from first scan to a verified, burned disc.</summary>
public static class WorkflowCommand
{
    public static int Run(Ctx c)
    {
        Console.WriteLine("""
            SAMPLE WORKFLOW  (nothing here is executed - copy the commands you need)
            Put the catalog on a safe disk of its own:  --catalog D:\bdxl-catalog   (default: ./catalog)

            1. One-time setup
                 bdxl settings set label-prefix BDXL-          disc names: BDXL-0001, BDXL-0002 ... (counter never resets)
                 bdxl settings set currency PLN
                 bdxl ignore add "node_modules/" "*.tmp" "Thumbs.db"     .gitignore syntax, kept in the catalog
                 bdxl blanks add 5 --size 96882130944 --reserve 200MB --cost 45

            2. Index your data (hashes every file; --threads speeds it up on SSDs / several disks)
                 bdxl scan D:\Photos --name Photos --threads 4
                 bdxl scan E:\Documents --name Docs
               Later, to pick up changes in ALL known locations at once:
                 bdxl scan --threads 4

            3. See what would go where - without changing anything
                 bdxl plan
               It tells you when the data does not fit on the blank discs you registered.

            4. Prepare a disc folder (add --encrypt to encrypt every file with a password)
                 bdxl stage E:\Staging --max-discs 1 --min-fill 0.9 --with-catalog
               Burn the folder E:\Staging\BDXL-0001 with your burning software (UDF 2.50 file system).

            5. Check the burned disc and record it
                 bdxl verify BDXL-0001 G:\                      add --deep for encrypted discs to decrypt and compare
                 bdxl burned BDXL-0001 --date 2026-10-29
                 bdxl cost report                               cost per disc, per GB and per TB

            6. Day-to-day
                 bdxl status        bdxl find holiday        bdxl discs
                 bdxl restore D:\Recovered "holiday" --mount G:\       copy files back (asks for the password if encrypted)

            7. When things go wrong
                 bdxl lost BDXL-0003 --reason "left on the train"     its files become pending again
                 bdxl restage BDXL-0003 E:\Staging --from H:\recovered   rebuild the same disc to burn again
                 bdxl catalog check                                    is the catalog intact?
                 bdxl catalog history   /   bdxl catalog rollback <id>   older catalog versions (one per command)
                 bdxl catalog backup create F:\safe                    catalog copy with repair data
                 bdxl catalog backup verify F:\safe\catalog-... --repair

            8. Discs you already have
                 bdxl scandisc G:\ --label OLD-2019-01 --method hash --cost 30 --write-disc-json

            9. Housekeeping
                 bdxl purge --dry-run          forget files that are gone and were never burned
                 bdxl alias rename Docs Documents
            """);
        return 0;
    }
}
