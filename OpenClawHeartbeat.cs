namespace ClaudeBuddy
{
    // Which gateway sessions the heartbeat drives.
    //
    // OpenClaw's heartbeat is a periodic turn the gateway sends an agent so it
    // can do background work — check a queue, sweep for stalled sessions, notice
    // something happened while nobody was talking to it. The gateway keeps one
    // system-owned automation job per heartbeat-enabled agent, and by default it
    // delivers into **the agent's own main session**: `agent:<id>:main`. See
    // docs/openclaw-findings.md for what a real gateway was measured to say.
    //
    // Worth marking because of what it does to a screen full of orbs. Eight
    // agents with heartbeats enabled are eight main sessions that go active
    // together every half hour, and nothing about them says the activity was a
    // timer rather than a person — an orb that just lit up reads as somebody
    // waiting for you. It is also the answer to why those orbs never go quiet.
    //
    // Detection is deliberately *structural and narrow*, for a reason worth
    // stating plainly: **the gateway does not report heartbeats.** `sessions.list`
    // was read off a live gateway (84 sessions, 8 agents) and carries no field
    // that names one — not `kind` (only "direct"/"group"), not `systemSent`
    // (true on ordinary Discord chats too, and absent on five of the eight main
    // sessions), not `label` (set only on cron sessions). The heartbeat's own
    // prompts are hidden from `sessions.history` the same way the gateway's own
    // Control UI hides them, so they cannot be recognised by their text either:
    // the reply to one arrives with no visible message before it.
    //
    // So this answers "where does the heartbeat land", which is documented and
    // stable, rather than "was this particular turn a heartbeat", which the
    // gateway does not say. Two consequences, both deliberate:
    //
    //   - A main session is marked whether or not that agent actually has a
    //     heartbeat enabled. Enabling it is per-agent config, and config is
    //     behind `operator.admin`, a scope this app does not ask for and should
    //     not start asking for to draw a badge.
    //   - A heartbeat retargeted at a channel or DM with the job's `session`
    //     override is *not* marked. It looks exactly like a channel session,
    //     because that is what it is.
    //
    // Both are visible-and-wrong-in-a-safe-direction rather than silent: the
    // first over-marks sessions the heartbeat *would* use, the second under-marks
    // an unusual setup. Neither says anything false about who can read a
    // conversation, which is the mistake OpenClawSessionKind exists to avoid.
    //
    // Pure and string-valued, like OpenClawSessionKind next to it, so the rule
    // can be tested without a gateway — and so that if a later gateway does
    // start reporting heartbeats, exactly one function changes.
    public static class OpenClawHeartbeat
    {
        // key is "agent:<name>:<surface>[:<type>:<id>]"; label is the session's
        // own label, which the gateway sets on scheduled sessions ("Cron: …")
        // and leaves empty on everything else.
        public static bool Is(string? key, string? label = null)
        {
            var parts = (key ?? "").Split(':');

            if (parts.Length >= 3 && parts[0] == "agent")
            {
                // The documented default target, and the case that matters:
                // `agent:main:main`, `agent:alexis:main`.
                if (Same(parts[2], "main")) return true;

                // Not observed on the gateway this was written against, and
                // matched anyway: a surface segment that says the word costs one
                // comparison, and a gateway that ever keys a heartbeat this way
                // would otherwise draw an ordinary orb with no hint of what
                // keeps waking it. The narrow risk of a false positive here is
                // a session someone deliberately named "heartbeat", which is
                // the thing being looked for.
                if (Same(parts[2], "heartbeat")) return true;
            }

            // The gateway's own name for the job — `openclaw cron list --all`
            // shows it as "Heartbeat (<agent-id>)". That job is system-owned and
            // was *not* among the three returned by cron.list at this app's
            // scopes, and no session carried the label, so this is untested
            // against a real payload. It is here because a session labelled
            // "Heartbeat (main)" arriving one gateway version later should draw
            // the heart rather than nothing, and matching a label the gateway
            // documents costs nothing until then.
            //
            // Prefix rather than substring: "Cron: heartbeat-followup" is a job
            // somebody wrote that mentions the word, not the heartbeat itself.
            var name = (label ?? "").Trim();
            if (name.StartsWith("Cron: ", StringComparison.OrdinalIgnoreCase))
                name = name["Cron: ".Length..].TrimStart();

            return name.StartsWith("heartbeat", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // How hard the heart is beating at a given point in its cycle: 0 at rest,
        // 1 at the top of a contraction, for a phase running 0 to 1.
        //
        // Two half-sine arches — the second smaller and just after the first —
        // and then flat for the remaining half of the cycle. That rest is the
        // whole point: the orb is already breathing on a cosine, and a second
        // smooth swell beside it reads as one thing wobbling. Lub-dub is the one
        // rhythm nobody has to be told the meaning of.
        //
        // Here rather than in the window that draws it because two things draw
        // it — the orb badge and the chat panel's chip — and a heart that beats
        // at two different rhythms depending on where you look at it is worse
        // than either rhythm. Pure, so the shape can be asserted without a
        // screen.
        public static double Beat(double phase)
        {
            // Wrapped rather than clamped, so a caller can hand it elapsed
            // time over a period without doing the modulo itself.
            phase -= Math.Floor(phase);

            static double Arch(double t) => t is < 0 or > 1 ? 0 : Math.Sin(t * Math.PI);

            return Math.Max(Arch(phase / 0.22), 0.62 * Arch((phase - 0.26) / 0.22));
        }

        // One beat a second, near enough — a resting pulse.
        public const double PeriodMs = 1100;
    }
}
