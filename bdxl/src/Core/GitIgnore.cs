namespace Bdxl;

/// <summary>
/// .gitignore syntax: blank lines and #comments are skipped; a leading ! negates; a trailing / matches directories only;
/// a leading / or a / in the middle anchors the pattern to the source folder, otherwise it matches at any depth;
/// *, ?, [abc] and ** work as in git. Later rules win. A file inside an ignored directory is ignored.
/// Paths are relative to the scanned source folder and use '/'.
/// </summary>
public sealed class GitIgnore
{
    sealed class Rule
    {
        public string Text = "";
        public bool Negate;
        public bool DirOnly;
        public Regex Pattern = null!;
    }

    readonly List<Rule> rules = new();

    public GitIgnore(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            var rule = Parse(line);
            if (rule != null) rules.Add(rule);
        }
    }

    public int RuleCount => rules.Count;

    static Rule? Parse(string raw)
    {
        string line = raw.TrimEnd();
        if (line.Length == 0 || line.StartsWith('#')) return null;

        var rule = new Rule { Text = raw.Trim() };
        if (line.StartsWith('!')) { rule.Negate = true; line = line[1..]; }
        else if (line.StartsWith("\\!") || line.StartsWith("\\#")) line = line[1..];

        if (line.EndsWith('/')) { rule.DirOnly = true; line = line.TrimEnd('/'); }
        if (line.Length == 0) return null;

        bool anchored = line.Contains('/');
        if (line.StartsWith('/')) line = line[1..];

        string body = ToRegex(line);
        string full = anchored ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        rule.Pattern = new Regex(full, options | RegexOptions.CultureInvariant);
        return rule;
    }

    static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    bool atStart = i == 0 || glob[i - 1] == '/';
                    bool followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                    bool atEnd = i + 2 >= glob.Length;
                    if (atStart && followedBySlash) { sb.Append("(?:.*/)?"); i += 2; }      // "**/"  = any number of directories
                    else if (atStart && atEnd) { sb.Append(".*"); i += 1; }                  // "a/**" = everything below
                    else { sb.Append("[^/]*"); i += 1; }                                     // odd use: behaves like one star
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                int close = glob.IndexOf(']', i + 2 <= glob.Length ? i + 1 : i);
                if (close > i + 1)
                {
                    string cls = glob.Substring(i + 1, close - i - 1);
                    if (cls.StartsWith('!')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                    i = close;
                }
                else sb.Append("\\[");
            }
            else if (c == '\\' && i + 1 < glob.Length) { sb.Append(Regex.Escape(glob[i + 1].ToString())); i++; }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>The last rule matching exactly this path (null = none). Negations count as matches.</summary>
    Rule? LastMatch(string path, bool isDirectory)
    {
        Rule? hit = null;
        foreach (var rule in rules)
        {
            if (rule.DirOnly && !isDirectory) continue;
            if (rule.Pattern.IsMatch(path)) hit = rule;
        }
        return hit;
    }

    public bool IsIgnored(string relPath) => Explain(relPath, out _);

    /// <summary>True if the file is ignored; 'rule' is the rule text that decided it (an ancestor directory rule counts).</summary>
    public bool Explain(string relPath, out string? rule)
    {
        rule = null;
        if (rules.Count == 0) return false;
        string[] parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) current.Append('/');
            current.Append(parts[i]);
            bool isDirectory = i < parts.Length - 1;
            var hit = LastMatch(current.ToString(), isDirectory);
            bool ignored = hit != null && !hit.Negate;
            if (ignored) { rule = hit!.Text; return true; }   // an ignored directory (or the file itself)
            if (!isDirectory) { rule = hit?.Text; return false; }
        }
        return false;
    }
}
