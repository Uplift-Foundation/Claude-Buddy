using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using ArrowPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Platform;

namespace ClaudeBuddy
{
    // Draws the arrow from an agent-team member's orb to its team lead's.
    //
    // A team member is a separate claude process — its own session id, its own
    // status file, its own orb — so a team of four looks exactly like four
    // unrelated sessions that happen to have appeared at the same time. The
    // arrow is the only thing that says otherwise. Which orb points where comes
    // from the member's `lead` field; see SessionStatus.Lead.
    //
    // Each arrow is its own transparent click-through window sized to the
    // bounding box of the two orbs it joins, rather than one screen-sized
    // overlay. A full-screen window would have to be re-rendered whenever any
    // orb moved, would cover other Spaces awkwardly, and on macOS would sit in
    // front of every other app as one large surface — the same reasoning that
    // keeps each orb in its own 56x56 window.
    internal static class TeamLinks
    {
        // Rendered in DIPs, so these are the shape's own dimensions rather than
        // anything screen-dependent. The shaft is tapered — thin where it
        // leaves the member, full width where it meets the head — which reads
        // as direction even before you register the arrowhead.
        // All three live in TeamLinkGeometry, with HeadHalfWidth below. HeadLength
        // in particular used to be declared here *as well*, with the same value —
        // so shortening an arrow to leave room for its head and drawing the head
        // were reading two different constants, and changing one of them would
        // have moved the head off the end of the shaft.
        private const double ShaftAtMember = TeamLinkGeometry.ShaftAtMember;
        private const double ShaftAtHead = TeamLinkGeometry.ShaftAtHead;
        private const double HeadLength = TeamLinkGeometry.HeadLength;

        // Aliased rather than duplicated, the same way the gaps below are: Place
        // sizes the window from this and ArrowGeometry draws the head with it, so
        // two copies would be two chances to disagree about whether the head fits
        // inside its own window.
        private const double HeadHalfWidth = TeamLinkGeometry.HeadHalfWidth;

        // The room an arrow needs lives in TeamLinkGeometry, shared with
        // whatever places the orbs — see the note there about the two drifting
        // apart and every arrow silently vanishing.
        private const double MemberGap = TeamLinkGeometry.MemberGap;
        private const double LeadGap = TeamLinkGeometry.LeadGap;
        private const double MinimumLength = TeamLinkGeometry.MinimumLength;

        // Below full opacity the arrows sit behind the orbs visually without
        // needing to sit behind them in the window stack (they can't — every
        // orb is topmost).
        private const double ArrowOpacity = 0.55;

        private static readonly List<(OrbWindow Member, OrbWindow Lead)> Pairs = new();
        private static readonly List<LinkWindow> Windows = new();

        // Parked and reused, never closed — the same rule as
        // ClaudeDesktopOverlay, and for the same reason: tearing an Avalonia
        // window down while its render target is in flight crashes the app, and
        // a team's arrows appear and disappear constantly as members are
        // spawned and finish.
        private static readonly Stack<LinkWindow> Parked = new();
        private const int MaxParked = 8;

        private static bool _visible = true;

        // Replaces the whole set. Callers rebuild the pair list on every scan
        // rather than diffing it: it's a handful of pairs, and the alternative
        // is tracking membership changes in three places.
        // Excluded from coverage: creates and shows real transparent arrow windows
        // over live orb windows.
        [ExcludeFromCodeCoverage]
        public static void Update(IEnumerable<(OrbWindow Member, OrbWindow Lead)> pairs)
        {
            Pairs.Clear();
            Pairs.AddRange(pairs);
            Refresh();
        }

