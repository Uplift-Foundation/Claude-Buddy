using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The app's first real window. Everything else is a 56x56 orb or a native
    // menu, and a native menu can't take text input — which is the only reason
    // this exists: naming a profile needs a text field.
    //
    // Built in code rather than XAML because the contents are one row per
    // discovered profile, so there is no static tree to describe.
    //
    // Changes apply immediately. There is no OK/Cancel: this is a preferences
    // window for a menu-bar app, and a settings file that only commits on a
    // button is one more state to get wrong.
    internal sealed class SettingsWindow : Window
    {
        private static SettingsWindow? _open;

        // Excluded from coverage: this is the one entry point the app uses, and
        // it does more than construct a window — MacOSActivation.SetRegular(),
        // Show(), Activate(), StartStatusTicker(). tests/UiTests'
        // SettingsWindowSmokeTest says the same thing from the other side, and
        // reaches the private constructor directly for exactly this reason. The
        // rows the window builds are covered; putting the app in the Dock and
        // taking a window key is not something a headless suite has any business
        // doing.
        [ExcludeFromCodeCoverage]
        public static void Toggle()
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            _open = new SettingsWindow();
            _open.Closed += (_, _) =>
            {
                // A timer on a closed window would keep the window alive and go
                // on ticking for nothing.
                _open?._openClawStatusTimer?.Stop();
                _open = null;

                // The colour pickers defer their write; closing the window is the
                // last chance to land one that's still pending.
                ClaudeBuddySettings.FlushPendingSave();

                // Back to a menu-bar-only app: no Dock icon, no Cmd-Tab entry.
                MacOSActivation.SetAccessory();
            };

            // Becoming a regular app for as long as this window is open, so it
            // comes to the front and gets a Dock icon and a Cmd-Tab entry while
            // it is up.
            //
            // The reason recorded here used to be "an accessory app's window
            // can't take keyboard focus". That is not true, and the chat panel
            // demonstrates it: it is a borderless window on the same accessory
            // app, it takes typed input, and it does no policy switching at all.
            // What is different is how each one is opened — this window comes
            // from a status-item click, which does not activate the app, and the
            // panel comes from a click on one of the app's own windows, which
            // does. So the Activate() below is the part that matters here, and
            // SetRegular is what makes it stick for a window with no orb behind
            // it to have been clicked.
            MacOSActivation.SetRegular();
            _open.Show();
            _open.Activate();
            _open.StartStatusTicker();
        }

        private SettingsWindow()
        {
            Title = "Claude Buddy Settings";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MinHeight = 240;
            MaxHeight = 760;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Escape and Cmd-W close it, the way any Mac window does. That's
            // also what lets the Done button go away on macOS, where a
            // preferences window with one would look wrong.
            KeyDown += OnWindowKeyDown;

            // Liquid Glass: the window is a translucent material, not a filled
            // rectangle. AcrylicBlur is what Avalonia maps to NSVisualEffectView
            // on macOS — confirmed granted here, ActualTransparencyLevel reports
            // it back. The fallbacks matter: Windows takes Mica, and anything that
            // can end up with None still reads, because the text all sits on cards
            // that carry their own translucent fill rather than on bare glass.
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            };

            BorrowFluentToggleSwitch();
            EnsureColorPickerTheme();

            Rebuild();

            // Every card colour below is mixed for the current variant, so a
            // system-wide switch to dark while the window is open would otherwise
            // leave white cards on a dark window. Rebuilding is safe because
            // nothing here holds uncommitted state — each control writes its
            // setting as it changes.
            ActualThemeVariantChanged += (_, _) => Rebuild();
        }

        // Split out from the KeyDown handler above so the decision is testable
        // without ever calling Close() — see CloseFromKeyboardShortcut below for
        // why that matters.
        internal static bool ShouldCloseOnKeyDown(Key key, KeyModifiers modifiers) =>
            key == Key.Escape || (key == Key.W && modifiers.HasFlag(KeyModifiers.Meta));

        // Excluded from coverage: its only job is to call Close() on the two
        // shortcuts that should close a preferences window. ShouldCloseOnKeyDown
        // — which key combinations those are — is a static and is covered
        // directly; this is the half that cannot run, for the FontManager reason
        // below.
        [ExcludeFromCodeCoverage]
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldCloseOnKeyDown(e.Key, e.KeyModifiers)) CloseFromKeyboardShortcut();
        }

        // Excluded from coverage: Window.Close() corrupts a process-wide Avalonia
        // FontManager cache when called on a headless window — see
        // SettingsWindowSmokeTest.cs's own comment for the full story of how
        // that took down 25 of 28 UiTests in one CI run. ShouldCloseOnKeyDown
        // above is the actual decision, and it is fully tested; this is just the
        // one call this suite may never make.
        [ExcludeFromCodeCoverage]
        private void CloseFromKeyboardShortcut() => Close();

        // Excluded from coverage: the same Window.Close() hazard as
        // CloseFromKeyboardShortcut above, just reached from the Windows-only
        // Done button (see Body()) instead of Escape/Cmd-W.
        [ExcludeFromCodeCoverage]
        private void CloseFromDoneButton() => Close();

        // The macOS theme's ToggleSwitch template is broken against the stock
        // control: Avalonia's ToggleSwitch demands a Panel named
        // PART_MovingKnobs and its template doesn't satisfy that, so the first
        // switch to be measured throws KeyNotFoundException and takes the app
        // down. Confirmed on Avalonia 11.3.7 *and* 12.0.2, with the theme's
        // newest build for each — so it's the template, not a version mismatch,
        // and upgrading Avalonia doesn't help.
        //
        // Rather than give up switches (checkboxes would be the fallback) or
        // hand-write a template for one control, borrow Fluent's ToggleSwitch
        // ControlTheme into this window's resources. Everything else here stays
        // AppKit-styled by the theme. Remove this once upstream fixes it.
        // Excluded from coverage: reaches into the running application's style
        // resources to borrow one ControlTheme out of another theme's dictionary,
        // and its catch exists for a lookup that only fails under a particular
        // macOS theme. Neither the borrow nor the failure is arrangeable from a
        // test — and the consequence of the catch is covered where it lands:
        // Switch() falls back to a CheckBox, which every switch test in this
        // suite already exercises without knowing which of the two it got.
        [ExcludeFromCodeCoverage]
        private void BorrowFluentToggleSwitch()
        {
            try
            {
                var fluent = new Avalonia.Themes.Fluent.FluentTheme();
                if (fluent.TryGetResource(typeof(ToggleSwitch), ActualThemeVariant, out var found)
                    && found is ControlTheme fluentSwitch)
                {
                    Resources.Add(typeof(ToggleSwitch), fluentSwitch);
                }
            }
            catch
            {
                // Worst case the switches keep the theme's own template, which
                // is the crash this exists to avoid — so if this ever stops
                // working, Switch() below falls back to a CheckBox.
            }
        }

        private bool HasSwitchTheme => Resources.ContainsKey(typeof(ToggleSwitch))
                                       || !OperatingSystem.IsMacOS();

        // The same defensive shape as BorrowFluentToggleSwitch, for two different
        // reasons — and unlike that one, half of this is a confirmed hole rather
        // than a precaution.
        //
        //  - On Windows there is no ColorPicker template at all. The control lives
        //    in its own package and Avalonia.Themes.Fluent contains no reference
        //    to it, so the row would render as an empty gap, which is what an
        //    untemplated TemplatedControl looks like.
        //  - On macOS the Devolutions theme does ship /Controls/ColorPicker.axaml
        //    and its PART names cover everything the control looks up. But its
        //    ToggleSwitch template is already known to be broken against the stock
        //    control, so a themed template here is something to check rather than
        //    assume.
        //
        // Both are answered the same way: merge the ColorPicker package's own
        // Fluent styles into *this window* when the live theme has no ControlTheme
        // for the type. Window-scoped, so nothing else is restyled and no other
        // window pays for it — and window styles beat application ones, so this
        // can also be forced unconditionally if the themed picker turns out to be
        // broken rather than missing.
        //
        // Fluent.xaml's root element is <Styles>, not <ResourceDictionary> — it
        // wraps its sub-dictionaries in Styles.Resources — so this is a
        // StyleInclude.
        // Excluded from coverage: loads a XAML style dictionary from an avares://
        // URI belonging to another assembly, and catches the case where that
        // assembly's layout has changed. Worst case the pickers come out
        // unstyled, which is a gap in one card rather than a crash — but neither
        // the load nor its failure can be arranged headlessly.
        [ExcludeFromCodeCoverage]
        private void EnsureColorPickerTheme()
        {
            try
            {
                var styles = Application.Current?.Styles;
                if (styles is not null
                    && styles.TryGetResource(typeof(ColorPicker), ActualThemeVariant, out _))
                {
                    return;
                }

                Styles.Add(new StyleInclude(new Uri("avares://ClaudeBuddy/"))
                {
                    Source = new Uri(
                        "avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")
                });
            }
            catch
            {
                // Worst case the pickers come out unstyled, which is a gap in one
                // card rather than a crash — everything that was already in this
                // window still works.
            }
        }

        internal void Rebuild()
        {
            // Held against System Settings side by side, Apple's content pane is
            // *not* very transparent — the glass in Tahoe lives in sidebars,
            // popovers and menus, while a settings pane behind grouped rows stays
            // a near-opaque light surface. A near-clear wash here (the first
            // attempt at fixing the opposite mistake) let the wallpaper through
            // and read as murky rather than glassy. This sits at 85%: the material
            // still lifts the window's edges, the content still reads crisp.
            Background = new SolidColorBrush(IsDark
                ? Color.FromArgb(0xD9, 0x1E, 0x1E, 0x20)
                : Color.FromArgb(0xD9, 0xF2, 0xF2, 0xF5));

            Content = new ScrollViewer
            {
                Content = Body(),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
        }

        // No control metrics here on purpose. Heights, corner radii, fills and
        // borders for fields and pop-ups come from the macOS theme on macOS and
        // from Fluent on Windows; pinning them by hand is what produced capsule
        // pop-ups and 20pt checkboxes in the first place.

        private Control Body()
        {
            var root = new StackPanel { Margin = new Thickness(20, 18), Spacing = 18 };

            root.Children.Add(Group("Orbs", Card(OrbsRows())));

            root.Children.Add(Group("Clicking an orb", Card(ClickRows())));

            root.Children.Add(Group("Auto-organize", Card(AutoOrganizeRows())));

            root.Children.Add(Group("Orb colours", Card(OrbColourRows())));

            root.Children.Add(Group("Voice", Card(VoiceRows())));

            // One section per agent, each starting with whether it is tracked
            // at all and then everything about it — the panel, replying, extra
            // accounts, and on Windows the WSL distros.
            //
            // These used to be six sections scattered between Voice and the
            // Claude Desktop profiles: "Claude Code sessions" here, "Claude Code
            // profiles" four groups later, the same again for Codex, and the
            // Desktop app's own profiles in between. Nothing was wrong with any
            // of them individually; the order was just the order they were
            // added in, which is how a settings window gets that way.
            root.Children.Add(Group("Claude Code", ClaudeCodeSection()));

            root.Children.Add(Group("Codex", CodexSection()));

            root.Children.Add(Group("OpenClaw agents", Card(OpenClawRows())));

            // Straight after the CLI sections and before the Desktop app,
            // because that is what it is about: the same Claude Code sessions
            // those two sections govern, just not on this machine.
            root.Children.Add(Group("Other machines", Card(RemoteControlRows())));

            // Not an agent CLI at all — the Electron desktop app — so it sits
            // after them with its own profiles, which is where someone looking
            // for them would go first.
            root.Children.Add(Group("Claude Desktop",
                Card(ClaudeDesktopTintRow()),
                ProfilesCard()));

            // macOS preference windows are dismissed by the window's own close
            // button, not by a Done inside the content. Windows expects the
            // button, so it keeps it.
            if (!OperatingSystem.IsMacOS()) root.Children.Add(DoneButton());

            return root;
        }

        // internal so a test can drive each switch directly, the same reason
        // ClickRows() below is internal — walking the visual tree to find
        // whichever control a row holds depends on which theme template
        // loaded, which is not what these rows are about.
        internal Control[] OrbsRows() => new[]
        {
            Row("Show orbs",
                Switch(SessionManager.Instance?.OrbsVisible ?? ClaudeBuddySettings.ShowOrbs,
                    value => SessionManager.Instance?.SetOrbsVisible(value))),
            Row("Keep orbs for", LifetimePicker(),
                "How long an orb stays after its session goes quiet. A session that's "
                + "waiting on you is never removed, however long this is — those only go "
                + "away when you answer it or reset it from the orb's menu."),
            Row("Two-letter initials",
                Switch(ClaudeBuddySettings.TwoLetterGlyphs, value =>
                {
                    ClaudeBuddySettings.TwoLetterGlyphs = value;
                    SessionManager.Instance?.ReapplyGlyphs();
                }),
                "One letter from each of the first two words of a chat's name, or the "
                + "first two letters of it when there's only one word — instead of just "
                + "the one letter every orb shows today.")
        };

        // internal for the same reason OrbsRows() above is: a test can drive
        // each row's control directly.
        //
        // KNOWN BUG, found while writing SettingsWindowRowBuilderTests and left
        // as-is rather than fixed here (CB-3 is a coverage ticket, not a bugfix
        // one — see that test file's own comment): "Give each session a colour"
        // is built *twice*, back to back, each its own Switch bound to the same
        // ClaudeBuddySettings.AutoColorSessions and the same OnAutoColorToggled
        // handler, with two help strings that were each hand-edited slightly
        // differently at some point (compare "that has none" / "with none",
        // "so there its orb" / "so a Codex orb"). On screen this reads as one
        // switch that happens to repeat its own explanation right below itself
        // — easy to miss, and it is NOT harmless. This comment used to say the
        // two copies cannot disagree because they share state; they can, and
        // SettingsWindowCoverageTests now asserts it. They share the *setting*,
        // not the control: each switch is built from the setting once, so
        // flipping one writes the setting and leaves the other showing the old
        // value until something rebuilds the window. Two switches sitting
        // adjacent and reading differently is a visible inconsistency, not dead
        // weight. Still clearly meant to be one row.
        internal Control[] OrbColourRows() => new[]
        {
            ColorRow("Idle", "idle"),
            ColorRow("Working", "generating"),
            ColorRow("Needs you", "waiting"),
            Row("Give each session a colour",
                Switch(ClaudeBuddySettings.AutoColorSessions, OnAutoColorToggled),
                "Off, only a colour you set with /color shows on an orb. On, a session "
                + "that has none is given one, from its working directory — so a project "
                + "keeps its colour, and both CLIs agree on it. For Claude Code this "
                + "writes the same record /color writes, so the colour survives a resume "
                + "and the terminal agrees; /color still overrides it. Codex has nowhere "
                + "to write one and shows none of its own, so there its orb takes the "
                + "colour of its Codex section if it has one and the derived colour "
                + "otherwise."),
            Row("Give each session a colour",
                Switch(ClaudeBuddySettings.AutoColorSessions, OnAutoColorToggled),
                "Off, only a colour you set with /color shows on an orb. On, a session "
                + "with none is given one from its working directory, so a project keeps "
                + "its colour and both CLIs agree on it. For Claude Code that writes the "
                + "same record /color writes, so the colour survives a resume and the "
                + "terminal agrees; /color still overrides it. Codex has nowhere to write "
                + "one and shows none of its own, so a Codex orb takes its Codex section's "
                + "colour if it has one and the derived colour otherwise."),
            Row("Restore the built-in colours", ResetColorsButton(),
                "The orb's fill and its glow. The menu-bar icon follows them too — it "
                + "shows the most urgent state across every session, so very light or "
                + "very dark choices can disappear into the menu bar. A session's own "
                + "/color is separate: that one goes on the orb's ring and letter.")
        };

        // --- Voice input ---
        // --- Auto-organize ---

        // internal so a test can drive the switch directly.
        internal Control ClaudeDesktopTintRow() => Row("Tint the active window",
            Switch(ClaudeDesktopOverlay.Enabled, ClaudeDesktopOverlay.SetEnabled));

        // --- what a click does ---

        private static readonly (string Label, string Value)[] ClickChoices =
        {
            ("Go to the session", "terminal"),
            ("Open the chat panel", "chat"),
            ("Read the latest reply", "speak"),
            ("Nothing", "none")
        };

        // "Go to the session" rather than "Terminal", because that is what it
        // does: for a local CLI it is the terminal, and for a gateway agent
        // there is no terminal anywhere and the panel is the only place it
        // exists. One label covering both beats a label that is wrong for half
        // the orbs on screen.
        // internal, like the toggle handlers: these three return the control they
        // build, so a test can drive the control itself rather than hunting it in
        // a visual tree whose shape depends on which theme templates loaded.
        internal Control ClickPicker(Func<string> get, Action<string> set)
        {
            var current = get();
            var choices = ClickChoices.ToList();

            if (choices.All(c => c.Value != current)) choices.Add((current, current));

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == current),
                MinWidth = 168
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index >= 0) set(choices[index].Value);
            };
            return combo;
        }

        // internal so the three rows can be driven as the window builds them,
        // which is the only way the production getters and setters below run — a
        // test supplying its own would be checking ClickPicker rather than the
        // rows.
        internal Control[] ClickRows() => new[]
        {
            Row("Click", ClickPicker(
                () => ClaudeBuddySettings.ClickAction,
                v => ClaudeBuddySettings.ClickAction = v)),
            Row("Double click", ClickPicker(
                () => ClaudeBuddySettings.DoubleClickAction,
                v => ClaudeBuddySettings.DoubleClickAction = v)),
            Row("Triple click", ClickPicker(
                () => ClaudeBuddySettings.TripleClickAction,
                v => ClaudeBuddySettings.TripleClickAction = v),
                "Binding a second or third click makes a single click wait a moment to see "
                + "whether another is coming — there is no way to tell them apart without "
                + "that pause. Leave them on Nothing and a single click acts the instant "
                + "you release, which is what the app has always done.")
        };

        private static readonly (string Label, string Value)[] ShapeChoices =
        {
            ("Heart", "heart"),
            ("Circle", "circle"),
            ("Diamond", "diamond"),
            ("Star", "star"),
            ("Grid", "grid"),
            ("Line", "line")
        };

        internal Control ShapePicker()
        {
            var current = ClaudeBuddySettings.ArrangeShape;
            var choices = ShapeChoices.ToList();

            if (choices.All(c => c.Value != current))
                choices.Add((current, current));

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == current),
                MinWidth = 132
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0) return;

                ClaudeBuddySettings.ArrangeShape = choices[index].Value;
                SessionManager.Instance?.ReapplyArrangement();
            };
            return combo;
        }

        private Control[] AutoOrganizeRows()
        {
            return new Control[]
            {
                Row("Shape", ShapePicker(),
                    "The pattern orbs arrange into when you click the sparkle button on any orb's flyout."),
                Row("Spacing", SpacingSlider(),
                    "How far apart the orbs sit inside the shape. Drag to see them move in real time.")
            };
        }

        internal Control SpacingSlider()
        {
            var slider = new Slider
            {
                Minimum = 0.3,
                Maximum = 2.0,
                Value = ClaudeBuddySettings.ArrangeSpacing,
                MinWidth = 160,
                SmallChange = 0.05,
                LargeChange = 0.1,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = true
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                ClaudeBuddySettings.ArrangeSpacing = slider.Value;
                SessionManager.Instance?.ReapplyArrangement();
            };
            return slider;
        }

        // Off by default (see ClaudeBuddySettings.VoiceInputEnabled) —
        // turning it on is what triggers the one-time Whisper model download,
        // never the first mic click on an orb, so the multi-hundred-MB
        // fetch is always something the user just asked for here.

        // Only meaningful while a download this window kicked off is still
        // running; null the rest of the time, in which case no status row
        // is shown at all rather than a stale one.
        private string? _voiceModelStatus;

        // Only meaningful while a neural-engine download this window kicked off is
        // still running; null otherwise, so no stale row is left behind. Separate
        // from _voiceModelStatus rather than shared: enabling dictation and
        // enabling the neural voice each fetch a few hundred MB, and two
        // downloads reporting into one field would overwrite each other's text.
        private string? _neuralModelStatus;

        // Off by default and, unlike everything else in this window, off for a
        // reason that isn't about taste: while this switch is off the app opens
        // no socket, starts no background task and generates no key. Turning it
        // on is the whole of the consent to talk to a machine on the network.
        // The chat panel on a local orb, and whether it can type back.
        //
        // Unlike every other feature switch in this window the first one is
        // *on* by default, and the difference is real rather than an
        // inconsistency: it opens no socket, starts no engine and asks macOS for
        // no permission. It reads a file the hook already points at, only while
        // a panel is actually up. There is nothing to consent to.
        //
        // The second one is the OpenClaw split, for the OpenClaw reason.
        // Everything about one CLI in one place: whether it is tracked at all,
        // what its orb can do, and which extra accounts to wire.
        //
        // These used to be spread across the window — "Claude Code sessions"
        // near the top, "Claude Code profiles" four groups below it, the same
        // again for Codex, with the Desktop app's profiles in between. Nothing
        // was wrong with any of them alone; the order was the order they were
        // added in, which is how a settings window gets that way.
        private Control[] ClaudeCodeSection()
        {
            var cards = new List<Control> { Card(ClaudeCodeChatRows()) };

            if (!ClaudeBuddySettings.ClaudeCodeEnabled) return cards.ToArray();

            cards.Add(Card(ProfileDirsCard(
                blurb: "Wire Claude Buddy hooks into additional Claude Code accounts managed "
                       + "via CLAUDE_CONFIG_DIR, alongside the default ~/.claude.",
                watermark: ".claude-work",
                current: () => ClaudeBuddySettings.ClaudeCodeProfileDirs,
                add: ClaudeBuddySettings.AddClaudeCodeProfileDir,
                remove: ClaudeBuddySettings.RemoveClaudeCodeProfileDir,
                reapply: HookInstaller.ReapplyClaudeCode)));

            // WSL is genuinely Windows-only, unlike the extra accounts above,
            // and belongs here because Claude Code's sessions are what it
            // reaches.
            if (OperatingSystem.IsWindows()) AddWslCard(cards);

            return cards.ToArray();
        }

        private Control[] CodexSection()
        {
            var cards = new List<Control> { Card(CodexChatRows()) };

            if (!ClaudeBuddySettings.CodexEnabled) return cards.ToArray();

            cards.Add(Card(ProfileDirsCard(
                blurb: "Wire Claude Buddy hooks into additional Codex accounts managed via "
                       + "CODEX_HOME, alongside the default ~/.codex. Codex asks you to trust "
                       + "hooks the first time it sees them, once per account.",
                watermark: ".codex-work",
                current: () => ClaudeBuddySettings.CodexHomes,
                add: ClaudeBuddySettings.AddCodexHome,
                remove: ClaudeBuddySettings.RemoveCodexHome,
                reapply: HookInstaller.ReapplyCodex)));

            return cards.ToArray();
        }

        // Switching a CLI off hides the rest of its section rather than greying
        // it out. A column of dead switches is a worse answer to "I only use
        // Claude Code" than a two-line section is: what remains is what still
        // does something.
        private Control[] ClaudeCodeChatRows()
        {
            var rows = new List<Control>
            {
                Row("Show Claude Code sessions",
                    Switch(ClaudeBuddySettings.ClaudeCodeEnabled, OnClaudeCodeEnabledToggled),
                    "Off, Claude Code sessions get no orbs and are left out of the menu bar. "
                    + "Its hooks are left alone — they are your own config, and they keep "
                    + "writing where the app will find them again the moment you switch this "
                    + "back on.")
            };

            if (!ClaudeBuddySettings.ClaudeCodeEnabled) return rows.ToArray();

            rows.Add(Row("Chat panel on the orb",
                Switch(ClaudeBuddySettings.ClaudeCodeChatEnabled, OnClaudeCodeChatToggled),
                "Adds a keyboard button to the orb's hover menu that opens the session's "
                + "conversation — the same panel OpenClaw agents use. It is the same "
                + "conversation as the terminal's, not a copy: it reads the transcript "
                + "Claude Code already writes. Clicking the orb still goes to the terminal."));

            if (!ClaudeBuddySettings.ClaudeCodeChatEnabled) return rows.ToArray();

            rows.Add(Row("Allow replying to sessions",
                Switch(ClaudeBuddySettings.ClaudeCodeReplyEnabled, OnClaudeCodeReplyToggled),
                "Off, the panel shows what a session is doing. On, you can type into it, "
                + "answer its permission prompts and interrupt it — by typing into its tmux "
                + "pane, exactly as if you had typed there yourself, so the terminal shows it "
                + "too. Sessions not running under tmux stay read-only either way, because "
                + "the only way to type into those is to bring their window to the front."));

            return rows.ToArray();
        }

        internal void OnClaudeCodeChatToggled(bool enabled)
        {
            ClaudeBuddySettings.ClaudeCodeChatEnabled = enabled;
            Rebuild();
        }

        internal void OnClaudeCodeReplyToggled(bool enabled)
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = enabled;
        }

        // No re-wiring. The hooks read a marker file beside the status files,
        // which the scan reconciles with this setting within a couple of
        // seconds — see SessionManager.SyncAutoColorMarker. An earlier version
        // baked a flag into the hook command instead, which meant every toggle
        // rewrote Codex's hooks.json and cost the user their hook trust.
        internal void OnAutoColorToggled(bool enabled)
        {
            ClaudeBuddySettings.AutoColorSessions = enabled;
        }

        // The same two powers for Codex, and its own pair of switches rather
        // than one shared "local CLI" setting: someone can reasonably want to
        // read a Codex session and never type into it while doing the opposite
        // for Claude Code, and the two CLIs are wired independently anyway.
        //
        // The wording differs where the behaviour does, and only there.
        private Control[] CodexChatRows()
        {
            var rows = new List<Control>
            {
                Row("Show Codex sessions",
                    Switch(ClaudeBuddySettings.CodexEnabled, OnCodexEnabledToggled),
                    "Off, Codex sessions get no orbs and are left out of the menu bar. Its "
                    + "hooks are left alone, so nothing has to be re-approved when you switch "
                    + "this back on.")
            };

            if (!ClaudeBuddySettings.CodexEnabled) return rows.ToArray();

            rows.AddRange(new Control[]
            {
                Row("Chat panel on the orb",
                    Switch(ClaudeBuddySettings.CodexChatEnabled, OnCodexChatToggled),
                    "The same panel Claude Code sessions get, reading the rollout transcript "
                    + "Codex already writes. It is the same conversation as the terminal's, not "
                    + "a copy. Clicking the orb still goes to the terminal.")
            });

            if (!ClaudeBuddySettings.CodexChatEnabled) return rows.ToArray();

            rows.Add(Row("Allow replying to sessions",
                Switch(ClaudeBuddySettings.CodexReplyEnabled, OnCodexReplyToggled),
                "Off, the panel shows what a session is doing. On, you can type into it, "
                + "answer its approval prompts and interrupt it — by typing into its tmux "
                + "pane, exactly as if you had typed there yourself. Codex's approval prompts "
                + "are numbered the same way Claude Code's are, and a digit answers one "
                + "outright. Sessions not running under tmux stay read-only either way."));

            return rows.ToArray();
        }

        // Rebuild, because switching a CLI off removes the rest of its section.
        internal void OnClaudeCodeEnabledToggled(bool enabled)
        {
            ClaudeBuddySettings.ClaudeCodeEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexEnabledToggled(bool enabled)
        {
            ClaudeBuddySettings.CodexEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexChatToggled(bool enabled)
        {
            ClaudeBuddySettings.CodexChatEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexReplyToggled(bool enabled)
        {
            ClaudeBuddySettings.CodexReplyEnabled = enabled;
        }

        internal Control[] OpenClawRows()
        {
            var rows = new List<Control>
            {
                Row("Show OpenClaw agents (experimental)",
                    Switch(ClaudeBuddySettings.OpenClawEnabled, OnOpenClawToggled),
                    "Shows an orb for each recently active session on an OpenClaw gateway, "
                    + "alongside your Claude Code ones. Read-only: Claude Buddy can see what "
                    + "your agents are doing, and cannot ask them to do anything.")
            };

            if (!ClaudeBuddySettings.OpenClawEnabled) return rows.ToArray();

            rows.Add(Row("Gateway address", GatewayHostBox(),
                "The address of the machine running the gateway — an IP, because the "
                + "certificate it serves carries no hostname. Port "
                + ClaudeBuddySettings.DefaultOpenClawPort + " unless you changed it."));

            rows.Add(Row("Gateway token", GatewayTokenBox(),
                "From `gateway.auth.token` in the gateway's own openclaw.json. Stored "
                + "outside settings.json, in a file only you can read."));

            rows.Add(Row("Show sessions active within", ActiveWithinPicker(),
                "A gateway remembers every conversation it has ever had, so only recent "
                + "ones get orbs. Anything currently working shows regardless. Note that "
                + "the gateway's own idea of \"recent\" lags badly for Discord chats, so "
                + "Claude Buddy also counts anything it has watched happen since it started."));

            rows.Add(Row("Show heartbeat sessions",
                Switch(ClaudeBuddySettings.OpenClawShowHeartbeats, OnOpenClawHeartbeatsToggled),
                "A gateway wakes each agent on a timer to do background work, and it does "
                + "that in the agent's own main session — so those orbs go active together "
                + "every few minutes with nobody on the other end. They wear a beating "
                + "heart. Off hides them; the agents keep their colours in any channel they "
                + "are in either way."));

            rows.Add(Row("Allow replying to agents",
                Switch(ClaudeBuddySettings.OpenClawReplyEnabled, OnOpenClawReplyToggled),
                "Off, this shows what your agents are doing. On, you can also reply to "
                + "them from an orb — which asks the gateway for write permission, so you "
                + "have to approve this device again there (`openclaw devices approve --latest`)."));

            // Kept as a field and ticked, rather than rebuilt: the connection
            // changes state while you are looking at it — pairing gets approved,
            // a machine wakes up — and a status line that only tells the truth
            // at the moment the window was built is worse than none. Rebuilding
            // the window instead would be simpler and would also take the focus
            // out of whichever box was being typed into.
            _openClawStatus = new TextBlock
            {
                Text = OpenClawSessions.StatusText,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

            rows.Add(NoteRow(_openClawStatus));

            // The first connection from a new install lands in the gateway's
            // pending list and stays there until a human approves it, so the
            // status line above will say so rather than looking broken.
            var reconnect = new Button
            {
                Content = "Reconnect",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            reconnect.Click += (_, _) => OnOpenClawReconnectClicked();
            rows.Add(Row("", reconnect));

            // Only when the pin is the thing standing in the way, because this
            // is the one control here that gives something up.
            //
            // It has to exist. A gateway that regenerates its certificate —
            // reinstalled, upgraded, switched to mkcert — is refused for ever
            // after with a message the user can do nothing about: Reconnect
            // fails the same way every time, and the only way through was to
            // edit settings.json by hand. That is not a security property, it is
            // a dead end that teaches people to distrust the message.
            //
            // Deliberately not automatic, and deliberately not a general "always
            // trust" switch. Accepting a new certificate is exactly what an
            // interception needs you to do, so it stays a separate, explicit act
            // taken while looking at a line that says what changed.
            if (OpenClawSessions.CertificateRejected)
            {
                var trust = new Button
                {
                    Content = "Trust the new certificate",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                };

                // The pin is cleared rather than set to what was observed: the
                // next successful connection records it anyway (trust on first
                // use), and writing a fingerprint the app has been *refusing* is
                // a longer way round to the same place with more to get wrong.
                //
                // TrustNewCertificate rather than doing it here, so the button
                // is gone by the time Rebuild runs — see its own comment.
                trust.Click += (_, _) => OnTrustNewCertificateClicked();

                rows.Add(Row("", trust,
                    "The gateway's certificate has changed since this install first "
                    + "connected. That is normal after the gateway is reinstalled or "
                    + "upgraded — and is also what someone impersonating it would look "
                    + "like. Only do this if you know why it changed."));
            }

            return rows.ToArray();
        }

        // Excluded from coverage: reconnects to the gateway over a real socket —
        // the same reason OnOpenClawToggled below is excluded. The button that
        // reaches this is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawReconnectClicked()
        {
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: clears the pinned certificate and reconnects,
        // which is a real socket the same as OnOpenClawReconnectClicked above.
        // The row that reaches this — including that it only appears when
        // OpenClawSessions.CertificateRejected is set — is covered separately.
        [ExcludeFromCodeCoverage]
        private void OnTrustNewCertificateClicked()
        {
            OpenClawSessions.TrustNewCertificate();
            Rebuild();
        }

        private static readonly (string Label, int Minutes)[] ActiveWithinChoices =
        {
            ("15 minutes", 15),
            ("1 hour", 60),
            ("4 hours", 240),
            ("12 hours", 720),
            ("Everything", ClaudeBuddySettings.OpenClawActiveWithinAll)
        };

        internal Control ActiveWithinPicker()
        {
            var current = ClaudeBuddySettings.OpenClawActiveWithinMinutes;
            var choices = ActiveWithinChoices.ToList();

            // Same courtesy LifetimePicker extends: a value typed into
            // settings.json by hand shows as itself rather than being silently
            // rounded to the nearest one on the list.
            if (choices.All(choice => choice.Minutes != current))
            {
                choices.Insert(choices.Count - 1, ($"{current} minutes", current));
            }

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            };

            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0 || index >= choices.Count) return;

                var minutes = choices[index].Minutes;
                if (minutes == ClaudeBuddySettings.OpenClawActiveWithinMinutes) return;

                // No reconnect: this only changes which of the sessions we
                // already have gets an orb, and the next poll is a few seconds
                // away.
                ClaudeBuddySettings.OpenClawActiveWithinMinutes = minutes;
            };

            return combo;
        }

        private TextBlock? _openClawStatus;
        private TextBlock? _remoteControlStatus;
        private DispatcherTimer? _openClawStatusTimer;

        // Read by the tests that drive OnStatusTick. Internal rather than
        // reflected because there is nothing private about what a label says —
        // the fields are private only so that nothing outside assigns them.
        internal string? OpenClawStatusText => _openClawStatus?.Text;
        internal string? RemoteControlStatusText => _remoteControlStatus?.Text;

        // Excluded from coverage: starts a real one-second Avalonia timer. The
        // work it schedules is OnStatusTick below, which is reachable directly and
        // is tested that way — this is only the subscription and the Start().
        [ExcludeFromCodeCoverage]
        private void StartStatusTicker()
        {
            _openClawStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _openClawStatusTimer.Tick -= OnStatusTick;
            _openClawStatusTimer.Tick += OnStatusTick;
            _openClawStatusTimer.Start();
        }

        // Ticks both status lines, whichever of them the current window actually
        // built. Each is null unless its own section is expanded, so this is one
        // timer serving at most two labels rather than a timer each.
        internal void OnStatusTick(object? sender, EventArgs e)
        {
            if (_openClawStatus is not null)
            {
                var gateway = OpenClawSessions.StatusText;
                if (_openClawStatus.Text != gateway) _openClawStatus.Text = gateway;
            }

            if (_remoteControlStatus is not null)
            {
                // The relay changes state while you are looking at it — it takes
                // a few seconds to start, and it stops itself when idle — so a
                // line that was only true when the window opened would be worse
                // than none.
                var relay = RemoteControlSessions.StatusText;
                if (_remoteControlStatus.Text != relay) _remoteControlStatus.Text = relay;
            }
        }

        internal Control GatewayHostBox()
        {
            var box = new TextBox
            {
                Text = ClaudeBuddySettings.OpenClawHost,
                Watermark = "192.168.0.10",
                Width = 220
            };

            // On losing focus rather than per keystroke: every edit restarts the
            // connection, and restarting once per character typed would hammer
            // the gateway with half-written addresses.
            box.LostFocus += (_, _) =>
            {
                var value = (box.Text ?? "").Trim();
                if (value == ClaudeBuddySettings.OpenClawHost) return;

                OnGatewayHostChanged(value);
            };

            return box;
        }

        // Excluded from coverage: writes the new address, clears the pinned
        // certificate — a different gateway is a different certificate, and
        // keeping the old pin would refuse the new one for reasons the user
        // could not possibly guess — and reconnects over a real socket. The
        // no-op path above, losing focus without having actually changed the
        // address, is what a test can safely drive instead.
        [ExcludeFromCodeCoverage]
        internal void OnGatewayHostChanged(string value)
        {
            ClaudeBuddySettings.OpenClawHost = value;
            ClaudeBuddySettings.OpenClawFingerprint = "";
            OpenClawSessions.Restart();
            Rebuild();
        }

        internal Control GatewayTokenBox()
        {
            var host = ClaudeBuddySettings.OpenClawHost;
            var existing = string.IsNullOrEmpty(host) ? null : OpenClawIdentity.GatewayTokenFor(host);

            var box = new TextBox
            {
                PasswordChar = '•',
                Text = existing ?? "",
                Watermark = "paste the gateway token",
                Width = 220
            };

            box.LostFocus += (_, _) =>
            {
                var value = (box.Text ?? "").Trim();
                if (string.IsNullOrEmpty(host) || value == (existing ?? "")) return;

                OnGatewayTokenChanged(host, value);
            };

            return box;
        }

        // Excluded from coverage: same shape and same reason as
        // OnGatewayHostChanged above — a real reconnect over the network. The
        // no-op path in the LostFocus handler is what a test can safely drive.
        [ExcludeFromCodeCoverage]
        internal void OnGatewayTokenChanged(string host, string value)
        {
            OpenClawIdentity.SetGatewayTokenFor(host, value);
            OpenClawSessions.Restart();
            Rebuild();
        }

        // No Restart() here, unlike the two toggles below it. This changes which
        // sessions the *next scan* keeps, not what the connection asked the
        // gateway for, and dropping a working socket to filter a list would take
        // every gateway orb off the screen for as long as the handshake takes.
        // The orbs appear or vanish on the next poll instead.
        internal void OnOpenClawHeartbeatsToggled(bool enabled)
        {
            ClaudeBuddySettings.OpenClawShowHeartbeats = enabled;
        }

        // Excluded from coverage: reconnects to the gateway, because the scopes
        // are part of the handshake — the setting write either side of that is one
        // line, and the switch it belongs to is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawReplyToggled(bool enabled)
        {
            ClaudeBuddySettings.OpenClawReplyEnabled = enabled;

            // Reconnects, because the scopes are part of the handshake and the
            // gateway treats a changed scope set as a device to approve afresh.
            // The status row then says it is waiting for that approval.
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: opens or closes the gateway socket immediately —
        // the setting write either side of that is one line, and the switch it
        // belongs to is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawToggled(bool enabled)
        {
            ClaudeBuddySettings.OpenClawEnabled = enabled;

            // Immediately, not at the next launch: turning it off should take
            // the orbs off the screen and the socket off the network while the
            // user is still looking at the switch.
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Both of the builders below used to sit inline inside a platform gate,
        // which made them reachable on exactly one CI leg — and what they build is
        // a decision about what to tell the user, not a platform detail. Pulled
        // out so it can be asserted from either runner, leaving one line of
        // genuine platform behaviour behind at each call site.

        // macOS preference windows are dismissed by the window's own close button,
        // not by a Done inside the content. Windows expects the button, so it
        // keeps it.
        internal Button DoneButton()
        {
            var done = new Button
            {
                Content = "Done",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 90
            };

            done.Click += (_, _) => CloseFromDoneButton();
            return done;
        }

        // The bridge is tmux-based, so there is nothing to offer on Windows yet.
        // Said plainly rather than hidden: a feature that exists in the docs and
        // nowhere in the window reads as a broken install.
        internal Control[] RemoteControlUnsupportedRows() => new[]
        {
            NoteRow(new TextBlock
            {
                Text = "Sessions from other machines are macOS-only for now. "
                     + "The relay runs inside tmux, which Windows has no equivalent of here.",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            })
        };

        internal Control[] RemoteControlRows()
        {
            // Dropped before anything is built, because Rebuild() replaces the
            // whole content tree: left set, this would point at a label that is
            // no longer on screen and the ticker would spend every second
            // writing to it.
            _remoteControlStatus = null;

            // The bridge is tmux-based, so there is nothing to offer on Windows
            // yet. Said plainly rather than hidden: a feature that exists in the
            // docs and nowhere in the window reads as a broken install.
            if (!RemoteControlBridge.IsSupported) return RemoteControlUnsupportedRows();

            var rows = new List<Control>
            {
                Row("Show sessions from other machines",
                    Switch(ClaudeBuddySettings.RemoteControlEnabled, OnRemoteControlToggled),

                    // The cost is stated up front because it is the one thing
                    // about this feature a person cannot discover by looking at
                    // it: every other switch in this window only changes what is
                    // drawn.
                    "Shows an orb for each of your Claude Code sessions that has Remote Control "
                    + "on, wherever it is running. To reach them, Claude Buddy runs a hidden "
                    + "Claude Code session of its own as a relay — so this uses your account, "
                    + "and counts against your usage while it is running.")
            };

            if (!ClaudeBuddySettings.RemoteControlEnabled) return rows.ToArray();

            rows.Add(Row("Accounts", RemoteControlAccountList(),
                "Which Claude Code config directories to look through. Remote Control only shows "
                + "sessions on the same account, so tick the ones your other machines use — "
                + "each ticked account runs its own relay, and each relay costs usage while it is "
                + "running. Add more under \"Claude Code profiles\" above."));

            rows.Add(Row("Stop the relay after", RemoteControlIdlePicker(),
                "The relay shuts down once you stop using it, and starts again by itself the "
                + "next time you open or send to a remote session."));

            // Shares the OpenClaw ticker's TextBlock slot deliberately — see
            // OnStatusTick, which now reports whichever of the two is switched
            // on. Both being on at once is possible but rare, and a second
            // one-second timer for a line of text nobody is watching is worse
            // than the ambiguity.
            _remoteControlStatus = new TextBlock
            {
                Text = RemoteControlSessions.StatusText,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

            rows.Add(NoteRow(_remoteControlStatus));

            // Starting it from here rather than only from the tray, because this
            // is where someone has just switched the feature on and wants to
            // know whether it works — and "nothing appeared" is the least
            // helpful possible answer at that moment.
            var start = new Button
            {
                Content = "Start the relay now",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            start.Click += (_, _) => OnStartTheRelayNowClicked();
            rows.Add(Row("", start));

            return rows.ToArray();
        }

        // Excluded from coverage: starts a real Claude Code session in a tmux
        // pane as the relay. The button that reaches this is covered by the
        // rows around it.
        [ExcludeFromCodeCoverage]
        private void OnStartTheRelayNowClicked() => RemoteControlSessions.EnsureStarted();

        // Every config directory a relay could sign into, each with a tick.
        //
        // A list of checkboxes rather than one picker because Remote Control is
        // account-scoped: one relay sees exactly one account's sessions, so
        // seeing two accounts means running two relays. That is a real cost
        // multiplier — each is a live Claude Code session — which is why it is
        // opt-in per account rather than "all of them, automatically".
        //
        // Not free text: a directory name that is not a real profile fails by
        // producing no sessions at all, which is indistinguishable from having
        // none, so the only offer is what actually exists.
        // Excluded from coverage: WslCard runs `wsl.exe -l -q` as a real subprocess
        // to list the installed distributions, and everything it builds depends on
        // that answer.
        [ExcludeFromCodeCoverage]
        private void AddWslCard(List<Control> cards)
        {
            var wsl = WslCard();
            if (wsl is not null) cards.Add(Card(wsl));
        }

        // Excluded from coverage: opens a web page — see
        // OnDownloadVoicesLinkClicked.
        [ExcludeFromCodeCoverage]
        private void OnDownloadVoicesLinkPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            OnDownloadVoicesLinkClicked();
        }

        internal Control RemoteControlAccountList()
        {
            var offered = new List<string> { ClaudeBuddySettings.DefaultRemoteControlProfileDir };
            foreach (var dir in ClaudeBuddySettings.ClaudeCodeProfileDirs)
            {
                if (!offered.Contains(dir, StringComparer.Ordinal)) offered.Add(dir);
            }

            var selected = ClaudeBuddySettings.RemoteControlProfileDirs.ToList();

            // A previously ticked account whose profile has since been removed
            // from the Claude Code section still shows, ticked, rather than
            // silently disappearing — otherwise a relay would keep running for
            // something the window claims is not selected.
            foreach (var dir in selected)
            {
                if (!offered.Contains(dir, StringComparer.Ordinal)) offered.Add(dir);
            }

            var stack = new StackPanel { Spacing = 2 };
            var boxes = new List<CheckBox>();

            void Apply()
            {
                var chosen = boxes.Where(b => b.IsChecked == true)
                                  .Select(b => (string)b.Content!)
                                  .ToList();

                // Never all-unticked: with none selected the feature is on and
                // can do nothing, which reads as broken. Falling back to the
                // default account is the least surprising answer, and the switch
                // above is the way to actually turn it off.
                if (chosen.Count == 0)
                {
                    chosen.Add(ClaudeBuddySettings.DefaultRemoteControlProfileDir);
                    foreach (var box in boxes)
                    {
                        if ((string)box.Content! == ClaudeBuddySettings.DefaultRemoteControlProfileDir)
                            box.IsChecked = true;
                    }
                }

                ClaudeBuddySettings.SetRemoteControlProfileDirs(chosen);

                // Relays for accounts that were just un-ticked are retired by the
                // next poll, which is where the "which relays should exist"
                // decision lives. Nothing is started here: ticking an account
                // must not begin spending on it until something asks.
            }

            foreach (var dir in offered)
            {
                var box = new CheckBox
                {
                    Content = dir,
                    IsChecked = selected.Contains(dir, StringComparer.Ordinal)
                };

                box.IsCheckedChanged += (_, _) => Apply();
                boxes.Add(box);
                stack.Children.Add(box);
            }

            return stack;
        }

        private static readonly (string Label, int Minutes)[] RemoteIdleChoices =
        {
            ("2 minutes", 2),
            ("10 minutes", ClaudeBuddySettings.DefaultRemoteControlIdle),
            ("30 minutes", 30),
            ("1 hour", 60),
            ("Never", ClaudeBuddySettings.RemoteControlIdleNever)
        };

        internal Control RemoteControlIdlePicker()
        {
            var current = ClaudeBuddySettings.RemoteControlIdleMinutes;
            var choices = RemoteIdleChoices.ToList();

            if (choices.All(choice => choice.Minutes != current))
            {
                choices.Insert(choices.Count - 1, ($"{current} minutes", current));
            }

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            };

            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0 || index >= choices.Count) return;

                var minutes = choices[index].Minutes;
                if (minutes == ClaudeBuddySettings.RemoteControlIdleMinutes) return;

                // Read on every idle check rather than captured when the relay
                // started, so shortening this takes effect on the next poll
                // instead of at the next launch.
                ClaudeBuddySettings.RemoteControlIdleMinutes = minutes;
            };

            return combo;
        }

        // Excluded from coverage: tears down every Remote Control relay — the
        // setting write either side of that is one line, and the switch it belongs
        // to is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnRemoteControlToggled(bool enabled)
        {
            ClaudeBuddySettings.RemoteControlEnabled = enabled;

            // Only ever tears down, unlike OnOpenClawToggled above. Turning this
            // on must not start the relay by itself — that spends the user's
            // quota, so something has to ask for it (the tray item, opening a
            // remote session, or the button in this section).
            RemoteControlSessions.Restart();
            Rebuild();
        }

        internal Control[] VoiceRows()
        {
            var rows = new List<Control>();

            rows.Add(Row("High-quality voice (experimental)",
                Switch(ClaudeBuddySettings.NeuralVoiceEnabled, OnNeuralVoiceToggled),
                "Speaks with a neural voice (Kokoro) that runs entirely on this machine. "
                + "Downloads about 300 MB the first time and takes a few seconds "
                + "before it starts talking."));

            if (ClaudeBuddySettings.NeuralVoiceEnabled && _neuralModelStatus is not null)
            {
                rows.Add(Row("Speech engine", new TextBlock
                {
                    Text = _neuralModelStatus,
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                }));
            }

            // One list, every engine, each entry saying where it comes from — so
            // choosing a voice is also choosing the engine, and nothing is hidden
            // by a precedence the user can't see.
            rows.Add(Row("Speak voice", SpeakVoicePicker(),
                "Which voice the speaker button on the orb flyout uses to read the latest "
                + "assistant turn aloud. Marked (system) for the ones Windows or macOS "
                + "provides, (Kokoro) for the high-quality engine above, and (custom) for "
                + "anything your own speakCommand lists."));

            // Still shown, because the system voices are always among the choices
            // now rather than being shadowed by whatever else is installed — so a
            // voice added through Windows' own settings is genuinely usable.
            rows.Add(DownloadVoicesRow());

            rows.AddRange(new Control[]
            {
                Row("Enable voice input (experimental)",
                    Switch(ClaudeBuddySettings.VoiceInputEnabled, OnVoiceInputToggled),
                    "Hover an orb and click the mic that appears to dictate a prompt. Speech is "
                    + "transcribed entirely on this machine (Whisper, no cloud service) and typed "
                    + "into that session's terminal for review — nothing is sent anywhere, and "
                    + "Enter is never pressed for you.")
            });

            if (ClaudeBuddySettings.VoiceInputEnabled && _voiceModelStatus is not null)
            {
                rows.Add(Row("Voice model", new TextBlock
                {
                    Text = _voiceModelStatus,
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                }));
            }

            return rows.ToArray();
        }

        private Control SpeakVoicePicker()
        {
            // Every voice from every available engine in one list — system, Kokoro
            // and a user command together — with the engine shown against each
            // name. Previously this showed one engine's worth at a time, decided by
            // a precedence the user could not see, so downloading the neural engine
            // silently hid the system voices and configuring a command hid both.
            //
            // Selecting an entry records both the voice and which engine speaks it
            // (TextToSpeech.SelectVoice), so the choice is explicit rather than
            // inferred, and each engine's own key remembers what was picked there.
            //
            // TextToSpeech.AllVoiceOptions() enumerates every engine, and on
            // macOS that means `say -v ?` — a real process, launched synchronously.
            // Building this eagerly meant constructing the settings window at all
            // did that. The saved name alone (no scan) is enough for a placeholder
            // item; the real list, and the ability to change it, arrives the first
            // time the user actually opens the dropdown.
            var placeholder = SavedVoiceNameForPlaceholder() ?? "Loading voices…";

            var combo = new ComboBox
            {
                ItemsSource = new[] { placeholder },
                SelectedIndex = 0,
                MinWidth = 220
            };

            List<TextToSpeech.VoiceOption>? options = null;

            combo.DropDownOpened += (_, _) => options = FillVoiceList(combo, options);
            combo.SelectionChanged += (_, _) => ChooseVoice(combo, options);

            return combo;
        }

        // Excluded from coverage: AllVoiceOptions() enumerates the machine's
        // installed voices, which on macOS means running `say -v ?` as a real
        // subprocess and on the neural side means asking an engine binary that may
        // not be downloaded yet. That is exactly the scan the placeholder above
        // exists to defer, and a test that opened this dropdown would perform it.
        //
        // Deferring it is also what makes the rest of the voice section testable:
        // building those rows touches nothing, which SettingsVoiceRowTests relies
        // on and asserts.
        [ExcludeFromCodeCoverage]
        private static List<TextToSpeech.VoiceOption>? FillVoiceList(
            ComboBox combo, List<TextToSpeech.VoiceOption>? options)
        {
            if (options is not null) return options;

            options = TextToSpeech.AllVoiceOptions();
            if (options.Count == 0)
            {
                combo.ItemsSource = new[] { "No voices found" };
                combo.SelectedIndex = 0;
                combo.IsEnabled = false;
                return options;
            }

            var selected = TextToSpeech.SelectedVoice();
            combo.ItemsSource = options.Select(o => o.Label).ToList();
            combo.SelectedIndex = selected is null ? 0 : Math.Max(0, options.IndexOf(selected));
            return options;
        }

        // Excluded from coverage: only reachable once FillVoiceList above has run,
        // since until then `options` is null and this returns immediately — and
        // running it means having enumerated the machine's voices. SelectVoice
        // then writes whichever engine's voice setting the choice belongs to.
        [ExcludeFromCodeCoverage]
        private static void ChooseVoice(ComboBox combo, List<TextToSpeech.VoiceOption>? options)
        {
            if (options is null) return; // still the unscanned placeholder item

            var index = combo.SelectedIndex;
            if (index < 0 || index >= options.Count) return;

            TextToSpeech.SelectVoice(options[index]);
        }

        // The raw saved voice name, read straight from settings rather than via
        // TextToSpeech.SelectedVoice() — that method calls AllVoiceOptions()
        // itself, which is exactly the scan this placeholder exists to avoid.
        internal static string? SavedVoiceNameForPlaceholder() =>
            ClaudeBuddySettings.SpeakEngine switch
            {
                "custom" => ClaudeBuddySettings.SpeakCommandVoice,
                "neural" => ClaudeBuddySettings.NeuralVoice,
                _ => ClaudeBuddySettings.SpeakVoice
            };

        // A near-copy of OnVoiceInputToggled below, and deliberately so: the
        // download-progress dance (write the setting first, seed the status row,
        // Rebuild, marshal progress onto the UI thread, guard against this window
        // having been closed) is already established there and is worth matching
        // line for line rather than reinventing.
        //
        // The one addition is InvalidateVoiceCache: the voice list is cached for the
        // process lifetime, and this toggle changes what belongs in it — turning the
        // neural engine on adds its voices, turning it off takes them away — so
        // without it the picker would keep showing the previous answer.
        // Whether flicking this switch should start a download. Pulled out so the
        // decision stays measured while the handler that acts on it does not —
        // acting means fetching ~300MB, which no test may do.
        //
        // Both halves matter: enabling with the engine already on disk must NOT
        // re-download it, and disabling must never start one.
        internal static bool ShouldStartNeuralDownload(bool enabled) =>
            enabled && !NeuralSpeech.Installed;

        // Excluded from coverage: the one path through here that a test could not
        // already reach ends in StartNeuralVoiceDownload, which fetches about
        // 300MB over the network. Everything this method decides is
        // ShouldStartNeuralDownload above, which is covered directly; what is left
        // is the setting write, the cache invalidation and the Rebuild.
        [ExcludeFromCodeCoverage]
        internal void OnNeuralVoiceToggled(bool enabled)
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = enabled;
            TextToSpeech.InvalidateVoiceCache();

            if (!ShouldStartNeuralDownload(enabled))
            {
                Rebuild();
                return;
            }

            StartNeuralVoiceDownload();
        }

        // Excluded from coverage: starts a real ~300MB download over the
        // network (NeuralSpeech.DownloadAsync is itself already excluded for
        // exactly that reason), then chains progress and completion callbacks
        // onto the task it returns. OnNeuralVoiceToggled above — the decision
        // of *whether* to call this, which is everything a test can drive
        // without touching the network — is fully covered on its own.
        [ExcludeFromCodeCoverage]
        private void StartNeuralVoiceDownload()
        {
            _neuralModelStatus = "Downloading the speech engine and its voice model (about 300 MB)…";
            Rebuild();

            var progress = new Progress<string>(message => Dispatcher.UIThread.Post(() =>
            {
                if (_open != this) return;
                _neuralModelStatus = message;
                Rebuild();
            }));

            _ = NeuralSpeech.DownloadAsync(progress).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_open != this) return;

                    // The voice list only becomes answerable once the engine is on
                    // disk, since it is the engine that enumerates it.
                    TextToSpeech.InvalidateVoiceCache();

                    _neuralModelStatus = t.IsFaulted
                        ? "Couldn't download the speech engine — check your connection and try again."
                        : null;
                    Rebuild();
                });
            });
        }

        internal Control DownloadVoicesRow()
        {
            var link = new TextBlock
            {
                Text = "Download more voices…",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#4A90D9")),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(14, 2, 14, 8)
            };
            link.PointerPressed += OnDownloadVoicesLinkPressed;
            link.PointerEntered += (_, _) =>
                link.TextDecorations = TextDecorations.Underline;
            link.PointerExited += (_, _) =>
                link.TextDecorations = null;
            return link;
        }

        // Excluded from coverage: launches a real subprocess — macOS's `open`
        // against a System Settings deep link, or Windows' ms-settings: URI —
        // to hand off to the OS's own voice-download UI. Nothing here is
        // decided by data a test could vary; it is OperatingSystem.IsMacOS()
        // and OperatingSystem.IsWindows() picking one fixed command each, on
        // whichever platform is actually running.
        [ExcludeFromCodeCoverage]
        private void OnDownloadVoicesLinkClicked()
        {
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        ArgumentList = { "x-apple.systempreferences:com.apple.Accessibility-Settings.extension?SpokenContent" },
                        UseShellExecute = false
                    })?.Dispose();
                }
                catch { }

                TextToSpeech.InvalidateVoiceCache();
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows' own Speech page, which is where "Manage voices"
                // and "Add voices" live. There was no Windows branch here at
                // all, so the link hovered, underlined and did nothing —
                // the whole of the reported bug.
                //
                // UseShellExecute must be true, unlike the macOS call above:
                // ms-settings: is a URI for the shell to resolve, not an
                // executable to launch, and CreateProcess cannot start it.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:speech",
                    UseShellExecute = true
                })?.Dispose();
            }
        }

        // The same decision for dictation's own model, and the same reason for
        // being separate: acting on it fetches ~150MB.
        internal static bool ShouldStartVoiceInputDownload(bool enabled) =>
            enabled && !SpeechTranscriber.ModelDownloaded;

        // Excluded from coverage: ends in StartVoiceInputDownload, which fetches
        // about 150MB over the network. The decision is
        // ShouldStartVoiceInputDownload above and is covered directly.
        [ExcludeFromCodeCoverage]
        internal void OnVoiceInputToggled(bool enabled)
        {
            ClaudeBuddySettings.VoiceInputEnabled = enabled;

            if (!ShouldStartVoiceInputDownload(enabled))
            {
                Rebuild();
                return;
            }

            StartVoiceInputDownload();
        }

        // Excluded from coverage: starts a real ~150MB download over the
        // network (SpeechTranscriber.DownloadModelAsync is itself already
        // excluded for exactly that reason). Same shape as
        // StartNeuralVoiceDownload above, and for the same reason: the decision
        // of whether to call this is what OnVoiceInputToggled covers, and that
        // is testable without ever reaching the network.
        [ExcludeFromCodeCoverage]
        private void StartVoiceInputDownload()
        {
            _voiceModelStatus = "Downloading voice model (about 150 MB)…";
            Rebuild();

            var progress = new Progress<string>(message => Dispatcher.UIThread.Post(() =>
            {
                // The window this download was kicked off from may already be
                // closed (or replaced by a fresh Toggle()) by the time a
                // progress callback lands — updating a closed window's fields
                // and rebuilding its (torn-down) content would be pointless
                // at best.
                if (_open != this) return;

                _voiceModelStatus = message;
                Rebuild();
            }));

            _ = SpeechTranscriber.DownloadModelAsync(progress).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_open != this) return;

                    _voiceModelStatus = t.IsFaulted
                        ? "Couldn't download the voice model — check your connection and try again."
                        : null;
                    Rebuild();
                });
            });
        }

        // --- Mac-ish chrome ---------------------------------------------------
        // System Settings' shape: a small dimmed label, then a rounded card whose
        // rows are label-left / control-right and divided by hairlines that stop
        // short of the left edge. Built from brushes derived off the live theme
        // variant rather than hard-coded greys, so the window doesn't invert
        // badly when someone switches to light mode.

        private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

        // Apple's grouped cards are flat, crisp and *unbordered* — the fill
        // against the pane is the whole edge treatment, no rim and no gradient
        // sheen. A gradient plus a bright border, which is what a search for
        // "glass" produces, is visibly not what System Settings does.
        private IBrush CardBackground => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));

        private IBrush Hairline => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

        // A heading over several cards, for a section whose parts are separate
        // lists rather than one run of rows — an agent and its extra accounts,
        // say. Same heading treatment as the single-card form; the cards are
        // spaced the way two groups would be, so the break still reads as a
        // break without inventing a second heading level.
        private Control Group(string title, params Control[] cards)
        {
            var stack = new StackPanel { Spacing = 10 };
            foreach (var card in cards) stack.Children.Add(card);
            return Group(title, (Control)stack);
        }

        private Control Group(string title, Control card) => new StackPanel
        {
            Children =
            {
                // "Theme" and "Windows" in System Settings are semibold and full
                // strength, not the dimmed 12pt caption this had. They read as
                // headings; a dimmed caption reads as a hint.
                new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.9,
                    // Left inset matches the rows' own 14, because in System
                    // Settings the group heading sits directly above the first
                    // row's label rather than out to the left of it.
                    Margin = new Thickness(14, 0, 0, 7)
                },
                card
            }
        };

        private Control Card(params Control[] rows)
        {
            var stack = new StackPanel();

            for (var i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    stack.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Hairline,
                        Margin = new Thickness(14, 0, 0, 0)
                    });
                }

                stack.Children.Add(rows[i]);
            }

            // 12, measured off System Settings' own groups — 18 plus a drop
            // shadow made these read as floating panels, which is a popover's
            // treatment, not a grouped row's.
            return new Border
            {
                Background = CardBackground,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Child = stack
            };
        }

        // A line of text on its own, full width.
        //
        // Not Row(): that puts its control in an Auto-width column so it can sit
        // right-aligned beside a label, and a TextBlock in an Auto column is
        // never given a width to wrap inside — it just runs off the edge of the
        // window, which is what the connection status did. The help text under a
        // row wraps because it spans both columns instead, and this is that,
        // without a setting above it.
        private static Control NoteRow(Control content)
        {
            var grid = new Grid { Margin = new Thickness(14, 10) };
            grid.Children.Add(content);
            return grid;
        }

        private static Control Row(string label, Control control, string? help = null)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions(help is null ? "Auto" : "Auto,Auto"),
                Margin = new Thickness(14, 10)
            };

            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(text);

            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            if (help is not null)
            {
                var hint = new TextBlock
                {
                    Text = help,
                    FontSize = 11,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                Grid.SetRow(hint, 1);
                Grid.SetColumnSpan(hint, 2);
                grid.Children.Add(hint);
            }

            return grid;
        }

        // One row per state, seeded from the stored colour and written on change
        // with no commit step — the same read-seed-then-write shape as
        // LifetimePicker below.
        // internal: the arming guard below is the fix for a bug that wrote three
        // colours nobody chose into settings.json, and it is decided entirely in
        // managed code — no OS call, no window needed.
        internal Control ColorRow(string label, string state)
        {
            var picker = new ColorPicker
            {
                Color = OrbColors.For(state),

                // The orb builds its own alphas — the glow's gradient stops are
                // 150/95/0 over the chosen RGB, and the tray icon's alpha channel
                // is the shape of its ring — so a user-set alpha would either be
                // thrown away silently or make the orb look broken. Hidden *and*
                // disabled, so the control never shows a value we won't honour.
                IsAlphaVisible = false,
                IsAlphaEnabled = false
            };

            // No Width or Height here: the picker's metrics are the theme's
            // business, the same as the combo box's — see the note above Body().

            // ColorChanged is not trustworthy until the user has touched the
            // control, and this is not theoretical: seeding Color and subscribing
            // afterwards is not enough, because the macOS theme's template raises
            // ColorChanged *after* that with a colour of its own — a palette entry,
            // by the look of the values. It wrote #2C273C / #50D140 / #E82323 into
            // settings.json on the first launch that ever opened this window, so
            // three colours nobody chose became the user's colours, the swatches
            // re-seeded from them on the next build, and nothing anywhere looked
            // like an error.
            //
            // Comparing against the stored value can't catch that on its own: a
            // spurious change is a genuine difference. What distinguishes a real
            // edit is that a real one is preceded by a click or a focus — you
            // cannot pick a colour without opening the drop down first. So arm on
            // that, and treat everything before it as the template talking to
            // itself.
            var armed = false;

            // Tunnelling, so it arrives before the template's own button handles
            // it and marks it handled. GotFocus covers tabbing in without a click.
            picker.AddHandler(
                PointerPressedEvent,
                (object? _, PointerPressedEventArgs _) => armed = true,
                RoutingStrategies.Tunnel);
            picker.GotFocus += (_, _) => armed = true;

            picker.ColorChanged += (_, e) =>
            {
                var current = OrbColors.For(state);
                var same = e.NewColor.R == current.R
                           && e.NewColor.G == current.G
                           && e.NewColor.B == current.B;

                if (!armed)
                {
                    // Put ours back rather than just declining to save it,
                    // otherwise the swatch sits there showing a colour the app is
                    // not using. Self-correcting and terminating: the assignment
                    // raises this again, and that pass is a no-op.
                    if (!same) picker.Color = current;
                    return;
                }

                // A real edit that changes nothing still must not write. Writing
                // the current colour as an explicit hex would freeze today's
                // default into the file and light up the Reset button for a colour
                // nobody chose. Compare RGB only — alpha isn't ours (see above).
                if (same) return;

                OrbColors.Set(state, OrbColors.ToHex(e.NewColor));

                // Nothing observes the settings store, and a colour change isn't a
                // state change, so the orbs and the tray icon have to be told.
                SessionManager.Instance?.ReapplyStateColors();
            };

            return Row(label, picker);
        }

        // One button rather than a reset per row: the rows are narrow already, and
        // "put it back how it shipped" is a single intention.
        //
        // It writes null rather than today's default hex — see
        // ClaudeBuddySettings.IdleColor for why that distinction matters — and then
        // rebuilds instead of assigning each picker's Color back, because
        // assigning Color raises ColorChanged, which would write the default hex
        // straight into the file that was just cleared. Rebuilding re-seeds every
        // control from the store, which this window already does on a theme
        // change, and there's no uncommitted state to lose. It does reset the
        // scroll position, which for a window this short isn't worth solving.
        // internal so a test can raise its Click without walking the visual
        // tree to find it.
        internal Control ResetColorsButton()
        {
            var reset = new Button
            {
                Content = "Reset",
                IsEnabled = !OrbColors.AllDefault
            };

            reset.Click += (_, _) =>
            {
                OrbColors.Set("idle", null);
                OrbColors.Set("generating", null);
                OrbColors.Set("waiting", null);
                SessionManager.Instance?.ReapplyStateColors();
                Rebuild();
            };

            return reset;
        }

        // A bare switch: Avalonia's default writes "On"/"Off" beside it, which no
        // Mac control does. Falls back to a checkbox if there is no usable switch
        // template — see BorrowFluentToggleSwitch — because a settings row with a
        // working checkbox beats one that crashes the app.
        private Control Switch(bool value, Action<bool> onChange)
        {
            if (!HasSwitchTheme) return Check(value, onChange);

            var toggle = new ToggleSwitch
            {
                IsChecked = value,
                OnContent = null,
                OffContent = null
            };
            toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked ?? false);
            return toggle;
        }

        // Minutes, with 0 for forever. Coarse on purpose: the useful answers are
        // "a few minutes", "the rest of the afternoon" and "never", and a spinner
        // asking for a number would invite precision that doesn't mean anything
        // when the input is a hook that fires every couple of seconds.
        private static readonly (string Label, int Minutes)[] LifetimeChoices =
        {
            ("1 minute", 1),
            ("5 minutes", 5),
            ("15 minutes", 15),
            ("30 minutes", 30),
            ("1 hour", 60),
            ("4 hours", 240),
            ("Forever", ClaudeBuddySettings.OrbLifetimeForever)
        };

        internal Control LifetimePicker()
        {
            var current = ClaudeBuddySettings.OrbLifetimeMinutes;
            var choices = LifetimeChoices.ToList();

            // A number hand-written into settings.json shows as itself instead of
            // being silently rounded to whatever is on the list — opening this
            // window shouldn't quietly change a setting.
            if (choices.All(choice => choice.Minutes != current))
            {
                choices.Insert(choices.Count - 1, ($"{current} minutes", current));
            }

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0) return;

                ClaudeBuddySettings.OrbLifetimeMinutes = choices[index].Minutes;
            };
            return combo;
        }

        internal Control ProfilesCard()
        {
            var snapshot = ClaudeDesktopManager.Snapshot;

            if (snapshot.Profiles.Count == 0)
            {
                return Card(new TextBlock
                {
                    Text = "No profiles found. Create one from the menu bar.",
                    FontSize = 13,
                    Opacity = 0.6,
                    Margin = new Thickness(14, 12)
                });
            }

            var rows = new List<Control> { ColumnLabels() };
            rows.AddRange(snapshot.Profiles.Select(Row));
            rows.Add(new TextBlock
            {
                Text = "Colour applies to the menu swatch, the Dock icon and the window tint. "
                       + "Leave a name empty to use the folder name.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11,
                Margin = new Thickness(14, 10)
            });

            return Card(rows.ToArray());
        }

        internal static Control ColumnLabels()
        {
            var grid = RowGrid();
            Add(grid, 0, Label("Name"));
            Add(grid, 1, Label("Colour"));
            Add(grid, 2, Label("Swatch"));
            Add(grid, 3, Label("Dock"));
            Add(grid, 4, Label("Tint"));
            return grid;

            static TextBlock Label(string text) => new()
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        internal static Grid RowGrid() => new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,130,64,54,44,84"),
            Margin = new Thickness(14, 8)
        };

        internal static void Add(Grid grid, int column, Control child)
        {
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }

        // What a typed profile name becomes on disk. Blank means NULL rather than
        // an empty string, so clearing the box restores the folder-derived name
        // instead of leaving the profile with no name at all.
        internal static string? ChosenProfileName(string? typed)
        {
            var trimmed = typed?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        // Excluded from coverage: KickRefresh rescans every profile directory on a
        // background task, and does so whether or not this attribute is here.
        // ChosenProfileName above is the decision and is covered.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileName(string folder, string? typed)
        {
            ClaudeBuddySettings.Update(folder, entry => entry.Name = ChosenProfileName(typed));
            ClaudeDesktopManager.KickRefresh();
        }

        // Which stored colour a picker index means. Index 0 is "auto", which maps
        // to a NULL stored colour rather than to a colour named "auto" — that is
        // what lets a profile go back to its name-derived colour, and without it a
        // colour is a one-way door, including one set by a stray keystroke.
        //
        // Pure and separate from the handler because the handler cannot run here:
        // see ApplyProfileColour below.
        internal static string? ChosenProfileColour(IReadOnlyList<string> options, int index) =>
            index <= 0 ? null : options[index];

        // Excluded from coverage: RecolourDockIcon rebuilds a tinted clone of
        // Claude.app — scanning live processes first to decide whether it is safe
        // to delete the bundle out from under a running instance — and KickRefresh
        // rescans every profile directory on a background task. Both do that work
        // whether or not this attribute is here, which is precisely why no test may
        // call this method: an exclusion stops a line being counted, not being run.
        //
        // The decision it makes is ChosenProfileColour above, which is covered.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileColour(
            string folder, IReadOnlyList<string> options, int index)
        {
            if (index < 0) return;

            ClaudeBuddySettings.Update(
                folder, entry => entry.Color = ChosenProfileColour(options, index));

            // The Dock icon was tinted when its clone was built, so it needs
            // regenerating; the swatch and window tint just re-read the colour.
            ClaudeDesktopManager.RecolourDockIcon(folder);
            ClaudeDesktopManager.KickRefresh();
        }

        // Excluded from coverage: same KickRefresh, same reason.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileSwatch(string folder, bool value)
        {
            ClaudeBuddySettings.Update(folder, entry => entry.ShowSwatch = value);
            ClaudeDesktopManager.KickRefresh();
        }

        internal Control Row(ProfileView profile)
        {
            var folder = Path.GetFileName(profile.Directory);
            var settings = ClaudeBuddySettings.For(folder);
            var grid = RowGrid();

            var name = new TextBox
            {
                Text = settings.Name ?? "",
                Watermark = profile.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            // On every keystroke rather than on commit: there is no OK button to
            // commit at, and the tray picks it up on its next rebuild.
            name.TextChanged += (_, _) => ApplyProfileName(folder, name.Text);
            Add(grid, 0, name);

            // "auto" first, mapping to a null stored colour, so a profile can go
            // back to its name-derived colour. Without it a colour is a one-way
            // door — including one set by a stray keystroke.
            var options = new List<string> { AutoColour };
            options.AddRange(ClaudeDesktopColors.Names);

            var stored = settings.Color;
            var selected = 0;
            if (stored is { Length: > 0 })
            {
                var found = options.FindIndex(o =>
                    string.Equals(o, stored, StringComparison.OrdinalIgnoreCase));
                if (found > 0) selected = found;
            }

            var colour = new ComboBox
            {
                ItemsSource = options
                    .Select(name => name == AutoColour
                        ? SwatchItem(AutoColour, ClaudeDesktopColors.For(folder, profile.IsDefault))
                        : SwatchItem(name, ClaudeDesktopColors.ByName(name)))
                    .ToList(),
                SelectedIndex = selected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            colour.SelectionChanged += (_, _) =>
                ApplyProfileColour(folder, options, colour.SelectedIndex);
            Add(grid, 1, colour);

            Add(grid, 2, Check(settings.ShowSwatch, value => ApplyProfileSwatch(folder, value)));

            Add(grid, 3, Check(settings.TintDockIcon, value =>
                ClaudeBuddySettings.Update(folder, entry => entry.TintDockIcon = value)));

            Add(grid, 4, Check(settings.TintWindow, value =>
                ClaudeBuddySettings.Update(folder, entry => entry.TintWindow = value)));

            Add(grid, 5, DeleteProfileButton(profile));

            return grid;
        }

        // Removing a profile, which means removing a Claude Desktop login, its
        // chat history and its local databases. There was no way to do it at
        // all before this — "Reveal profiles folder" and a trip to Finder was
        // the whole story — which is a gap rather than a safeguard, because
        // deleting the folder by hand leaves the cloned Dock icon and the saved
        // name and colour behind.
        //
        // Two clicks, not a modal. The second click is the confirmation, the
        // button says what it is about to do while it waits, and it gives up
        // after a few seconds so a stray click cannot arm it and leave it
        // armed. A dialog would be the heavier answer, and the thing this
        // guards is already recoverable — it goes to the Trash.
        internal Control DeleteProfileButton(ProfileView profile)
        {
            // The default profile is Claude Desktop's own directory rather than
            // one this app made, so there is nothing here to offer.
            if (profile.IsDefault) return new Panel();

            var button = new Button
            {
                Content = "Delete",
                FontSize = 11,
                Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var armed = false;
            DispatcherTimer? disarm = null;

            void Disarm()
            {
                disarm?.Stop();
                disarm = null;
                armed = false;
                button.Content = "Delete";
            }

            button.Click += (_, _) =>
            {
                if (!armed)
                {
                    // Say what will happen, in the place the click will happen.
                    armed = true;
                    button.Content = "Trash it?";

                    disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    disarm.Tick += (_, _) => Disarm();
                    disarm.Start();
                    return;
                }

                Disarm();
                button.IsEnabled = false;

                var outcome = ClaudeDesktopManager.DeleteProfile(profile);

                switch (outcome)
                {
                    case ClaudeDesktopManager.DeleteOutcome.Deleted:
                        // Say so on the button *and* rebuild. The rescan the
                        // delete kicked is asynchronous, so the row may still be
                        // in the snapshot this rebuild reads — and a row that
                        // stays put with nothing said reads as a click that did
                        // nothing. Whichever happens first, the user is told.
                        button.Content = "Trashed";
                        Rebuild();
                        break;

                    case ClaudeDesktopManager.DeleteOutcome.RefusedRunning:
                        button.Content = "Quit it first";
                        button.IsEnabled = true;
                        break;

                    default:
                        button.Content = "Couldn't";
                        button.IsEnabled = true;
                        break;
                }
            };

            return button;
        }

        private const string AutoColour = "auto";

        internal static Control SwatchItem(string colourName, Color color)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Shapes.Ellipse
                    {
                        Width = 11,
                        Height = 11,
                        Fill = new SolidColorBrush(color),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock { Text = colourName, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }

        internal static CheckBox Check(bool value, Action<bool> onChange)
        {
            var box = new CheckBox
            {
                IsChecked = value,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            box.IsCheckedChanged += (_, _) => onChange(box.IsChecked ?? false);
            return box;
        }

        // ---- extra CLI account directories ----------------------------------

        // Distinct from "Profiles" above (Claude Desktop, the Electron app) —
        // these are *CLI* config directory names, for a second (or third...)
        // account: CLAUDE_CONFIG_DIR for Claude Code, CODEX_HOME for Codex,
        // e.g. an alias like `alias kwork="CLAUDE_CONFIG_DIR=~/.claude-work claude"`.
        // Each one is wired in *addition* to the default, never as a
        // replacement, and never auto-discovered: only names added here (or
        // passed explicitly to an installer's --profile-dir) are ever touched.
        //
        // One card, used twice. The two lists are separate settings because
        // they are separate products and someone can easily have extras of one
        // and not the other, but nothing about the *UI* differs between them,
        // and two near-identical copies would have drifted the way the
        // platforms did.
        internal static Control ProfileDirsCard(
            string blurb,
            string watermark,
            Func<IReadOnlyList<string>> current,
            Action<string> add,
            Action<string> remove,
            Action reapply)
        {
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };

            content.Children.Add(new TextBlock
            {
                Text = blurb,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11
            });

            var itemsPanel = new StackPanel { Spacing = 4 };
            foreach (var dirName in current())
            {
                itemsPanel.Children.Add(ProfileDirRow(dirName, itemsPanel, remove));
            }
            content.Children.Add(itemsPanel);

            var input = new TextBox { Watermark = watermark, Width = 220 };
            var browseButton = new Button { Content = "Browse…" };
            var addButton = new Button { Content = "Add" };
            var status = new TextBlock { FontSize = 11, Opacity = 0.7 };

            // A folder picker is the more discoverable way to do this, but
            // typing stays available too: these variables can point at a
            // directory that doesn't exist yet (the CLI creates it on first use
            // with that alias), which a picker — browsing existing folders only
            // — can't select.
            browseButton.Click += async (_, _) => await BrowseInto(browseButton, status, input);

            addButton.Click += (_, _) =>
            {
                var name = input.Text?.Trim();
                if (string.IsNullOrEmpty(name)) return;

                status.Text = "";
                add(name);
                itemsPanel.Children.Add(ProfileDirRow(name, itemsPanel, remove));
                input.Text = "";

                // Off the UI thread: this shells out to an installer, and on
                // Windows that means a re-run for every already-wired WSL
                // distro. The window must stay responsive the whole time; the
                // installers' own timeouts guarantee it eventually returns
                // either way.
                addButton.IsEnabled = false;
                input.IsEnabled = false;
                Task.Run(reapply).ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        addButton.IsEnabled = true;
                        input.IsEnabled = true;
                    });
                });
            };

            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { input, browseButton, addButton }
            });
            content.Children.Add(status);

            content.Children.Add(new TextBlock
            {
                Text = "Removing a profile stops it from being wired on future changes; it doesn't "
                       + "remove hooks already written to that profile's own config.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.5,
                FontSize = 11
            });

            return content;
        }

        // Returns the picked folder's bare name (e.g. ".claude-work"), or
        // Excluded from coverage: everything past the first line is the OS folder
        // picker — see BrowseForProfileDir below.
        [ExcludeFromCodeCoverage]
        private static async Task BrowseInto(Control owner, TextBlock status, TextBox input)
        {
            var picked = await BrowseForProfileDir(owner, status);
            if (picked is not null) input.Text = picked;
        }

        // null if the user cancelled or picked something invalid — in which
        // case `status` is set to say why, since a folder outside the home
        // directory would resolve to the wrong place everywhere: native
        // Windows, WSL, and macOS alike (see ProfileDirsCard's doc comment).
        //
        // No longer Windows-only. GetWslHomeUncPaths already returns an empty
        // list off Windows, so the validation below reduces to "a direct child
        // of $HOME", which is exactly the rule the macOS installers enforce
        // when they refuse a profile name containing a slash.
        // Excluded from coverage: opens the OS's own folder picker and waits for a
        // human to answer it. There is no headless storage provider to satisfy
        // OpenFolderPickerAsync, and a test that got one would sit on a modal
        // dialog until CI timed out.
        //
        // The rule it enforces afterwards — a profile directory must be a direct
        // child of $HOME, because anything else resolves to the wrong place on
        // native Windows, WSL and macOS alike — is the part worth having, and it
        // is the same rule the macOS installers apply when they refuse a profile
        // name containing a slash. WslIntegration's half of it is pure and covered
        // in tests/UnitTests/WslIntegrationRulesTests.
        [ExcludeFromCodeCoverage]
        private static async Task<string?> BrowseForProfileDir(Control owner, TextBlock status)
        {
            var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider;
            if (storageProvider is null) return null;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var startLocation = await storageProvider.TryGetFolderFromPathAsync(home);

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a config directory",
                SuggestedStartLocation = startLocation,
                AllowMultiple = false
            });

            if (result.Count == 0) return null;

            var pickedPath = result[0].TryGetLocalPath();
            if (pickedPath is null)
            {
                status.Text = "Couldn't resolve that selection to a local folder.";
                return null;
            }

            // Must be a *direct* child of a recognized home, not just nested
            // somewhere under it — the underlying model (-ProfileDir/
            // -WslProfileDir) only ever takes a single path segment relative
            // to home, so picking e.g. ~/work/claude-work would silently
            // keep only "claude-work" and resolve to the wrong, nonexistent
            // ~/claude-work instead. "A recognized home" is deliberately not
            // just the Windows one: the same dir name gets wired under every
            // WSL distro's home too (see the section's own doc comment), and
            // a profile can be WSL-only with no Windows-side counterpart at
            // all — e.g. a second Linux-only account — so a folder picked
            // from \\wsl.localhost\<distro>\home\<user>\ must validate the
            // same way a native one does, not be rejected just because it
            // isn't under C:\Users\....
            var validHomes = new[] { home }.Concat(WslIntegration.GetWslHomeUncPaths())
                .Select(h => h.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToList();
            var trimmedPicked = pickedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmedPicked);
            if (!validHomes.Any(h => string.Equals(parent, h, StringComparison.OrdinalIgnoreCase)))
            {
                status.Text = "Must be a folder directly inside your home directory (" + home
                    + ") or a WSL distro's home directory, not a nested subfolder.";
                return null;
            }

            status.Text = "";
            return Path.GetFileName(trimmedPicked);
        }

        [SupportedOSPlatform("windows")]
        internal static Control ProfileDirRow(
            string dirName, StackPanel itemsPanel, Action<string> remove)
        {
            var label = new TextBlock
            {
                Text = dirName,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 220
            };
            var removeButton = new Button { Content = "Remove" };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { label, removeButton }
            };

            removeButton.Click += (_, _) =>
            {
                remove(dirName);
                itemsPanel.Children.Remove(row);
            };

            return row;
        }

        // Null when there's nothing to show (no WSL, or no distros besides
        // Docker Desktop's plumbing ones) — Body() omits the whole group in
        // that case rather than showing an empty card.
        [SupportedOSPlatform("windows")]
        // Excluded from coverage: begins by running `wsl.exe -l -q` as a real
        // subprocess to list the installed distributions, and everything it builds
        // depends on that answer. WslIntegration's own rules — which settings file
        // to write, how to read a home directory out of /etc/passwd — are pure and
        // covered in tests/UnitTests/WslIntegrationRulesTests.
        [ExcludeFromCodeCoverage]
        private static Control? WslCard()
        {
            var distros = WslIntegration.ListDistros();
            if (distros.Count == 0) return null;

            var content = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
            content.Children.Add(new TextBlock
            {
                Text = "Wire or unwire Claude Buddy's hooks for Claude Code running inside each WSL distro.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11
            });

            foreach (var distro in distros) content.Children.Add(WslDistroRow(distro));

            return content;
        }

        [SupportedOSPlatform("windows")]
        private static Control WslDistroRow(string distro)
        {
            var box = new CheckBox { Content = distro, IsChecked = WslIntegration.IsWired(distro) };

            box.IsCheckedChanged += (_, _) =>
            {
                var desired = box.IsChecked ?? false;
                // Prevent a re-entrant click while the script from the last
                // one is still running — SetWired has its own ~10s timeout,
                // so this can't disable the box forever even on failure.
                box.IsEnabled = false;

                Task.Run(() => WslIntegration.SetWired(distro, desired)).ContinueWith(task =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        box.IsEnabled = true;
                        // Revert on failure rather than show a checked state
                        // that doesn't match settings.json's real contents.
                        if (!task.Result) box.IsChecked = !desired;
                    });
                });
            };

            return box;
        }
    }

    // Switching activation policy so a menu-bar-only app can own a focusable
    // window, then switching back.
    //
    // Excluded from coverage as a whole class: every method here is P/Invoke
    // into libobjc, calling into the real Objective-C runtime to change this
    // process's actual NSApplication activation policy — exactly the kind of
    // call a headless CI runner has no business making. It is already only
    // reachable in production via the already-excluded SettingsWindow.Toggle().
    [ExcludeFromCodeCoverage]
    internal static class MacOSActivation
    {
        private const string Objc = "/usr/lib/libobjc.A.dylib";

        private const long Regular = 0;    // NSApplicationActivationPolicyRegular
        private const long Accessory = 1;  // NSApplicationActivationPolicyAccessory

        [DllImport(Objc)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool msgSend_policy(IntPtr receiver, IntPtr selector, long policy);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern void msgSend_activate(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.U1)] bool ignoringOtherApps);

        public static void SetRegular()
        {
            // Apply() no-ops on non-macOS, but SharedApplication() below goes
            // straight to a P/Invoke against libobjc with no such guard — on
            // Windows that's a DllNotFoundException with nothing upstream to
            // catch it, which took the whole app down the first time Settings
            // was opened on a real Windows box.
            if (!OperatingSystem.IsMacOS()) return;

            Apply(Regular);

            // Regular alone doesn't bring us forward; without this the window
            // opens behind whatever you were using.
            var app = SharedApplication();
            if (app != IntPtr.Zero)
            {
                msgSend_activate(app, sel_registerName("activateIgnoringOtherApps:"), true);
            }
        }

        public static void SetAccessory() => Apply(Accessory);

        private static void Apply(long policy)
        {
            if (!OperatingSystem.IsMacOS()) return;

            try
            {
                var app = SharedApplication();
                if (app == IntPtr.Zero) return;

                msgSend_policy(app, sel_registerName("setActivationPolicy:"), policy);
            }
            catch
            {
                // Worst case the window opens without focus; not fatal.
            }
        }

        private static IntPtr SharedApplication()
        {
            var cls = objc_getClass("NSApplication");
            return cls == IntPtr.Zero ? IntPtr.Zero : msgSend(cls, sel_registerName("sharedApplication"));
        }
    }
}
