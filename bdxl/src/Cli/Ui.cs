namespace Bdxl;

/// <summary>Interactive helpers: confirmation and password prompts.</summary>
public static class Ui
{
    public static bool Confirm(Options opt, string question)
    {
        if (opt.Has("yes")) return true;
        if (Console.IsInputRedirected) throw new UserException("Confirmation needed but input is redirected; add --yes.");
        Console.Error.Write(question + " [y/N] ");
        string? answer = Console.ReadLine();
        return answer != null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Typed without echo. Throws if input is redirected.</summary>
    public static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) throw new UserException("A secret has to be typed, but input is redirected.");
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    /// <summary>The password from the environment variable named by --password-env (default BDXL_PASSWORD), or null.</summary>
    public static string? PasswordFromEnvironment(Options opt, out string envName, string option = "password-env", string defaultVar = "BDXL_PASSWORD")
    {
        envName = opt.GetOr(option, defaultVar);
        string? value = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// A new password: from the environment variable named by --&lt;option&gt; (default defaultVar), or typed twice.
    /// The password rules are enforced either way.
    /// </summary>
    public static string NewPassword(Options opt, string option, string defaultVar = "BDXL_PASSWORD")
    {
        string? password = PasswordFromEnvironment(opt, out string envName, option, defaultVar);
        if (password == null)
        {
            if (Console.IsInputRedirected)
                throw new UserException($"A new password is needed: set the environment variable {envName}, or run this in an interactive terminal.");
            password = ReadSecret("New password: ");
            if (!PasswordPolicy.Check(password, out string why1)) throw new UserException(why1);
            if (ReadSecret("Repeat the password: ") != password) throw new UserException("The two passwords differ.");
            Console.Error.WriteLine("Without this password (or a recovery key) nothing can be decrypted - store it safely.");
            return password;
        }
        if (!PasswordPolicy.Check(password, out string why2)) throw new UserException($"The password in {envName}: {why2}");
        return password;
    }

    /// <summary>
    /// Password from the environment variable named by --password-env (default BDXL_PASSWORD) if it is set,
    /// otherwise typed in (hidden). For a NEW encryption the password rules are enforced and it is typed twice.
    /// </summary>
    public static string GetPassword(Options opt, bool forNewEncryption)
    {
        string envName = opt.GetOr("password-env", "BDXL_PASSWORD");
        string? password = Environment.GetEnvironmentVariable(envName);

        if (string.IsNullOrEmpty(password))
        {
            if (Console.IsInputRedirected)
                throw new UserException($"A password is needed: set the environment variable {envName}, or run this in an interactive terminal.");
            password = ReadSecret(forNewEncryption ? "New encryption password: " : "Password: ");
            if (forNewEncryption)
            {
                if (!PasswordPolicy.Check(password, out string why1)) throw new UserException(why1);
                if (ReadSecret("Repeat the password: ") != password) throw new UserException("The two passwords differ.");
                Console.Error.WriteLine("There is no way to recover files if this password is lost - store it safely.");
            }
            return password;
        }

        if (forNewEncryption && !PasswordPolicy.Check(password, out string why2)) throw new UserException(why2);
        return password;
    }
}
