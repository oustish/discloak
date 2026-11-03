namespace Discloak;

/// <summary>
/// ignore [list] | add &lt;pattern&gt;... | remove &lt;pattern&gt;... | test &lt;path&gt; | apply
/// Rules use .gitignore syntax, are stored in settings.json, and are matched against paths relative to each source folder.
/// </summary>
public sealed class IgnoreCommand : ICommand
{
    public string Name => "ignore";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Define ignored files and folders (.gitignore syntax)";
    public string Usage => "ignore [list] | add <pattern>... | remove <pattern|number>... | test <path> | apply";
    public bool ReadOnlyFor(Options opt) => SubCommand.Is(opt, "list", "list", "test");
    public string Details => """
        Rules live in settings.json inside the catalog and are matched against paths relative to each scanned folder.
          ignore add "node_modules/" "*.tmp" "/cache/**" "!keep.tmp"
          ignore test photos/2020/Thumbs.db
        Ignored files stay in the catalog (marked 'ignored') but are not hashed and never staged. When a rule starts
        matching files that are already catalogued:
          - not on any disc: they are marked ignored and will not be staged;
          - already burned: no new backup is made, but 'restage' of the old disc still includes them.
        Changing rules re-evaluates the catalog immediately ('ignore apply' repeats that).
        """;

    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "list";
        var rules = c.Settings.IgnoreRules;

        switch (sub)
        {
            case "list":
                if (rules.Count == 0) Console.WriteLine("No ignore rules. Add one:  ignore add \"node_modules/\"   (.gitignore syntax)");
                for (int i = 0; i < rules.Count; i++) Console.WriteLine($"{i + 1,3}  {rules[i]}");
                int ignored = c.Cat.Files.Count(f => f.Ignored);
                if (ignored > 0 || rules.Count > 0) Console.WriteLine($"{ignored:N0} catalogued file(s) are currently ignored.");
                return 0;

            case "add":
                if (c.Args.Count < 2) throw new UserException("Usage: ignore add <pattern> [<pattern> ...]");
                foreach (string pattern in c.Args.Skip(1))
                {
                    if (rules.Contains(pattern)) { Console.WriteLine($"Already there: {pattern}"); continue; }
                    rules.Add(pattern);
                }
                return Apply(c, save: true);

            case "remove":
                if (c.Args.Count < 2) throw new UserException("Usage: ignore remove <pattern> [<pattern> ...]  (or the rule number from 'ignore list')");
                int notFound = 0;
                foreach (string pattern in c.Args.Skip(1))
                {
                    bool removed = rules.Remove(pattern);
                    if (!removed && int.TryParse(pattern, out int number) && number >= 1 && number <= rules.Count)
                    {
                        rules.RemoveAt(number - 1);
                        removed = true;
                    }
                    if (!removed) { Console.WriteLine($"No such rule: {pattern}"); notFound++; }
                }
                if (notFound == c.Args.Count - 1) return 1;   // nothing changed: no need to touch the catalog
                Apply(c, save: true);
                return notFound > 0 ? 1 : 0;

            case "apply":
                return Apply(c, save: true);

            case "test":
                if (c.Args.Count != 2) throw new UserException("Usage: ignore test <path relative to a source folder>");
                bool hit = c.Arc.BuildIgnore().Explain(c.Args[1].Replace('\\', '/'), out string? rule);
                Console.WriteLine(hit ? $"IGNORED by rule: {rule}" : rule != null ? $"not ignored (re-included by: {rule})" : "not ignored");
                return 0;

            default:
                throw new UserException("Usage: ignore [list] | add <pattern>... | remove <pattern|number>... | test <path> | apply");
        }
    }

    /// <summary>Re-evaluates the whole catalog against the rules and reports what changes.</summary>
    static int Apply(Ctx c, bool save)
    {
        var before = c.Cat.Files.Where(f => !f.Ignored).ToHashSet();
        var (nowIgnored, noLonger) = c.Arc.ApplyIgnoreRules();
        Console.WriteLine($"{c.Settings.IgnoreRules.Count} rule(s). {nowIgnored:N0} file(s) became ignored, {noLonger:N0} are no longer ignored.");

        if (nowIgnored > 0)
        {
            var dead = c.Arc.DeadDiscs();
            var newlyIgnored = c.Cat.Files.Where(f => f.Ignored && before.Contains(f)).ToList();
            int burned = newlyIgnored.Count(f => f.Backups.Any(b => !dead.Contains(b.Disc)));
            Console.WriteLine($"  {newlyIgnored.Count - burned:N0} of them were not on any disc: they will not be staged.");
            Console.WriteLine($"  {burned:N0} of them are already on a disc: no new backup will be made, but 'restage' still includes them.");
        }
        int toHash = c.Cat.Files.Count(f => !f.Ignored && f.Sha256.Length == 0 && f.MissingSinceUtc == null);
        if (noLonger > 0 && toHash > 0)
            Console.WriteLine($"  {toHash:N0} file(s) have never been hashed; run 'scan' to hash them.");

        if (save) c.Cat.Save();
        return 0;
    }
}
