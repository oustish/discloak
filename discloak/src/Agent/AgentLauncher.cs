using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Discloak;

/// <summary>
/// Starts the agent: the program files are copied to %LOCALAPPDATA%\discloak\agent\&lt;build&gt;\ first (so a rebuild or an
/// update never meets a discloak.exe locked by a running agent) and the agent is started from there, detached from the
/// console and, where Windows allows it, outside the terminal's job (so closing the terminal does not end it).
/// A single-file build (publish profile win-x64) is just discloak.exe; a normal build also has its DLLs and runtimes\.
/// </summary>
public static class AgentLauncher
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "discloak", "agent");
    public static string LogPath => Path.Combine(Root, "agent.log");
    const string CompleteMarker = ".complete";

    /// <summary>Starts an agent and waits until it answers. Returns a note for the user ("" if none).</summary>
    public static string Start(AgentClient client)
    {
        string folder = CopyProgram();
        string note = Launch(folder);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (client.Status() is { Ok: true }) { CleanOldCopies(folder); return note; }
            Thread.Sleep(100);
        }
        throw new UserException($"The agent did not start (see {LogPath}).");
    }

    static string CopyProgram()
    {
        string target = Path.Combine(Root, AgentProtocol.Build);
        if (File.Exists(Path.Combine(target, CompleteMarker))) return target;
        if (Directory.Exists(target)) Directory.Delete(target, true);   // an earlier copy was cut off

        string source = AppContext.BaseDirectory;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".dll" or ".exe" or ".json") Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        string runtimes = Path.Combine(source, "runtimes");   // native libsodium
        if (Directory.Exists(runtimes))
            foreach (string file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        File.WriteAllText(Path.Combine(target, CompleteMarker), DateTime.UtcNow.ToString("O"));
        return target;
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>Copies of older builds whose agent has ended can go (a running one keeps its files locked).</summary>
    static void CleanOldCopies(string current)
    {
        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static string Launch(string folder)
    {
        // the apphost (discloak.exe) when there is one; otherwise "dotnet discloak.dll" as this process was started
        string exe = Path.Combine(folder, "discloak.exe");
        string commandLine = File.Exists(exe)
            ? $"\"{exe}\" agent"
            : $"\"{Environment.ProcessPath}\" \"{Path.Combine(folder, "discloak.dll")}\" agent";

        if (!OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(commandLine) { UseShellExecute = false, WorkingDirectory = folder });
            return "";
        }
        if (CreateDetached(commandLine, folder, breakaway: true)) return "";
        if (CreateDetached(commandLine, folder, breakaway: false))
            return "Note: Windows did not let the agent leave this terminal's process group: closing the terminal ends it (and locks).";
        throw new Win32Exception(Marshal.GetLastWin32Error(), "The agent could not be started");
    }

    const uint DETACHED_PROCESS = 0x00000008, CREATE_NEW_PROCESS_GROUP = 0x00000200, CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
               CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    static bool CreateDetached(string commandLine, string folder, bool breakaway)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        uint flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT | (breakaway ? CREATE_BREAKAWAY_FROM_JOB : 0);
        var cmd = new StringBuilder(commandLine);
        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, folder, ref si, out var pi)) return false;
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
