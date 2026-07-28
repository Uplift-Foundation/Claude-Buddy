using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeBuddy
{
    // Which Claude Desktop instances are running right now, and which profile
    // directory each one was launched against.
    //
    // The plan for this was a POSIX record-lock query: fcntl(fd, F_GETLK,
    // &flock) against <profile>/Local Storage/leveldb/LOCK, which Chromium
    // holds for the life of a session. That route is closed. fcntl(2) is
    // variadic, and on Apple arm64 the variadic arguments are passed on the
    // *stack* while a plain .NET DllImport puts the third argument in x2 — the
    // callee would read a garbage pointer. .NET has no variadic P/Invoke, so
    // there's no correct way to make that call. (open(2) is variadic too, but
    // open(path, O_RDONLY) passes no variadic argument, so that one is fine —
    // it's specifically the three-argument fcntl that can't be expressed.)
    //
    // libproc and sysctl(3) are both fixed-arity, and they answer the question
    // more directly: the scan filters on the *main* process's executable path,
    // so there's no need to assume which process inside the app happens to hold
    // a given file lock, and the pid we get back is the one to activate or
    // quit. Like the lock probe, it spawns nothing and sees instances launched
    // by anything, including from the Dock.
    //
    // Not `ps eww`: that prints the environment as space-separated KEY=VALUE
    // pairs, and every profile path contains a space ("Application Support"),
    // so the output cannot be tokenised back into paths. KERN_PROCARGS2 is
    // NUL-separated, and the comparison is against the whole value — which also
    // avoids the trap that ".../Claude" is a strict prefix of ".../Claude-3p".
    internal static class MacOSProcessScan
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        private const int CTL_KERN = 1;
        private const int KERN_ARGMAX = 8;
        private const int KERN_PROCARGS2 = 49;

        private const int MaxPathBytes = 4096; // PROC_PIDPATHINFO_MAXSIZE

        // The main process; helpers live in "Claude Helper.app/Contents/MacOS/
        // Claude Helper (Renderer)" and friends, so this suffix excludes them.
        private const string MainExecutableSuffix = "/Claude.app/Contents/MacOS/Claude";

        private const string UserDataDirKey = "CLAUDE_USER_DATA_DIR=";

        [DllImport(LibSystem, SetLastError = true)]
        private static extern int proc_listallpids(int[]? buffer, int buffersize);

        [DllImport(LibSystem, SetLastError = true)]
        private static extern int proc_pidpath(int pid, byte[] buffer, uint buffersize);

        [DllImport(LibSystem, EntryPoint = "sysctl", SetLastError = true)]
        private static extern int sysctl(int[] name, uint namelen, byte[]? oldp, ref nuint oldlenp,
                                         IntPtr newp, nuint newlen);

        // A live process's environment never changes, so the expensive half of
        // the scan (KERN_PROCARGS2 needs a KERN_ARGMAX-sized buffer, typically
        // 1 MB) runs once per instance rather than once per poll. Re-read after
        // a minute anyway, so a recycled pid can't pin a stale answer forever.
        private const long EnvCacheMs = 60_000;

        private static readonly object Gate = new();
        private static readonly Dictionary<int, (string? Dir, long Stamp)> EnvCache = new();
        private static int _argMax;

        public static IReadOnlyList<ClaudeInstance> Scan()
        {
            if (!OperatingSystem.IsMacOS()) return Array.Empty<ClaudeInstance>();

            var results = new List<ClaudeInstance>();

            try
            {
                var pids = AllPids();
                if (pids.Length == 0) return results;

                var pathBuffer = new byte[MaxPathBytes];
                var live = new HashSet<int>();

                foreach (var pid in pids)
                {
                    if (pid <= 0) continue;
                    if (!IsClaudeMainProcess(pid, pathBuffer)) continue;

                    live.Add(pid);
                    results.Add(new ClaudeInstance(pid, UserDataDirOf(pid)));
                }

                lock (Gate)
                {
                    foreach (var stale in EnvCache.Keys.Where(k => !live.Contains(k)).ToList())
                    {
                        EnvCache.Remove(stale);
                    }
                }
            }
            catch
            {
                // A scan that fails reads as "nothing running", which degrades
                // to offering Launch — never to launching a second instance
                // against a live directory, because the launch path re-checks.
                return results;
            }

            return results;
        }

        private static int[] AllPids()
        {
            var count = proc_listallpids(null, 0);
            if (count <= 0) return Array.Empty<int>();

            // Headroom: processes can appear between the sizing call and the
            // real one, and proc_listallpids just truncates.
            var buffer = new int[count + 64];
            var filled = proc_listallpids(buffer, buffer.Length * sizeof(int));
            if (filled <= 0) return Array.Empty<int>();

            if (filled < buffer.Length) Array.Resize(ref buffer, filled);
            return buffer;
        }

        private static bool IsClaudeMainProcess(int pid, byte[] buffer)
        {
            // Fails with EPERM for other users' processes; those are skipped,
            // which is right — a profile belongs to one user's home directory.
            var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
            if (length <= 0) return false;

            var path = Encoding.UTF8.GetString(buffer, 0, length);
            return path.EndsWith(MainExecutableSuffix, StringComparison.Ordinal);
        }

        private static string? UserDataDirOf(int pid)
        {
            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (EnvCache.TryGetValue(pid, out var cached) && now - cached.Stamp < EnvCacheMs)
                {
                    return cached.Dir;
                }
            }

            var dir = ReadUserDataDir(pid);

            lock (Gate)
            {
                EnvCache[pid] = (dir, now);
            }

            return dir;
        }

        private static string? ReadUserDataDir(int pid)
        {
            var size = ArgMax();
            var buffer = new byte[size];
            var length = (nuint)buffer.Length;

            var mib = new[] { CTL_KERN, KERN_PROCARGS2, pid };
            if (sysctl(mib, 3, buffer, ref length, IntPtr.Zero, 0) != 0) return null;

            return ParseUserDataDir(buffer, (int)length);
        }

        // KERN_PROCARGS2 hands back:
        //   [int32 argc][exec path\0][\0 padding][argv 0..argc-1, each \0][env, each \0]
        internal static string? ParseUserDataDir(byte[] buffer, int length)
        {
            if (length < sizeof(int)) return null;

            var argc = BitConverter.ToInt32(buffer, 0);
            if (argc < 0) return null;

            var i = sizeof(int);

            while (i < length && buffer[i] != 0) i++;   // exec path
            while (i < length && buffer[i] == 0) i++;   // its alignment padding

            for (var arg = 0; arg < argc && i < length; arg++)
            {
                while (i < length && buffer[i] != 0) i++;
                i++;
            }

            while (i < length)
            {
                var start = i;
                while (i < length && buffer[i] != 0) i++;
                if (i == start) break; // empty string terminates the block

                var entry = Encoding.UTF8.GetString(buffer, start, i - start);
                if (entry.StartsWith(UserDataDirKey, StringComparison.Ordinal))
                {
                    var value = entry[UserDataDirKey.Length..];
                    return value.Length == 0 ? null : value;
                }

                i++;
            }

            return null;
        }

        private static int ArgMax()
        {
            if (_argMax > 0) return _argMax;

            var fallback = 256 * 1024;
            var buffer = new byte[sizeof(int)];
            var length = (nuint)sizeof(int);
            var mib = new[] { CTL_KERN, KERN_ARGMAX };

            var value = sysctl(mib, 2, buffer, ref length, IntPtr.Zero, 0) == 0
                ? BitConverter.ToInt32(buffer, 0)
                : fallback;

            if (value <= 0 || value > 4 * 1024 * 1024) value = fallback;

            _argMax = value;
            return _argMax;
        }
    }
}
