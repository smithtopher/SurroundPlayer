// SURROUND_DEBUG=<file>: timestamped breadcrumbs (start-up steps, UI vs presenter thread)
// appended to the same file as the status log, for diagnosing hangs without a debugger.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SurroundPlayer;

internal static class DebugTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SURROUND_DEBUG");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Lock = new();

    public static bool Enabled => Path != null;

    public static void Log(string msg)
    {
        if (Path == null) return;
        try
        {
            lock (Lock)
                File.AppendAllText(Path, $"[trace {Clock.ElapsedMilliseconds,6} ms t{Environment.CurrentManagedThreadId}] {msg}\n");
        }
        catch { }
    }
}
