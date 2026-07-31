using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
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

            Rebuild();

            // Every card colour below is mixed for the current variant, so a
            // system-wide switch to dark while the window is open would otherwise
            // leave white cards on a dark window. Rebuilding is safe because
            // nothing here holds uncommitted state — each control writes its
            // setting as it changes.
            ActualThemeVariantChanged += (_, _) => Rebuild();
        }

        private void Rebuild()
        {
            // Barely a tint. macOS has already put a frosted NSVisualEffectView
            // behind this window (verified: ActualTransparencyLevel comes back
            // AcrylicBlur), and that frost *is* the glass — painting 75% opaque
            // grey over it, which is what this did first, produces a flat panel
            // that happens to sit on a blur nobody can see. Everything legible
            // here rides on the cards below instead, which is also how Tahoe does
            // it: the window is a material, the content is on top of it.
            Background = new SolidColorBrush(IsDark
                ? Color.FromArgb(0x33, 0x0A, 0x0A, 0x0C)
                : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

            Content = new ScrollViewer
            {
                Content = Body(),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
        }

        // Fluent's controls are sized for touch; AppKit's are not. Every text
        // field and pop-up here is pinned to roughly the height of the real
        // thing, which also brings the rows down to a Mac row height.
        private const double ControlHeight = 26;

        // Capsules, not rectangles with the corners taken off: Liquid Glass rounds
        // a control to its own half-height, and that shape is most of what
        // separates a Tahoe pop-up from a Fluent one.
        private static CornerRadius Capsule => new(ControlHeight / 2);

        private TextBox Slim(TextBox box)
        {
            box.Height = ControlHeight;
            box.MinHeight = ControlHeight;
            box.FontSize = 13;
            box.Padding = new Thickness(10, 0);
            box.CornerRadius = Capsule;
            box.Background = FieldBackground;
            box.BorderBrush = FieldBorder;
            box.BorderThickness = new Thickness(1);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            return box;
        }

        private ComboBox Slim(ComboBox combo)
        {
            combo.Height = ControlHeight;
            combo.MinHeight = ControlHeight;
            combo.FontSize = 13;
            combo.Padding = new Thickness(11, 0, 4, 0);
            combo.CornerRadius = Capsule;
            combo.Background = FieldBackground;
            combo.BorderBrush = FieldBorder;
            combo.BorderThickness = new Thickness(1);
            return combo;
        }

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

        // Glass, not paint. A card is a lit sheet of it: brighter at the top than
        // the bottom, because that's the cheap trick that reads as a curved
        // surface catching light, and edged with a gradient that goes from a
        // near-white highlight down to almost nothing — the specular rim Liquid
        // Glass puts on everything. Both variants build from translucent white so
        // the frost behind still comes through.
        private IBrush CardBackground => VerticalGradient(
            IsDark ? Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),
            IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

        private IBrush CardBorder => VerticalGradient(
            IsDark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
            IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        private IBrush Hairline => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

        // Glass on glass: a field or pop-up is its own small pane rather than an
        // opaque white box punched through the card.
        private IBrush FieldBackground => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        private IBrush FieldBorder => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));

        private static IBrush VerticalGradient(Color top, Color bottom) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(bottom, 1)
            }
        };

        private Control Group(string title, Control card) => new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    // Higher than you would use on an opaque panel: this label is
                    // the one piece of text sitting directly on the glass, with
                    // whatever is behind the window showing through it.
                    Opacity = 0.75,
                    Margin = new Thickness(6, 0, 0, 6)
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

            // Liquid Glass rounds hard and floats: 18 rather than Fluent's 4, and
            // a soft shadow, because a pane of glass sits *above* the material
            // instead of being drawn into it.
            return new Border
            {
                Background = CardBackground,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                ClipToBounds = true,
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetY = 2,
                    Blur = 14,
                    Color = Color.FromArgb(IsDark ? (byte)0x4D : (byte)0x1F, 0, 0, 0)
                }),
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

        // A bare switch: Avalonia's default writes "On"/"Off" beside it, which no
        // Mac control does.
        private static ToggleSwitch Switch(bool value, Action<bool> onChange)
        {
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

            var combo = Slim(new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            });
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

            var name = Slim(new TextBox
            {
                Text = settings.Name ?? "",
                Watermark = profile.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
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

            var colour = Slim(new ComboBox
            {
                ItemsSource = options
                    .Select(name => name == AutoColour
                        ? SwatchItem(AutoColour, ClaudeDesktopColors.For(folder, profile.IsDefault))
                        : SwatchItem(name, ClaudeDesktopColors.ByName(name)))
                    .ToList(),
                SelectedIndex = selected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
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
