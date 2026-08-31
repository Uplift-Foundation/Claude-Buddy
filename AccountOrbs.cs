using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The account orbs, their cards, and the poll that feeds them.
    //
    // A collection of its own rather than entries in SessionManager's _windows.
    // The app already proves synthetic orbs work — the room orb is built from a
    // hand-made SessionStatus under a namespaced id — but an account orb put in
    // _windows would land in DisplayOrder, in the tray's list of sessions (whose
    // _statuses lookup is unguarded), in ReflowPositions, in the clustered
    // arrangement and in the dead-file sweep's path building. That is five
    // places that would have to learn what a non-session is, to save wiring four
    // things here.
    //
    // Deliberately outside OrbArrangement. An account is not a session, has no
    // team and no lead, and joining the arrangement would mean a new OrbCluster
    // case plus a matching entry in SessionManager.Shapes() — which that method
    // says in as many words must be edited together — and a re-run of the
    // twenty-thousand-case geometry sweep, for orbs that want to sit still.
    internal sealed class AccountOrbs
    {
        // The two halves of the hover bridge, copied from OrbWindow because the
        // problem is identical: the orb and its card are separate OS windows, so
        // a bare PointerExited on either hides the card the instant the pointer
        // crosses between them — before it has arrived anywhere.
        //
        // The open delay is the flyout's, for the flyout's reason: a card that
        // appears the moment a pointer grazes an orb makes the orb hostile to
        // whatever is behind it.
        private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(200);

        // Where the orbs sit before anyone moves them: a row along the bottom
        // left of the work area. Chosen because the session orbs stack down the
        // right-hand edge, so this is the one part of the screen the app is not
        // already using.
        private const int EdgeMargin = 16;
        private const int OrbPitch = 80;

        private readonly IUsageSource _source;
        private readonly Dictionary<string, AccountOrbWindow> _orbs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UsageCard> _cards = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AccountUsage> _readings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _emails = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);

        private DispatcherTimer? _showTimer;
        private DispatcherTimer? _hideTimer;
        private AccountOrbWindow? _hovered;
        private AccountOrbWindow? _pendingShow;

        private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
        private bool _polling;
        private bool _visible = true;

        internal AccountOrbs(IUsageSource source) => _source = source;

        // The position-store key for an account.
        //
        // Namespaced the way the gateway and room ids are, so an account can
        // never collide with a session's key — those are cwd paths, and a config
        // directory is a path too.
        internal static string PositionKey(string? configDir) =>
            "account:" + (configDir ?? "~");

        internal IReadOnlyDictionary<string, AccountOrbWindow> Orbs => _orbs;

        internal IReadOnlyDictionary<string, UsageCard> Cards => _cards;

        // Called on SessionManager's existing two-second tick.
        //
        // The floor is the point: Claude Code caches the underlying usage fetch
        // with a five-minute write guard, so polling faster cannot produce a
        // newer number — it only spends a process launch per account to be told
        // the same thing.
        internal void Tick(DateTimeOffset now)
        {
            if (!ClaudeBuddySettings.AccountUsageEnabled)
            {
                if (_orbs.Count > 0) CloseAll();
                return;
            }

            if (_polling || now - _lastPoll < UsagePoller.MinimumInterval) return;

            _lastPoll = now;
            _polling = true;

            // Off the UI thread, because this starts a process per account and
            // waits seconds for each. On the dispatcher it would freeze every orb
            // on the screen for as long as the slowest account took to answer.
            Task.Run(() =>
            {
                IReadOnlyList<AccountUsage> readings;
                try { readings = _source.Read(); }
                catch { readings = Array.Empty<AccountUsage>(); }

                Dispatcher.UIThread.Post(() =>
                {
                    _polling = false;
                    Apply(readings, DateTimeOffset.UtcNow);
                });
            });
        }

        internal void Apply(IReadOnlyList<AccountUsage> readings, DateTimeOffset now)
        {
            foreach (var reading in readings)
            {
                var key = reading.ConfigDir ?? string.Empty;
                _readings[key] = reading;
            }

            // Accounts that answered nothing keep the orb they had. A poll that
            // failed is not news about usage, and removing the orb would make a
            // network blink look like an account being deleted.
            Redraw(now);
        }

        internal void Redraw(DateTimeOffset now)
        {
            var index = 0;

            foreach (var (key, reading) in _readings.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!_orbs.TryGetValue(key, out var orb))
                {
                    orb = Create(key);
                    _orbs[key] = orb;
                    Place(orb, key, index);
                    if (_visible) orb.Show();
                }

                orb.UpdateFrom(reading, now);
                orb.SetPinned(_pinned.Contains(key));

                if (_cards.TryGetValue(key, out var card))
                {
                    card.UpdateFrom(reading, _emails.GetValueOrDefault(key), now);
                    card.Reposition(orb);
                }

                index++;
            }
        }

        internal void SetEmail(string key, string? email) => _emails[key] = email;

        private AccountOrbWindow Create(string key)
        {
            var orb = new AccountOrbWindow(key);

            orb.HoverStarted += OnHoverStarted;
            orb.HoverEnded += OnHoverEnded;
            orb.Clicked += OnClicked;
            orb.Moved += OnMoved;

            return orb;
        }

        // Where a new orb goes: wherever it was last left, or the next slot in
        // the row if it has never been moved.
        private static void Place(AccountOrbWindow orb, string key, int index)
        {
            var saved = ClaudeBuddySettings.OrbPositionFor(PositionKey(key == string.Empty ? null : key));
            if (saved is not null)
            {
                orb.Position = new PixelPoint(saved.X, saved.Y);
                return;
            }

            var screen = orb.Screens.Primary;
            if (screen is null) return;

            var work = screen.WorkingArea;
            var scale = screen.Scaling;
            var size = (int)Math.Round(72 * scale);
            var margin = (int)Math.Round(EdgeMargin * scale);
            var pitch = (int)Math.Round(OrbPitch * scale);

            orb.Position = new PixelPoint(
                work.X + margin + index * pitch,
                work.Y + work.Height - margin - size);
        }

        private void OnMoved(AccountOrbWindow orb)
        {
            var configDir = orb.AccountKey == string.Empty ? null : orb.AccountKey;
            ClaudeBuddySettings.SetOrbPosition(
                PositionKey(configDir), orb.Position.X, orb.Position.Y);

            if (_cards.TryGetValue(orb.AccountKey, out var card)) card.Reposition(orb);
        }

        // ---- The hover bridge -----------------------------------------------

        private void OnHoverStarted(AccountOrbWindow orb)
        {
            CancelHide();
            ScheduleShow(orb);
        }

        private void OnHoverEnded(AccountOrbWindow orb)
        {
            CancelShow();
            ScheduleHide();
        }

        private void ScheduleShow(AccountOrbWindow orb)
        {
            _pendingShow = orb;

            // Already up: no delay to serve. Re-entering an orb whose card is
            // open should not make the card blink.
            if (_cards.ContainsKey(orb.AccountKey))
            {
                _hovered = orb;
                return;
            }

            _showTimer ??= new DispatcherTimer { Interval = ShowDelay };
            _showTimer.Stop();
            _showTimer.Tick -= OnShowTick;
            _showTimer.Tick += OnShowTick;
            _showTimer.Start();
        }

        private void CancelShow()
        {
            _showTimer?.Stop();
            _pendingShow = null;
        }

        private void OnShowTick(object? sender, EventArgs e)
        {
            _showTimer?.Stop();

            var orb = _pendingShow;
            _pendingShow = null;

            // Re-confirm rather than trust the schedule: the pointer may have
            // left during the wait, and showing a card for an orb nobody is
            // pointing at is how a hover surface becomes a popup.
            if (orb is null || !orb.Root.IsPointerOver) return;

            _hovered = orb;
            OpenCard(orb);
        }

        private void ScheduleHide()
        {
            _hideTimer ??= new DispatcherTimer { Interval = HideDelay };
            _hideTimer.Stop();
            _hideTimer.Tick -= OnHideTick;
            _hideTimer.Tick += OnHideTick;
            _hideTimer.Start();
        }

        private void CancelHide() => _hideTimer?.Stop();

        // The confirmation that makes the bridge a bridge.
        //
        // Without asking both windows whether the pointer landed on them, the
        // card closes in the gap between the orb and itself — which is the whole
        // journey the pointer has to make to reach it.
        private void OnHideTick(object? sender, EventArgs e)
        {
            _hideTimer?.Stop();

            foreach (var key in _cards.Keys.ToList())
            {
                if (_pinned.Contains(key)) continue;

                var overOrb = _orbs.TryGetValue(key, out var orb) && orb.Root.IsPointerOver;
                var overCard = _cards[key].IsPointerOverCard;

                if (overOrb || overCard) continue;

                CloseCard(key);
            }

            _hovered = null;
        }

        // ---- Cards ------------------------------------------------------------

        private void OpenCard(AccountOrbWindow orb)
        {
            var key = orb.AccountKey;

            if (!_cards.TryGetValue(key, out var card))
            {
                card = new UsageCard();
                card.PointerEnteredCard += _ => CancelHide();
                card.PointerExitedCard += _ => ScheduleHide();
                card.PinToggled += _ => TogglePin(key);
                _cards[key] = card;
            }

            if (_readings.TryGetValue(key, out var reading))
            {
                card.UpdateFrom(reading, _emails.GetValueOrDefault(key), DateTimeOffset.UtcNow);
            }

            card.SetPinned(_pinned.Contains(key));
            card.ShowNear(orb);
        }

        private void CloseCard(string key)
        {
            if (!_cards.Remove(key, out var card)) return;

            card.Hide();
            card.Close();
        }

        internal void TogglePin(string key)
        {
            if (_pinned.Contains(key))
            {
                _pinned.Remove(key);

                // Unpinning while the pointer is elsewhere should put the card
                // away immediately; unpinning while still hovering should not,
                // or the card vanishes under the cursor that is still on it.
                var stillThere = _orbs.TryGetValue(key, out var orb) && orb.Root.IsPointerOver;
                var overCard = _cards.TryGetValue(key, out var card) && card.IsPointerOverCard;

                if (!stillThere && !overCard) CloseCard(key);
                else card?.SetPinned(false);
            }
            else
            {
                _pinned.Add(key);
                if (_cards.TryGetValue(key, out var card)) card.SetPinned(true);
            }

            if (_orbs.TryGetValue(key, out var pinnedOrb))
            {
                pinnedOrb.SetPinned(_pinned.Contains(key));
            }
        }

        private void OnClicked(AccountOrbWindow orb) => TogglePin(orb.AccountKey);

        // ---- Visibility and teardown -----------------------------------------

        internal void SetVisible(bool visible)
        {
            _visible = visible;

            foreach (var orb in _orbs.Values)
            {
                if (visible) orb.Show();
                else orb.Hide();
            }

            if (visible) return;

            foreach (var key in _cards.Keys.ToList()) CloseCard(key);
        }

        internal void CloseAll()
        {
            foreach (var key in _cards.Keys.ToList()) CloseCard(key);

            foreach (var orb in _orbs.Values)
            {
                orb.Hide();
                orb.Close();
            }

            _orbs.Clear();
            _pinned.Clear();
            _readings.Clear();
        }
    }
}
