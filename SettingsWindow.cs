using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
            Width = 560;
            SizeToContent = SizeToContent.Height;
            MinHeight = 220;
            MaxHeight = 760;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Content = new ScrollViewer
            {
                Content = Body(),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
        }

        private Control Body()
        {
            var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

            var snapshot = ClaudeDesktopManager.Snapshot;

            root.Children.Add(Header("Claude Desktop profiles"));

            if (snapshot.Profiles.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "No profiles found. Create one from the menu bar.",
                    Opacity = 0.7
                });
            }
            else
            {
                root.Children.Add(ColumnLabels());
                foreach (var profile in snapshot.Profiles) root.Children.Add(Row(profile));

                root.Children.Add(new TextBlock
                {
                    Text = "Colour applies to the menu swatch, the Dock icon and the window tint. "
                           + "Leave a name empty to use the folder name.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    FontSize = 11
                });
            }

            root.Children.Add(new Separator { Margin = new Thickness(0, 6) });
            root.Children.Add(Header("Claude Buddy"));

            root.Children.Add(Toggle(
                "Show orbs",
                SessionManager.Instance?.OrbsVisible ?? ClaudeBuddySettings.ShowOrbs,
                value => SessionManager.Instance?.SetOrbsVisible(value)));

            root.Children.Add(Toggle(
                "Tint the active Claude Desktop window",
                ClaudeDesktopOverlay.Enabled,
                ClaudeDesktopOverlay.SetEnabled));

            if (OperatingSystem.IsWindows())
            {
                root.Children.Add(ClaudeCodeProfilesSection());

                var wslRows = WslSection();
                if (wslRows is not null) root.Children.Add(wslRows);
            }

            var done = new Button
            {
                Content = "Done",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 90
            };
            done.Click += (_, _) => Close();
            root.Children.Add(done);

            return root;
        }

        // Distinct from "Claude Desktop profiles" above (the Electron app) —
        // these are Claude Code *CLI* config directory names, for a second
        // (or third...) account managed via CLAUDE_CONFIG_DIR, e.g. an alias
        // like `alias kwork="CLAUDE_CONFIG_DIR=~/.claude-work claude"`. Each
        // one is wired in *addition* to the default ~/.claude, on native
        // Windows and every WSL distro below — never a replacement for it,
        // and never auto-discovered: only names added here (or passed
        // explicitly to install-windows-hooks.ps1's -ProfileDir/
        // -WslProfileDir) are ever touched. Always shown, unlike WslSection:
        // native wiring applies regardless of whether WSL is even installed.
        [SupportedOSPlatform("windows")]
        private static Control ClaudeCodeProfilesSection()
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new Separator { Margin = new Thickness(0, 6) });
            section.Children.Add(Header("Claude Code profiles"));
            section.Children.Add(new TextBlock
            {
                Text = "Wire Claude Buddy hooks into additional Claude Code accounts managed via "
                       + "CLAUDE_CONFIG_DIR, alongside the default ~/.claude.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 11
            });

            var itemsPanel = new StackPanel { Spacing = 4 };
            foreach (var dirName in ClaudeBuddySettings.ClaudeCodeProfileDirs)
            {
                itemsPanel.Children.Add(ProfileDirRow(dirName, itemsPanel));
            }
            section.Children.Add(itemsPanel);

            var input = new TextBox { Watermark = ".claude-work", Width = 220 };
            var browseButton = new Button { Content = "Browse…" };
            var addButton = new Button { Content = "Add" };
            var status = new TextBlock { FontSize = 11, Opacity = 0.7 };

            // A folder picker is the more discoverable way to do this, but
            // typing stays available too: CLAUDE_CONFIG_DIR can point at a
            // directory that doesn't exist yet (Claude Code creates it on
            // first use with that alias), which a picker — browsing existing
            // folders only — can't select.
            browseButton.Click += async (_, _) =>
            {
                var picked = await BrowseForProfileDir(browseButton, status);
                if (picked is not null) input.Text = picked;
            };

            addButton.Click += (_, _) =>
            {
                var name = input.Text?.Trim();
                if (string.IsNullOrEmpty(name)) return;

                status.Text = "";
                ClaudeBuddySettings.AddClaudeCodeProfileDir(name);
                itemsPanel.Children.Add(ProfileDirRow(name, itemsPanel));
                input.Text = "";

                // Off the UI thread: this shells out (native wiring, plus a
                // re-run for every already-wired WSL distro), and the window
                // must stay responsive the whole time — ReapplyProfiles'
                // own internal timeouts guarantee it eventually returns
                // either way.
                addButton.IsEnabled = false;
                input.IsEnabled = false;
                Task.Run(WslIntegration.ReapplyProfiles).ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        addButton.IsEnabled = true;
                        input.IsEnabled = true;
                    });
                });
            };

            section.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { input, browseButton, addButton }
            });
            section.Children.Add(status);

            section.Children.Add(new TextBlock
            {
                Text = "Removing a profile stops it from being wired on future changes; it doesn't "
                       + "remove hooks already written to that profile's own settings.json.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.5,
                FontSize = 11
            });

            return section;
        }

        // Returns the picked folder's bare name (e.g. ".claude-work"), or
        // null if the user cancelled or picked something invalid — in which
        // case `status` is set to say why, since a folder outside the home
        // directory would resolve to the wrong place on both native Windows
        // and WSL (see ClaudeCodeProfilesSection's own doc comment).
        [SupportedOSPlatform("windows")]
        private static async Task<string?> BrowseForProfileDir(Control owner, TextBlock status)
        {
            var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider;
            if (storageProvider is null) return null;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var startLocation = await storageProvider.TryGetFolderFromPathAsync(home);

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a Claude Code config directory",
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
        private static Control ProfileDirRow(string dirName, StackPanel itemsPanel)
        {
            var label = new TextBlock
            {
                Text = dirName,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 220
            };
            var remove = new Button { Content = "Remove" };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { label, remove }
            };

            remove.Click += (_, _) =>
            {
                ClaudeBuddySettings.RemoveClaudeCodeProfileDir(dirName);
                itemsPanel.Children.Remove(row);
            };

            return row;
        }

        // Null when there's nothing to show (no WSL, or no distros besides
        // Docker Desktop's plumbing ones) — Body() omits the section entirely
        // in that case rather than showing an empty header.
        [SupportedOSPlatform("windows")]
        private static Control? WslSection()
        {
            var distros = WslIntegration.ListDistros();
            if (distros.Count == 0) return null;

            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new Separator { Margin = new Thickness(0, 6) });
            section.Children.Add(Header("WSL integration"));
            section.Children.Add(new TextBlock
            {
                Text = "Wire or unwire Claude Buddy's hooks for Claude Code running inside each WSL distro.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 11
            });

            foreach (var distro in distros) section.Children.Add(WslDistroRow(distro));

            return section;
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

        private static TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold
        };

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
            Margin = new Thickness(0, 2)
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

        private static Control Toggle(string text, bool value, Action<bool> onChange)
        {
            var box = new CheckBox { Content = text, IsChecked = value };
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
