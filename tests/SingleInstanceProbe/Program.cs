using System.Runtime.InteropServices;
using Orbweaver;

// One claim of the single-instance mutex from a process of its own (CB-206),
// for tests/IntegrationTests' SingleInstanceTests.
//
//   SingleInstanceProbe <name> [--new-session] [--windows-scope] [--hold]
//
//   --new-session    setsid(2) and re-exec first, so the claim is made from a
//                    POSIX session of its own — what a terminal, an agent shell,
//                    ssh or nohup/setsid launch gives a real Buddy. Ignored on
//                    Windows.
//   --windows-scope  claim with Windows' scope (the bare, session-scoped name)
//                    instead of this platform's, so a test can show the bug
//                    the new scope fixes.
//   --hold           having acquired it, keep it until killed.
//
// Prints one line, `sid=<session id> claim=<SingleInstanceClaim>`, with sid -1
// on Windows, and flushes it before holding so the test can read it.
var name = args[0];
var newSession = args.Contains("--new-session");
var windowsScope = args.Contains("--windows-scope");
var hold = args.Contains("--hold");

// setsid(2) and then exec this same probe again, rather than setsid and carry
// on. The runtime fixes the session a session-scoped mutex name resolves
// against when it starts, before Main runs — so a setsid here alone left the
// old scope's claim still seeing the parent's session, and the paired control
// test caught it: the "blind across sessions" case answered HeldByAnother. A
// runtime started after the setsid is in the new session from its first
// instruction, which is what a real Buddy launched from another session is.
if (newSession && !OperatingSystem.IsWindows())
{
    if (Native.setsid() < 0)
    {
        Console.WriteLine("setsid failed: " + Marshal.GetLastPInvokeError());
        return 3;
    }

    var host = Environment.ProcessPath!;
    var argv = new List<string?> { host, Environment.GetCommandLineArgs()[0] };
    argv.AddRange(args.Where(a => a != "--new-session"));
    argv.Add(null);

    Console.Out.Flush();
    Native.execv(host, argv.ToArray());
    Console.WriteLine("execv failed: " + Marshal.GetLastPInvokeError());
    return 4;
}

var (claim, mutex) = windowsScope
    ? SingleInstance.Claim(name, onWindows: true)
    : SingleInstance.Claim(name);

var sid = OperatingSystem.IsWindows() ? -1 : Native.getsid(0);
Console.WriteLine($"sid={sid} claim={claim}");
Console.Out.Flush();

if (hold && SingleInstance.ShouldProceed(claim)) Thread.Sleep(Timeout.Infinite);

GC.KeepAlive(mutex);
return 0;

static class Native
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    internal static extern int getsid(int pid);

    [DllImport("libc", SetLastError = true)]
    internal static extern int execv(string path, string?[] argv);
}