        // Re-runs the geometry without touching the set — what a drag calls, on
        // every pointer move, so it does no allocation beyond the geometry it
        // has to rebuild.
        // Excluded from coverage: measures live orb windows and repositions real
        // arrow windows; the arithmetic between the two is TeamLinkGeometry.Place,
        // which is tested.
        [ExcludeFromCodeCoverage]
        public static void Refresh()
        {
            if (!_visible)
            {
                HideAll();
                return;
            }

            while (Windows.Count < Pairs.Count)
            {
                Windows.Add(Parked.Count > 0 ? Parked.Pop() : new LinkWindow());
            }

            for (var i = 0; i < Pairs.Count; i++)
            {
                var (member, lead) = Pairs[i];
                Windows[i].Apply(member, lead);
            }

            // Spare windows are parked rather than dropped, so a member that
            // comes and goes reuses the same window each time.
            for (var i = Windows.Count - 1; i >= Pairs.Count; i--)
            {
                Park(Windows[i]);
                Windows.RemoveAt(i);
            }
        }

        // Orbs hidden means arrows hidden: an arrow between two invisible orbs
        // is a line from nowhere to nowhere.
        // Excluded from coverage: shows or hides real arrow windows.
        [ExcludeFromCodeCoverage]
        public static void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;

            if (visible) Refresh();
            else HideAll();
        }

        // Excluded from coverage: hides real arrow windows.
        [ExcludeFromCodeCoverage]
        private static void HideAll()
        {
            foreach (var window in Windows) window.Park();
        }

        // Excluded from coverage: hides a real arrow window and returns it to the
        // pool.
        [ExcludeFromCodeCoverage]
        private static void Park(LinkWindow window)
        {
            window.Park();
            if (Parked.Count < MaxParked) Parked.Push(window);
        }

        // Excluded from coverage, as a class: what is left in here after the
        // outline maths moved to TeamLinkGeometry is nothing but a native window.
        // The constructor builds a transparent, undecorated, non-activating one;
        // Apply() reads PointToScreen off two real orbs and calls Show(); Park()
        // calls Hide(); MakeClickThrough() sends setIgnoresMouseEvents: to an
        // NSWindow through objc_msgSend on macOS and rewrites GWL_EXSTYLE through
        // user32 on Windows. Under the headless platform there is no window for
        // any of it to act on.
        //
        // Apply()'s catch is part of that: PointToScreen throws when an orb has no
        // platform window yet, which is a real race during startup and not
        // something a test can arrange without a window either.
        [ExcludeFromCodeCoverage]
        private sealed class LinkWindow : Window
        {
            private readonly ArrowPath _arrow;
            private readonly SolidColorBrush _brush = new(Colors.White, ArrowOpacity);
            private bool _shown;

            public LinkWindow()
            {
                WindowDecorations = WindowDecorations.None;
                Background = Brushes.Transparent;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                CanResize = false;
                SizeToContent = SizeToContent.Manual;

                _arrow = new ArrowPath
                {
                    Fill = _brush,
                    Stretch = Stretch.None,      // geometry is already in window coordinates
                    IsHitTestVisible = false
                };
                Content = _arrow;

                // Follows its orbs across Spaces, since they do.
                Opened += (_, _) =>
                {
                    this.ShowOnAllSpaces();
                    MakeClickThrough();
                };
            }

            public void Park()
            {
                if (!_shown) return;
                Hide();
                _shown = false;
            }

