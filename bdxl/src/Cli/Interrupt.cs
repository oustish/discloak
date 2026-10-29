namespace Bdxl;

/// <summary>
/// Ctrl+C. A command that can stop cleanly (ICommand.Interruptible) only gets Requested set on the first press: it
/// finishes the current step, saves and reports what was left undone. Any other command, or a second press, stops
/// the process at once with a note on what that means - but never in the middle of a catalog save (Critical): the
/// stop then waits until the save is complete.
/// </summary>
public static class Interrupt
{
    public const int ExitCode = 130;   // the usual code for "stopped by Ctrl+C"

    static readonly object gate = new();
    static bool installed, cooperative, exitPending;
    static int critical;
    static string command = "";
    static volatile bool requested;
    static CancellationTokenSource source = new();

    /// <summary>Set once Ctrl+C was pressed; interruptible commands check it at safe points.</summary>
    public static bool Requested => requested;

    /// <summary>Cancelled together with Requested: long copies, hashes and decryptions stop mid-file (OperationCanceledException).</summary>
    public static CancellationToken Token { get { lock (gate) return source.Token; } }

    /// <summary>Asks an interruptible command to stop, as the first Ctrl+C does.</summary>
    public static void Request()
    {
        lock (gate)
        {
            requested = true;
            source.Cancel();
        }
    }

    public static void Begin(ICommand cmd)
    {
        lock (gate)
        {
            command = cmd.Name;
            cooperative = cmd.Interruptible;
            if (installed) return;
            Console.CancelKeyPress += OnCancel;
            installed = true;
        }
    }

    /// <summary>Back to the idle state after a command (several commands can run in one process, e.g. in tests).</summary>
    public static void End()
    {
        lock (gate)
        {
            requested = false;
            cooperative = false;
            command = "";
            if (source.IsCancellationRequested) source = new CancellationTokenSource();
        }
    }

    /// <summary>A stretch of work that must not be cut in half (writing the catalog files).</summary>
    public static IDisposable Critical()
    {
        lock (gate) critical++;
        return new CriticalSection();
    }

    sealed class CriticalSection : IDisposable
    {
        bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            bool exitNow;
            lock (gate) exitNow = --critical == 0 && exitPending;
            if (exitNow) Stop();
        }
    }

    static void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;   // the process is ended here, on purpose, once it is safe
        lock (gate)
        {
            if (command.Length == 0)   // between commands: nothing is being written
            {
                Console.Error.WriteLine("\nInterrupted.");
                Environment.Exit(ExitCode);
                return;
            }
            if (cooperative && !requested)
            {
                requested = true;
                source.Cancel();
                Console.Error.WriteLine($"\nInterrupting '{command}': finishing the current step and saving. Press Ctrl+C again to stop at once.");
                return;
            }
            if (critical > 0)
            {
                exitPending = true;
                Console.Error.WriteLine("\nStopping as soon as the catalog is saved...");
                return;
            }
        }
        Stop();
    }

    static void Stop()
    {
        Console.Error.WriteLine($"\nINTERRUPTED: '{command}' was stopped before it finished. The catalog is as it was after its last " +
                                "save (complete - a save is never cut off); work done since then is lost, and files it was " +
                                "writing (a staged folder, restored files) may be incomplete. 'catalog history' lists the saved versions.");
        Environment.Exit(ExitCode);
    }
}
