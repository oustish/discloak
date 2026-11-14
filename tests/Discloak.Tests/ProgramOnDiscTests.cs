using System.IO.Compression;

namespace Discloak.Tests;

/// <summary>
/// The program on every disc (_program: the exe, its source, the licenses). The tests' discs get small stand-ins
/// (TestKeys.ProgramFiles); the real files are checked where the exe carries them, in its resources.
/// </summary>
[Collection(nameof(InterruptTests))]   // non-parallel: reads what commands print
public class ProgramOnDiscTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    int Discloak(params string[] args)
    {
        TestKeys.Init(tmp.File("cat"));
        return Program.Main([.. args, "--catalog", tmp.File("cat"), "--yes"]);
    }

    (int Code, string Text) Run(params string[] args)
    {
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try { return (Discloak(args), output.ToString()); }
        finally { Console.SetOut(stdout); }
    }

    string Disc(string label) => tmp.File(Path.Combine("stg", label));
    string ProgramIn(string label) => Path.Combine(Disc(label), DiscProgram.FolderName);

    [Fact]
    public void Every_disc_gets_the_program_unless_left_out_and_verify_checks_it()
    {
        tmp.RandomFile("src/a.bin", 30_000, seed: 1);
        Assert.Equal(0, Discloak("blanks", "add", "2", "--size", "1MB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        foreach (var (name, data) in TestKeys.ProgramFiles)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(ProgramIn("DISC-0001"), name)));
        Assert.Contains("_program/", File.ReadAllText(Path.Combine(Disc("DISC-0001"), DiscFiles.ReadmeName)));

        var (code, text) = Run("verify", "DISC-0001", Disc("DISC-0001"), "--deep");
        Assert.Equal(0, code);
        Assert.Contains($"OK       {DiscProgram.FolderName} ({DiscProgram.ExeName}", text);

        // damaged: the source zip does not open, the exe is missing
        string zip = Path.Combine(ProgramIn("DISC-0001"), TestKeys.ProgramFiles[1].Name);
        byte[] good = File.ReadAllBytes(zip);
        File.WriteAllBytes(zip, good[..(good.Length / 2)]);
        (code, text) = Run("verify", "DISC-0001", Disc("DISC-0001"));
        Assert.Equal(3, code);
        Assert.Contains($"PROBLEM  {DiscProgram.FolderName}: {TestKeys.ProgramFiles[1].Name} cannot be read", text);
        File.WriteAllBytes(zip, good);
        File.Delete(Path.Combine(ProgramIn("DISC-0001"), DiscProgram.ExeName));
        Assert.Contains($"PROBLEM  {DiscProgram.FolderName}: {DiscProgram.ExeName} is missing", Run("verify", "DISC-0001", Disc("DISC-0001")).Text);

        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--without-program"));
        Assert.False(Directory.Exists(ProgramIn("DISC-0002")));
        Assert.Contains($"-        {DiscProgram.FolderName}: none on this disc", Run("verify", "DISC-0002", Disc("DISC-0002")).Text);
    }

    [Fact]
    public void The_plan_keeps_room_for_the_program_and_restage_adds_it_only_where_there_is_room()
    {
        // a file that fills the disc when no room is kept for the program
        long usable = 900_000;   // 1 MB disc, 100 KB reserve (for disc.json, the key file, the README)
        long share = DiscSpace.ManifestShare(Piece.Whole(new FileRecord { RootName = "S", RelPath = "full.bin" }));
        tmp.RandomFile("src/full.bin", (int)(DiscFormat.Room(usable) - share - 100), seed: 1);   // 100 bytes left: far less than the program
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(2, Discloak("plan"));                                   // with the program it does not fit
        Assert.Equal(0, Discloak("plan", "--without-program"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--without-program"));
        Assert.Equal(0, Discloak("burned", "DISC-0001"));

        // that disc is full: rebuilt with the same layout, it has no room for a program larger than what its last
        // pack leaves free (the stand-ins are smaller; this collection runs alone, so they can be swapped here)
        Assert.Equal(0, Discloak("lost", "DISC-0001"));
        DiscProgram.UseFilesForTests([(DiscProgram.ExeName, new byte[200_000])]);
        try
        {
            var (code, text) = Run("restage", "DISC-0001", tmp.File("re"));
            Assert.Equal(0, code);
            Assert.Contains($"no {DiscProgram.FolderName} folder: the disc has no room left for it", text);
        }
        finally { DiscProgram.UseFilesForTests(TestKeys.ProgramFiles); }
        Assert.False(Directory.Exists(Path.Combine(tmp.File("re"), "DISC-0001", DiscProgram.FolderName)));

        // a disc with room gets it when it is rebuilt
        tmp.RandomFile("src2/small.bin", 30_000, seed: 2);
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src2"), "--name", "T"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg2"), "--without-program"));
        Assert.Equal(0, Discloak("burned", "DISC-0002"));
        Assert.Equal(0, Discloak("lost", "DISC-0002"));
        Assert.Equal(0, Discloak("restage", "DISC-0002", tmp.File("re2")));
        Assert.True(File.Exists(Path.Combine(tmp.File("re2"), "DISC-0002", DiscProgram.FolderName, DiscProgram.ExeName)));
    }

    [Fact]
    public void The_exe_carries_the_licenses_and_its_source_and_the_readmes_name_the_project()
    {
        var me = typeof(DiscProgram).Assembly;
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(repo, "Discloak.slnx")), repo);
        foreach (string name in new[] { "LICENSE.md", "LICENSING.md", "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES-dotnet.txt" })
        {
            using var resource = me.GetManifestResourceStream(name)!;
            var bytes = new MemoryStream();
            resource.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(Path.Combine(repo, name)), bytes.ToArray());
        }

        // the source: the tree the exe was built from, as git archive writes it (nothing git ignores)
        using var source = new ZipArchive(me.GetManifestResourceStream("discloak-source.zip")!);
        var names = source.Entries.Select(e => e.FullName).ToList();
        string root = $"discloak-{BuildInfo.Version}/";
        Assert.Contains(root + "discloak/Discloak.csproj", names);
        Assert.Contains(root + "discloak/src/Services/DiscProgram.cs", names);
        Assert.DoesNotContain(names, n => n.Contains("/bin/") || n.Contains("/obj/") || n.StartsWith(root + "publish/"));

        Assert.Contains(BuildInfo.ProjectUrl, DiscFiles.Readme);
        tmp.RandomFile("src/a.bin", 1000, seed: 1);
        Assert.Equal(0, Discloak("catalog", "backup", "create", tmp.File("backup")));
        string backup = Directory.GetDirectories(tmp.File("backup")).Single();
        Assert.Contains(BuildInfo.ProjectUrl, File.ReadAllText(Path.Combine(backup, "README.txt")));
        Assert.Contains(BuildInfo.ProjectUrl, Run("version").Text);
    }

    [Fact]
    public void Stage_refresh_brings_staged_folders_up_to_date_and_leaves_burned_ones_alone()
    {
        tmp.RandomFile("src/a.bin", 800_000, seed: 1);   // one file per disc
        tmp.RandomFile("src/b.bin", 800_000, seed: 2);
        Assert.Equal(0, Discloak("blanks", "add", "2", "--size", "2MB", "--reserve", "600KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--max-discs", "2", "--with-catalog"));
        byte[] Discs(string label) => File.ReadAllBytes(Path.Combine(Disc(label), DiscFiles.CatalogBackupName, "discs.jsonl.enc"));
        Assert.NotEqual(Discs("DISC-0001"), Discs("DISC-0002"));   // DISC-0001's backup was taken before DISC-0002 was staged

        // a newer program; a damaged README and key file
        (string, byte[])[] newer = [(DiscProgram.ExeName, "a newer stand-in for discloak.exe"u8.ToArray()),
                                    ("discloak-0.0.1-source.zip", TestKeys.ProgramFiles[1].Data), ("LICENSE.md", "the license"u8.ToArray())];
        string readme = Path.Combine(Disc("DISC-0002"), DiscFiles.ReadmeName), key = Path.Combine(Disc("DISC-0001"), KeyFile.FileName);
        File.WriteAllText(readme, "changed");
        File.WriteAllText(key, "{}");
        DiscProgram.UseFilesForTests(newer);
        try
        {
            var (code, text) = Run("stage", "refresh", tmp.File("stg"), "--dry-run");
            Assert.Equal(0, code);
            Assert.Contains("Would refresh 2 folder(s) (--dry-run: nothing was changed).", text);
            Assert.Equal("changed", File.ReadAllText(readme));
            Assert.False(Directory.Exists(Path.Combine(Disc("DISC-0001"), ".refresh")));

            (code, text) = Run("stage", "refresh", tmp.File("stg"));
            Assert.Equal(0, code);
            foreach (string line in new[] { "_catalog       rewritten: the catalog as it is now", "_program       replaced: ",
                                            "README.txt     rewritten", "discloak.key   rewritten", "Refreshed 2 folder(s)." })
                Assert.Contains(line, text);
            Assert.Equal(Discs("DISC-0001"), Discs("DISC-0002"));      // both: the catalog as it is now
            foreach (string label in new[] { "DISC-0001", "DISC-0002" })
            {
                Assert.Equal(newer.Select(f => f.Item1).Order(), Directory.GetFiles(ProgramIn(label)).Select(Path.GetFileName).Order()!);
                Assert.Equal(newer[0].Item2, File.ReadAllBytes(Path.Combine(ProgramIn(label), DiscProgram.ExeName)));
                Assert.Equal(0, Discloak("verify", label, Disc(label), "--deep"));   // packs untouched, the rest intact
            }
            Assert.Equal(DiscFiles.Readme, File.ReadAllText(readme));
            Assert.Equal(File.ReadAllBytes(KeyFile.PathIn(tmp.File("cat"))), File.ReadAllBytes(key));

            text = Run("stage", "refresh", tmp.File("stg")).Text;            // nothing new but the catalog
            Assert.Contains("_program       already this program", text);
            Assert.Contains("README.txt     unchanged", text);

            Assert.Equal(0, Discloak("burned", "DISC-0001"));
            (code, text) = Run("stage", "refresh", tmp.File("stg"));
            Assert.Equal(0, code);
            Assert.Contains("skipped: DISC-0001 is burned, not staged", text);
            Assert.Contains("Refreshed 1 folder(s), skipped 1", text);
            Assert.Contains("Refreshed 1 folder(s).", Run("stage", "refresh", Disc("DISC-0002")).Text);   // one disc folder
        }
        finally { DiscProgram.UseFilesForTests(TestKeys.ProgramFiles); }
    }

    [Fact]
    public void Stage_refresh_adds_only_when_asked_and_leaves_a_folder_whose_new_files_would_not_fit()
    {
        tmp.RandomFile("src/a.bin", 30_000, seed: 1);
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "1MB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--without-program"));
        string disc = Disc("DISC-0001"), backup = Path.Combine(disc, DiscFiles.CatalogBackupName);

        string text = Run("stage", "refresh", disc).Text;
        Assert.Contains("-  _catalog     none in this folder (--add-catalog adds one)", text);
        Assert.Contains("-  _program     none in this folder (--add-program adds it)", text);
        Assert.False(Directory.Exists(backup) || Directory.Exists(ProgramIn("DISC-0001")));

        Assert.Contains("_program       added (--add-program)", Run("stage", "refresh", disc, "--add-program").Text);
        Assert.True(Directory.Exists(ProgramIn("DISC-0001")));

        // the disc has room for the folder as it is, not for a catalog backup on top
        var cat = TestKeys.Load(tmp.File("cat"));
        cat.Discs.Single().Capacity = DiscSpace.Folder(disc) + 4096;
        cat.Save();
        var (code, output) = Run("stage", "refresh", disc, "--add-catalog");
        Assert.Equal(2, code);
        Assert.Contains("NOT REFRESHED: the folder would take", output);
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(Path.Combine(disc, ".refresh")));

        cat = TestKeys.Load(tmp.File("cat"));
        cat.Discs.Single().Capacity = 1_000_000;
        cat.Save();
        Assert.Contains("_catalog       added (--add-catalog)", Run("stage", "refresh", disc, "--add-catalog").Text);
        Assert.Contains("OK       _catalog (", Run("verify", "DISC-0001", disc).Text);

        // a folder left from before a discard is not the disc's any more
        Assert.Equal(0, Discloak("discard", "DISC-0001"));
        Assert.Contains("skipped: DISC-0001 is not in the catalog", Run("stage", "refresh", tmp.File("stg")).Text);
    }
}
