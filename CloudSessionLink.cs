using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Opening a cloud session where it actually lives: a browser tab.
    //
    // A cloud session has no process, no terminal and no transcript on this
    // disk, so "take me to this session" has exactly one honest answer — the
    // address claude.ai serves it at. Every other orb in this app answers that
    // gesture by focusing something local, which is why this is a separate file
    // rather than another arm inside TerminalFocuser: nothing here reads a
    // status, hunts a pane or touches a window.
    //
    // The split below is the whole point of the type. Choosing *how* to ask the
    // OS to open a URL is a decision that differs per platform and is worth
    // asserting; actually starting the process is not testable anywhere and
    // would open a real browser on whatever machine ran the suite. So the
    // choice is a pure function and only the launch carries the exclusion.
    internal static class CloudSessionLink
    {
        // What to hand Process.Start for this URL on this platform.
        //
        // Null for anything that is neither macOS nor Windows, which is not
        // laziness: this app ships for two platforms, and a Linux arm invented
        // here would be a guess nobody has run. A caller that gets null opens
        // nothing, which is the correct behaviour for "we do not know how".
        //
        // macOS goes through `open` with the URL as an *argument*, never as the
        // FileName, so a string that is not really a URL cannot be interpreted
        // as an executable to run. Windows must do the opposite and set
        // UseShellExecute, for the reason SettingsWindow's own ms-settings:
        // launch states: a URL is a thing for the shell to resolve, and
        // CreateProcess cannot start one. Getting that pair backwards fails
        // silently on both platforms rather than loudly on either.
        internal static ProcessStartInfo? StartInfoFor(
            string? url, bool isMacOs, bool isWindows)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            if (isMacOs)
            {
                return new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { url },
                    UseShellExecute = false
                };
            }

            if (isWindows)
            {
                return new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
            }

            return null;
        }

        // The current machine's answer, so callers do not each ask the OS.
        internal static ProcessStartInfo? StartInfoFor(string? url) =>
            StartInfoFor(url, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

        // True when something was actually launched, so a caller that has a
        // fallback knows whether to fall back.
        //
        // Excluded from coverage: the only thing left here is Process.Start,
        // which opens a real browser window on whichever machine runs it.
        // Everything it is handed is decided by StartInfoFor above, which is
        // pure and covered on both platforms' shapes.
        [ExcludeFromCodeCoverage]
        internal static bool Open(string? url)
        {
            var info = StartInfoFor(url);
            if (info is null) return false;

            try
            {
                Process.Start(info)?.Dispose();
                return true;
            }
            catch
            {
                // A browser that refuses to start is not worth taking the app
                // down for, and there is nothing useful to say about it at an
                // orb — the user clicked, and nothing happened, which is the
                // same outcome a failed terminal focus already has.
                return false;
            }
        }
    }
}