            public void Apply(OrbWindow member, OrbWindow lead)
            {
                // Nothing to join yet, or nothing to see: an orb with no
                // platform window can't be measured, and a hidden one has no
                // position worth pointing at.
                if (!member.IsVisible || !lead.IsVisible)
                {
                    Park();
                    return;
                }

                // How many units of Window.Position there are to a DIP —
                // measured rather than assumed, because the answer is 1 on
                // macOS (Avalonia hands out screen coordinates in points, which
                // are already the DIPs a 56x56 window is laid out in) and the
                // display scaling on Windows. Getting this from Scaling instead
                // put the arrows half an orb off on a Retina Mac.
                //
                // Both orbs are treated as being at the member's scale: an
                // arrow spanning two monitors of different densities is a few
                // pixels out, which doesn't show on a decoration and is much
                // cheaper than resolving each orb's screen every drag frame.
                double scale;
                Point from, to;
                try
                {
                    var origin = member.PointToScreen(new Point(0, 0));
                    scale = (member.PointToScreen(new Point(64, 0)).X - origin.X) / 64.0;
                    if (scale <= 0) scale = 1;

                    from = Centre(member);
                    to = Centre(lead);
                }
                catch
                {
                    // An orb torn down mid-scan. The next refresh has it.
                    Park();
                    return;
                }

                // Everything from here to the assignments below used to be
                // inline. It is arithmetic on two measured points and two radii,
                // and it is the part that decides whether there is an arrow at
                // all, so it lives in TeamLinkGeometry next to the clearance rule
                // it has to agree with — see the note there about the two
                // drifting apart and every arrow silently vanishing.
                if (TeamLinkGeometry.Place(from, to, member.OrbRadius, lead.OrbRadius, scale)
                    is not { } placement)
                {
                    Park();
                    return;
                }

                Position = placement.Position;
                Width = placement.Width;
                Height = placement.Height;

                _arrow.Data = ArrowGeometry(
                    placement.Start, placement.End, placement.Ux, placement.Uy);

                // The member's colour, not the lead's: several members pointing
                // at one lead stay distinguishable, and an arrow is the member's
                // statement about where it reports.
                if (_brush.Color != member.LinkColor) _brush.Color = member.LinkColor;

                if (_shown) return;
                _shown = true;
                Show();
            }

            // An orb sits at DIP (28,28) in its own window regardless of how
            // big the orb inside is drawn, so this point is the same for a
            // member and a lead. Asked of the window rather than derived from
            // Position, so it is right on both platforms' coordinate systems.
            //
            // (28,28) is the centre of OrbWindow's Root, which is pinned to
            // 56x56 and anchored top-left precisely so this stays true where
            // the OS won't let the window itself be 56x56 — see the comment on
            // Root in OrbWindow.axaml. Arrows were landing off-centre on
            // Windows for exactly that reason before it was pinned.
            private static Point Centre(OrbWindow orb)
            {
                var centre = orb.PointToScreen(new Point(28, 28));
                return new Point(centre.X, centre.Y);
            }

            // Streams TeamLinkGeometry.ArrowOutline into the one Avalonia type
            // that has to be built here. The shape itself is decided there, so
            // that it can be asserted on without a window — this method is only
            // the part that cannot be.
            private static StreamGeometry ArrowGeometry(Point start, Point end, double ux, double uy)
            {
                var outline = TeamLinkGeometry.ArrowOutline(start, end, ux, uy);

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(outline[0], true);
                    for (var i = 1; i < outline.Length; i++) context.LineTo(outline[i]);
                    context.EndFigure(true);
                }

                return geometry;
            }

            // An arrow that ate clicks would be worse than no arrow: it lies
            // across the gap between two orbs, which is exactly where you drag
            // one to. IsHitTestVisible only settles it inside Avalonia — the
            // window itself still takes the click — so this has to be told to
            // the window server on both platforms.
            private void MakeClickThrough()
            {
                var handle = TryGetPlatformHandle();
                if (handle is null) return;

                if (OperatingSystem.IsMacOS())
                {
                    if (handle is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                    {
                        objc_msgSend_bool(mac.NSWindow,
                            sel_registerName("setIgnoresMouseEvents:"), true);
                    }
                    return;
                }

                if (OperatingSystem.IsWindows() && handle.Handle != IntPtr.Zero)
                {
                    var style = GetWindowLongPtrW(handle.Handle, GWL_EXSTYLE);
                    SetWindowLongPtrW(handle.Handle, GWL_EXSTYLE,
                        style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                }
            }

            private const int GWL_EXSTYLE = -20;
            private const long WS_EX_TRANSPARENT = 0x00000020;
            private const long WS_EX_LAYERED = 0x00080000;

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            private static extern long GetWindowLongPtrW(IntPtr hWnd, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
            private static extern long SetWindowLongPtrW(IntPtr hWnd, int index, long value);

            [DllImport("/usr/lib/libobjc.A.dylib")]
            private static extern IntPtr sel_registerName(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
            private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector,
                [MarshalAs(UnmanagedType.U1)] bool value);
        }
    }
}
