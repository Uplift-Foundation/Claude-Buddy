namespace ClaudeBuddy
{
    // The CLI processes Claude Buddy starts for its own purposes, so that the
    // scan can tell them apart from the ones the user started.
    //
    // They are indistinguishable at the status file. A `claude -p` spawned to
    // write a spoken summary runs the user's hooks like any other session, so
    // ClaudeBuddyHook.sh writes it a status file and an orb appears for a
    // conversation nobody had — one that lives a few seconds, cannot be clicked
    // anywhere useful, and shifts the arrangement of the real orbs around it on
    // the way in and out. SpeechSummary's own header calls the summariser "a
    // different process, a different conversation... ambient"; an orb is the
    // least ambient thing this app draws.
    //
    // **Keyed on the pid this app actually started, and deliberately not on
    // anything about the invocation.** The tempting rules are all wrong in the
    // same direction: `-p` is a mode the user is entitled to run themselves, a
    // short lifetime describes plenty of real sessions, and the working
    // directory is shared with whatever else runs from a temp path. Each of
    // those would eventually hide a session somebody wanted to see, and would do
    // it silently. The app knows exactly which processes are its own because it
    // started them, so that is the fact to record rather than a heuristic that
    // approximates it.
    //
    // This is the same argument the relay's own drop makes one line below it in
    // SessionManager — see MachineNames.LooksLikeALeftoverRelay — reached from
    // the other side: the relay can be recognised by its cwd because this app
    // chose that cwd, and these can be recognised by their pid because this app
    // owns that pid.
    //
    // Membership is released when the process ends. A pid is reused by the OS
    // eventually, and a set that only ever grew would start hiding a stranger's
    // session months into an uptime — the window here is the few seconds the
    // child is alive, during which its pid cannot belong to anything else.
    internal static class InternalSessions
    {
        private static readonly HashSet<int> Pids = new();
        private static readonly object Gate = new();

        // A pid of 0 or less is not a process; ignored rather than stored, so a
        // caller that failed to start something cannot poison the set with a
        // sentinel that later matches a real session's missing pid.
        internal static void Remember(int pid)
        {
            if (pid <= 0) return;
            lock (Gate) Pids.Add(pid);
        }

        internal static void Forget(int pid)
        {
            if (pid <= 0) return;
            lock (Gate) Pids.Remove(pid);
        }

        internal static bool IsInternal(int pid)
        {
            if (pid <= 0) return false;
            lock (Gate) return Pids.Contains(pid);
        }

        // For tests, which share a process with each other and would otherwise
        // inherit whatever a previous case remembered.
        internal static void Clear()
        {
            lock (Gate) Pids.Clear();
        }
    }
}
