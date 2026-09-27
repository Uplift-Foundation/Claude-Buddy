using System;
using System.Linq;

namespace ClaudeBuddy
{
    // How big a session orb is drawn, as a multiplier over the 56-DIP window
    // (and the 36-DIP circle inside it) that every orb has always been. Pure and
    // window-free for the same reason ChatZoom and OrbArrangement are: the rules
    // are arithmetic, and the arrangement sweep, the settings slider and the
    // right-click Size menu all have to agree on them without one of them
    // constructing a window to find out.
    //
    // This is the *user's* size, and deliberately a separate number from the
    // three the orb already carries: Layout.Scale (DPI), the 36-in-56 circle,
    // and the 0.72 a team member is drawn at. OrbWindow applies it as a
    // LayoutTransform over the whole orb rather than folding it into any of
    // those, so each keeps meaning exactly what it did before CB-198.
    public static class OrbSizing
    {
        public const double Default = 1.0;

        public const double Max = 2.0;

        public const double Step = 0.05;

        // The window every orb has been, and the centre every anchor in the app
        // used to hard-code as (28,28).
        public const double BaseWindowDip = 56;

        // Windows will not let a top-level window be shorter than
        // SM_CYMINTRACK, and the strip that floor adds below a smaller orb is
        // not click-through — so an orb smaller than the floor would carry a
        // dead band that swallows clicks meant for whatever is behind it.
        //
        // Measured 2026-09-26 on the Windows 11 box (96 DPI, Avalonia 12.1.1,
        // this window's exact flags, real SendInput clicks, the installed
        // 0.5.7-beta's orbs as the control): SM_CYMINTRACK=39, and a window
        // asking for 33.6 DIP (0.6) got a 33x39 client area whose bottom strip
        // took left-click, right-click and hover itself and passed none of them
        // to the window behind. 0.65 (36.4 DIP) still got 36x39 with a dead
        // strip; 0.7 (39.2 DIP) got 39x39 and the orb's own Root took a click at
        // y=37. Hence 0.7 — the smallest step whose window clears the floor.
        // Not measured at other DPIs; SM_CYMINTRACK scales with DPI, so the
        // floor is expected to stay ~39 DIP, but that is an expectation.
        //
        // The width was honoured at every size (33 DIP asked, 33 got), which
        // is not what OrbWindow.axaml's older 120x56 note describes; see there.
        //
        // macOS has no such floor for a borderless window, so 0.6 there —
        // assumed from that, not measured with a click.
        public const double MinWindows = 0.7;

        public const double MinMacOS = 0.6;

        public static double MinFor(bool windows) => windows ? MinWindows : MinMacOS;

        // A property rather than a constant for the same reason as
        // ChatZoom.Accelerator: a test can check both platforms' answers on
        // whichever runner it is on, and separately that this picks the right
        // one.
        public static double Min => MinFor(OperatingSystem.IsWindows());

        // Anything at all — a hand-edited file, a NaN — maps onto a size the
        // orb can be drawn at. Snapped to the 0.05 step as well as bounded, so
        // the stored number stays one a person can read and the slider's ticks
        // and the menu's presets land on the same values.
        public static double Clamp(double size) => Clamp(size, Min);

        public static double Clamp(double size, double min)
        {
            if (double.IsNaN(size) || double.IsInfinity(size)) return Default;

            var snapped = Math.Round(size / Step) * Step;
            return Math.Round(Math.Clamp(snapped, min, Max), 2);
        }

        // An orb's own override if it has one, the global slider if not.
        public static double Effective(double? perOrb, double global) => Clamp(perOrb ?? global);

        // The right-click Size menu's choices. Filtered rather than clamped, so
        // a platform whose floor is above 60% simply does not offer 60%,
        // instead of offering a row that quietly means 70%.
        private static readonly double[] AllPresets = { 0.6, 0.75, 1.0, 1.25, 1.5, 2.0 };

        public static double[] PresetsFor(double min) => AllPresets.Where(p => p >= min - 1e-9).ToArray();

        public static double[] Presets => PresetsFor(Min);

        public static double WindowDip(double size) => BaseWindowDip * size;

        public static double CentreDip(double size) => WindowDip(size) / 2;

        // Distance from an orb's centre to the chat panel's near edge. Was a
        // flat 34 — the 56-DIP orb's half plus a 6-DIP gap — which a 2x orb's
        // 56-DIP half would sit on top of. The gap stays 6; the half grows.
        public const double ChatPanelClearance = 6;

        public static double ChatPanelGap(double size) => CentreDip(size) + ChatPanelClearance;
    }
}
