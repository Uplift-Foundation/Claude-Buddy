using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

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
                _open = null;

                // The colour pickers defer their write; closing the window is the
                // last chance to land one that's still pending.
                ClaudeBuddySettings.FlushPendingSave();

                // Back to a menu-bar-only app: no Dock icon, no Cmd-Tab entry.
                MacOSActivation.SetAccessory();
            };

            // An accessory app's window can't take keyboard focus, so a name
            // field would silently swallow every keystroke. Becoming a regular
            // app for as long as the window is open is the supported fix, and it
            // is why this is a Toggle rather than a plain Show.
            MacOSActivation.SetRegular();
            _open.Show();
            _open.Activate();
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
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape
                    || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                {
                    Close();
                }
            };

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

        private void Rebuild()
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

            root.Children.Add(Group("Orbs", Card(
                Row("Show orbs",
                    Switch(SessionManager.Instance?.OrbsVisible ?? ClaudeBuddySettings.ShowOrbs,
                        value => SessionManager.Instance?.SetOrbsVisible(value))),
                Row("Keep orbs for", LifetimePicker(),
                    "How long an orb stays after its session goes quiet. A session that's "
                    + "waiting on you is never removed, however long this is — those only go "
                    + "away when you answer it or reset it from the orb's menu."))));

            // Its own group rather than three more rows in Orbs: that card already
            // has two rows and one of them carries a paragraph of help, so five
            // would read as a list rather than a group — and System Settings
            // groups by what a setting is *about*. The labels are the same three
            // words the tray menu already uses for these states.
            root.Children.Add(Group("Orb colours", Card(
                ColorRow("Idle", "idle"),
                ColorRow("Working", "generating"),
                ColorRow("Needs you", "waiting"),
                Row("Restore the built-in colours", ResetColorsButton(),
                    "The orb's fill and its glow. The menu-bar icon follows them too — it "
                    + "shows the most urgent state across every session, so very light or "
                    + "very dark choices can disappear into the menu bar. A session's own "
                    + "/color is separate: that one goes on the orb's ring and letter."))));

            root.Children.Add(Group("Claude Desktop", Card(
                Row("Tint the active window",
                    Switch(ClaudeDesktopOverlay.Enabled, ClaudeDesktopOverlay.SetEnabled)))));

            root.Children.Add(Group("Profiles", ProfilesCard()));

            // macOS preference windows are dismissed by the window's own close
            // button, not by a Done inside the content. Windows expects the
            // button, so it keeps it.
            if (!OperatingSystem.IsMacOS())
            {
                var done = new Button
                {
                    Content = "Done",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 90
                };
                done.Click += (_, _) => Close();
                root.Children.Add(done);
            }

            return root;
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
        private Control ColorRow(string label, string state)
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
        private Control ResetColorsButton()
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

        private Control LifetimePicker()
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

        private Control ProfilesCard()
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

        private static Control ColumnLabels()
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

        private static Grid RowGrid() => new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,130,64,54,44"),
            Margin = new Thickness(14, 8)
        };

        private static void Add(Grid grid, int column, Control child)
        {
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }

        private Control Row(ProfileView profile)
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
            name.TextChanged += (_, _) =>
            {
                var typed = name.Text?.Trim();
                ClaudeBuddySettings.Update(folder, entry =>
                    entry.Name = string.IsNullOrEmpty(typed) ? null : typed);
                ClaudeDesktopManager.KickRefresh();
            };
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
            {
                var index = colour.SelectedIndex;
                if (index < 0) return;

                var chosen = index == 0 ? null : options[index];
                ClaudeBuddySettings.Update(folder, entry => entry.Color = chosen);

                // The Dock icon was tinted when its clone was built, so it needs
                // regenerating; the swatch and window tint just re-read the colour.
                ClaudeDesktopManager.RecolourDockIcon(folder);
                ClaudeDesktopManager.KickRefresh();
            };
            Add(grid, 1, colour);

            Add(grid, 2, Check(settings.ShowSwatch, value =>
            {
                ClaudeBuddySettings.Update(folder, entry => entry.ShowSwatch = value);
                ClaudeDesktopManager.KickRefresh();
            }));

            Add(grid, 3, Check(settings.TintDockIcon, value =>
                ClaudeBuddySettings.Update(folder, entry => entry.TintDockIcon = value)));

            Add(grid, 4, Check(settings.TintWindow, value =>
                ClaudeBuddySettings.Update(folder, entry => entry.TintWindow = value)));

            return grid;
        }

        private const string AutoColour = "auto";

        private static Control SwatchItem(string colourName, Color color)
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

        private static CheckBox Check(bool value, Action<bool> onChange)
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
    }

    // Switching activation policy so a menu-bar-only app can own a focusable
    // window, then switching back.
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
