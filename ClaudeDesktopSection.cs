using Avalonia.Controls;

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
            if (!OperatingSystem.IsMacOS()) return;

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
        }

        private static NativeMenuItem BuildProfileItem(ProfileView profile)
        {
            var item = new NativeMenuItem(ProfileLabel(profile));

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

            var logs = new NativeMenuItem("Reveal logs");
            logs.Click += (_, _) => ClaudeDesktopManager.RevealLogs(profile);
            submenu.Add(logs);

            item.Menu = submenu;
            return item;
        }

        private static string ProfileLabel(ProfileView profile)
        {
            var dot = profile.Activity switch
            {
                ProfileActivity.Launching or ProfileActivity.Quitting => "◐",
                _ => profile.IsRunning ? "●" : "○"
            };

            var suffix = profile.Activity switch
            {
                ProfileActivity.Launching => "   Launching…",
                ProfileActivity.Quitting => "   Quitting…",
                ProfileActivity.ForceQuitOffered => "   won't quit",
                ProfileActivity.Error => "   " + (profile.Message ?? "error"),
                _ => ""
            };

            return $"{dot} {Truncate(profile.DisplayName)}{suffix}";
        }

        // Profile names are folder names, so they can be arbitrarily long; the
        // session list above already caps its own labels for the same reason.
        private static string Truncate(string name) =>
            name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)].TrimEnd() + "…";
    }
}
