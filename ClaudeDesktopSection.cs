using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClaudeBuddy
{
    // The Claude Desktop block of the status-bar menu. TrayController calls
    // Append() and otherwise knows nothing about profiles, so removing this
    // feature is a two-line revert there plus deleting these files.
    internal static class ClaudeDesktopSection
    {
        private const int MaxNameLength = 28;

        public static void Append(NativeMenu menu)
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;

            var snapshot = ClaudeDesktopManager.Snapshot;
            if (!snapshot.AppInstalled) return;

            menu.Add(new NativeMenuItemSeparator());
            menu.Add(new NativeMenuItem("Claude Desktop") { IsEnabled = false });

            foreach (var profile in snapshot.Profiles)
            {
                menu.Add(BuildProfileItem(profile));
            }

            var newProfile = new NativeMenuItem("New profile");
            newProfile.Click += (_, _) => ClaudeDesktopManager.NewProfile();
            menu.Add(newProfile);

            var revealRoot = new NativeMenuItem("Reveal profiles folder");
            revealRoot.Click += (_, _) => ClaudeDesktopManager.RevealProfilesFolder();
            menu.Add(revealRoot);

            // Tinted Dock icons and the window-tint overlay have no Windows
            // analogue (no Dock, and the overlay is built on
            // CGWindowListCopyWindowInfo) — out of scope for the port, so this
            // whole submenu stays macOS-only rather than offering controls
            // that would do nothing there.
            if (!OperatingSystem.IsMacOS()) return;

            // Coloured Dock icons come from a cloned bundle per profile, and
            // Claude's updater only touches the one in /Applications — so clones
            // need rebuilding after an update or they keep running the old
            // version. Tucked in a submenu: it's maintenance, not everyday use.
            var icons = new NativeMenuItem("Dock icons");
            var iconMenu = new NativeMenu();

            var rebuild = new NativeMenuItem("Rebuild after a Claude update");
            rebuild.Click += (_, _) => ClaudeDesktopManager.RebuildDockIcons();
            iconMenu.Add(rebuild);

            var revealBundles = new NativeMenuItem("Reveal bundles folder");
            revealBundles.Click += (_, _) => ClaudeDesktopManager.RevealDockIconBundles();
            iconMenu.Add(revealBundles);

            var tint = new NativeMenuItem("Tint the active window")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ClaudeDesktopOverlay.Enabled
            };
            tint.Click += (_, _) => ClaudeDesktopOverlay.SetEnabled(!ClaudeDesktopOverlay.Enabled);
            iconMenu.Add(tint);

            icons.Menu = iconMenu;
            menu.Add(icons);
        }

        internal static NativeMenuItem BuildProfileItem(ProfileView profile)
        {
            var item = new NativeMenuItem(ProfileLabel(profile));

            // Colour says which profile, fill says whether it's running — the
            // same split the orbs use, where colour is identity and never
            // competes with state.
            var folder = Path.GetFileName(profile.Directory);
            if (ClaudeBuddySettings.For(folder).ShowSwatch)
            {
                item.Icon = Swatch(
                    ClaudeDesktopColors.For(folder, profile.IsDefault),
                    filled: profile.IsRunning);
            }

            // The child NativeMenu *and* its owning NativeMenuItem are built
            // fresh on every rebuild. Nothing clears NativeMenu.Parent when an
            // item leaves Items, so a cached child throws "NativeMenu already
            // has a parent" out of Avalonia's coercer the second time round.
            //
            // Three items, always, in this order: a submenu that changes length
            // as state changes makes the menu jump around under the pointer.
            var submenu = new NativeMenu();

            var busy = profile.Activity is ProfileActivity.Launching or ProfileActivity.Quitting;

            var primary = new NativeMenuItem(profile.IsRunning ? "Bring to front" : "Launch")
            {
                IsEnabled = !busy
            };
            primary.Click += (_, _) =>
            {
                if (profile.IsRunning) ClaudeDesktopManager.Focus(profile.Pid);
                else ClaudeDesktopManager.Launch(profile);
            };
            submenu.Add(primary);

            var offerForce = profile.Activity == ProfileActivity.ForceQuitOffered;
            var quit = new NativeMenuItem(offerForce ? "Force quit" : "Quit")
            {
                IsEnabled = profile.IsRunning && profile.Activity != ProfileActivity.Quitting
            };
            quit.Click += (_, _) =>
            {
                if (offerForce) ClaudeDesktopManager.ForceQuit(profile);
                else ClaudeDesktopManager.Quit(profile);
            };
            submenu.Add(quit);

            submenu.Add(BuildThemeItem(profile));

            var logs = new NativeMenuItem("Reveal logs");
            logs.Click += (_, _) => ClaudeDesktopManager.RevealLogs(profile);
            submenu.Add(logs);

            item.Menu = submenu;
            return item;
        }

        // Claude Desktop's light/dark choice lives in each profile's own
        // config.json, so it's already per-profile — the one way to make the app
        // windows themselves differ, since the app has no accent colour.
        //
        // A nested submenu rather than three more rows, so the parent submenu
        // keeps a fixed length whatever the state. Writing while the instance is
        // running would be discarded when it exits, so it's offered only while
        // stopped, and the label says why rather than leaving a dead item.
        internal static NativeMenuItem BuildThemeItem(ProfileView profile)
        {
            if (profile.IsRunning)
            {
                return new NativeMenuItem("Theme — quit to change") { IsEnabled = false };
            }

            var item = new NativeMenuItem("Theme");
            var choices = new NativeMenu();

            foreach (var (mode, label) in new[]
                     {
                         ("system", "Match system"),
                         ("light", "Light"),
                         ("dark", "Dark")
                     })
            {
                var choice = new NativeMenuItem(label)
                {
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = string.Equals(profile.ThemeMode, mode, StringComparison.OrdinalIgnoreCase)
                };
                var captured = mode;
                choice.Click += (_, _) => ClaudeDesktopManager.SetTheme(profile, captured);
                choices.Add(choice);
            }

            item.Menu = choices;
            return item;
        }

        // Swatches are cached: Rebuild() runs on every menu open and there are
        // only ever a handful of (colour, filled) combinations.
        private static readonly Dictionary<(uint Rgb, bool Filled), Bitmap> SwatchCache = new();

        internal static Bitmap Swatch(Color color, bool filled)
        {
            var key = ((uint)((color.R << 16) | (color.G << 8) | color.B), filled);
            if (SwatchCache.TryGetValue(key, out var cached)) return cached;

            // macOS: 32 physical pixels at 192 dpi = 16x16 dips, the size a menu
            // item image wants, with retina detail to spare.
            //
            // Windows renders that as a quarter of the circle. The bitmap's dip
            // size is 16x16 while its pixel buffer is 32x32, and Avalonia's Win32
            // NativeMenuItem.Icon path takes the dip size but reads the pixels
            // 1:1 — so it crops to the top-left 16x16 pixels, which is exactly
            // the top-left quadrant of the dot. Drawing 1:1 there sidesteps the
            // disagreement: same geometry in dips, fewer pixels, whole circle.
            var scale = OperatingSystem.IsMacOS() ? 2 : 1;
            var bitmap = new RenderTargetBitmap(
                new PixelSize(16 * scale, 16 * scale),
                new Vector(96 * scale, 96 * scale));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var brush = new SolidColorBrush(color);
                var circle = new Rect(3, 3, 10, 10);
                if (filled) ctx.DrawEllipse(brush, null, circle);
                else ctx.DrawEllipse(null, new Pen(brush, 2.5), circle);
            }

            SwatchCache[key] = bitmap;
            return bitmap;
        }

        internal static string ProfileLabel(ProfileView profile)
        {
            // No state glyph in the text: the swatch carries it. A dot as well
            // would just be noise next to the icon.
            var suffix = profile.Activity switch
            {
                ProfileActivity.Launching => "   Launching…",
                ProfileActivity.Quitting => "   Quitting…",
                ProfileActivity.ForceQuitOffered => "   won't quit",
                ProfileActivity.Error => "   " + (profile.Message ?? "error"),
                _ => ""
            };

            // Two processes on one profile directory corrupts leveldb and
            // SQLite, and it used to be invisible here: instances were counted
            // with TryAdd, so a duplicate collapsed into the same single
            // "running" row. It can happen without this app's involvement —
            // launching Claude from the Dock while a tinted clone of the same
            // profile is already up does it — so the menu has to be able to say
            // so, otherwise nothing ever will.
            if (suffix.Length == 0 && profile.InstanceCount > 1)
            {
                suffix = $"   ⚠ {profile.InstanceCount} instances — quit one";
            }

            return $"{Truncate(profile.DisplayName)}{suffix}";
        }

        // Profile names are folder names, so they can be arbitrarily long; the
        // session list above already caps its own labels for the same reason.
        internal static string Truncate(string name) =>
            name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)].TrimEnd() + "…";
    }
}
