using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // CB-168's "start a new chat" dialog: pick a CLI, pick a folder, Start.
    //
    // Follows SettingsWindow's singleton-window pattern almost exactly —
    // Toggle()/Activate(), MacOSActivation.SetRegular() on open and
    // SetAccessory() on close — see that file's own header for why the
    // activation dance exists at all. The one difference is the optional
    // prefill: an orb's "New chat here" context item opens this already
    // pointed at that orb's cwd and CLI, which SettingsWindow never needs.
    internal sealed class NewChatWindow : Window
    {
        private static NewChatWindow? _open;

        // Excluded from coverage for the same reason SettingsWindow.Toggle
        // is, and to the same width: this is real OS-facing work
        // (MacOSActivation, Show, Activate) with no decision inside it that
        // can be pulled out and left testable — "is a window already open"
        // only means anything once a real Window has actually been shown,
        // which is the one thing a headless suite must not do here (see
        // NewChatWindowTests' own header on why Close() specifically is
        // dangerous). Every actual decision this dialog makes — which CLI is
        // enabled, what the folder combo offers, what Start's status line
        // says, when the watch gives up, what an orb pre-fills — is pulled
        // out into its own pure method below and tested directly; this is
        // only ever the wiring around them. tests/UiTests reaches the
        // private constructor instead — see NewChatWindowTests.
        [ExcludeFromCodeCoverage]
        public static void Toggle(NewChatCli? prefillCli = null, string? prefillCwd = null)
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            _open = new NewChatWindow(prefillCli, prefillCwd);
            _open.Closed += (_, _) =>
            {
                _open?._watchTimer?.Stop();
                _open = null;
                MacOSActivation.SetAccessory();
            };

            MacOSActivation.SetRegular();
            _open.Show();
            _open.Activate();
        }

        // The seam a UI test satisfies in place of the real session scan —
        // the same shape NewChatAvailability.CurrentForTests and
        // NewChatLauncher.LaunchForTests already use. Without it, both the
        // folder combo (RecentFolders.Merge wants live cwds) and the
        // orb-appeared watch (NewChatOrbWatch wants the ids that existed
        // before a launch) would depend on SessionManager.Instance, which is
        // null outside the running app.
        internal static Func<IReadOnlyDictionary<string, SessionStatus>>? CurrentStatusesForTests;

        // The seam for Browse…, in the ChooseSoundFile shape SettingsWindow
        // already uses: a Func a test can point at a canned answer instead
        // of the real OS folder picker.
        internal static Func<Task<string?>>? ChooseFolderForTests;

        private static IReadOnlyDictionary<string, SessionStatus> CurrentStatuses() =>
            CurrentStatusesForTests?.Invoke()
            ?? SessionManager.Instance?.AllStatuses
            ?? new Dictionary<string, SessionStatus>();

        private readonly StackPanel _cliList = new() { Spacing = 6 };
        private readonly ComboBox _folderCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _statusLine = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly Button _startButton = new() { Content = "Start" };
        private readonly Button _browseButton = new() { Content = "Browse…" };
        private readonly NewChatCli? _prefillCli;
        private readonly string? _prefillCwd;

        private NewChatCli? _selectedCli;
        private HashSet<string>? _watchPriorIds;
        private NewChatCli? _watchCli;
        private string? _watchFolder;
        private DateTime _watchDeadlineUtc;
        private DispatcherTimer? _watchTimer;

        // Parameterless-looking default args rather than an overload, so the
        // reflection seam every smoke test in this app uses
        // (GetConstructor(..., types: new[] { typeof(NewChatCli?),
        // typeof(string) })) reaches exactly one constructor — the same
        // reasoning SettingsWindowSmokeTest's own header gives for reaching
        // this privately rather than through Toggle().
        private NewChatWindow(NewChatCli? prefillCli = null, string? prefillCwd = null)
        {
            _prefillCli = prefillCli;
            _prefillCwd = prefillCwd;

            Title = "New chat";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            MinHeight = 200;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += OnWindowKeyDown;

            Content = Body();
        }

        // Escape and Cmd-W close it, the same as SettingsWindow — a small
        // dialog with no unsaved state to protect. Pure, in
        // SettingsWindow.ShouldCloseOnKeyDown's own shape, so the decision
        // is testable without ever calling the real Close() — closing a
        // headless window here risks the same process-wide font-cache
        // corruption SettingsWindowSmokeTest's own comment documents.
        internal static bool ShouldClose(Key key, KeyModifiers modifiers) =>
            key == Key.Escape || (key == Key.W && modifiers.HasFlag(KeyModifiers.Meta));

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldClose(e.Key, e.KeyModifiers)) CloseForReal();
        }

        // The one line that actually needs the process's real windowing:
        // excluded so ShouldClose above stays the thing a test can drive.
        [ExcludeFromCodeCoverage]
        private void CloseForReal() => Close();

        private Control Body()
        {
            var root = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };

            root.Children.Add(new TextBlock { Text = "CLI", FontWeight = FontWeight.SemiBold });
            BuildCliList();
            root.Children.Add(_cliList);

            root.Children.Add(new TextBlock { Text = "Folder", FontWeight = FontWeight.SemiBold });

            var folderRow = new DockPanel();
            DockPanel.SetDock(_browseButton, Dock.Right);
            _browseButton.Margin = new Avalonia.Thickness(8, 0, 0, 0);
            _browseButton.Click += async (_, _) => await BrowseForFolder();
            folderRow.Children.Add(_browseButton);
            BuildFolderCombo();
            folderRow.Children.Add(_folderCombo);
            root.Children.Add(folderRow);

            _startButton.Click += async (_, _) => await StartClicked();
            root.Children.Add(_startButton);

            root.Children.Add(_statusLine);

            return root;
        }

        // internal for NewChatWindowSmokeTests, the same visibility
        // TrayMenuTests etc. use to reach a window's own children without a
        // synthesized click on each one.
        internal StackPanel CliList => _cliList;
        internal ComboBox FolderCombo => _folderCombo;
        internal TextBlock StatusLine => _statusLine;
        internal Button StartButton => _startButton;
        internal Button BrowseButton => _browseButton;
        internal NewChatCli? SelectedCli => _selectedCli;

        private void BuildCliList()
        {
            _cliList.Children.Clear();

            var options = NewChatAvailability.CurrentForTests?.Invoke() ?? NewChatAvailability.Current();
            NewChatCli? firstEnabled = null;

            var lastCli = ClaudeBuddySettings.NewChatLastCli is { Length: > 0 } saved
                && Enum.TryParse<NewChatCli>(saved, out var parsed)
                ? parsed
                : (NewChatCli?)null;

            foreach (var option in options)
            {
                var radio = new RadioButton
                {
                    GroupName = "NewChatCli",
                    Content = NewChatLauncher.DisplayName(option.Cli),
                    IsEnabled = option.Enabled,
                    Tag = option.Cli
                };

                if (option.Enabled && firstEnabled is null) firstEnabled = option.Cli;

                // Reason is set exactly when disabled, Warning exactly when
                // enabled-but-imperfect — NewChatOption's own comment states
                // this, so at most one of the two is ever shown.
                if (!option.Enabled && option.Reason is { Length: > 0 } reason)
                {
                    ToolTip.SetTip(radio, reason);
                }
                else if (option.Warning is { Length: > 0 } warning)
                {
                    ToolTip.SetTip(radio, warning);
                }

                var capturedCli = option.Cli;
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked == true) _selectedCli = capturedCli;
                };

                _cliList.Children.Add(radio);
            }

            // Preference order: an explicit prefill (from an orb's "New chat
            // here"), then whatever was chosen last time, then the first
            // enabled row — so the dialog never opens with nothing selected
            // when at least one CLI is available.
            var preferred =
                (_prefillCli is { } wanted && options.Any(o => o.Cli == wanted && o.Enabled)) ? wanted
                : (lastCli is { } last && options.Any(o => o.Cli == last && o.Enabled)) ? last
                : firstEnabled;

            if (preferred is { } chosen)
            {
                _selectedCli = chosen;
                foreach (var child in _cliList.Children)
                {
                    if (child is RadioButton { Tag: NewChatCli cli } rb && cli == chosen) rb.IsChecked = true;
                }
            }
        }

        private void BuildFolderCombo()
        {
            var live = CurrentStatuses().Values;
            var saved = ClaudeBuddySettings.NewChatRecentFolders;
            var folders = RecentFolders.Merge(live, saved).ToList();

            if (_prefillCwd is { Length: > 0 } pre && !folders.Contains(pre))
            {
                folders.Insert(0, pre);
            }

            if (folders.Count == 0) folders.Add(Environment.CurrentDirectory);

            _folderCombo.ItemsSource = folders;

            var selectIndex = _prefillCwd is { Length: > 0 } wanted ? folders.IndexOf(wanted) : 0;
            _folderCombo.SelectedIndex = selectIndex >= 0 ? selectIndex : 0;
        }

        private async Task BrowseForFolder()
        {
            var seam = ChooseFolderForTests;
            var picked = seam is not null ? await seam() : await PickFolderAsync();

            if (picked is not { Length: > 0 }) return;

            var items = (_folderCombo.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
            if (!items.Contains(picked)) items.Insert(0, picked);
            _folderCombo.ItemsSource = items;
            _folderCombo.SelectedItem = picked;
        }

        // Excluded from coverage: opens the OS's own folder picker, the same
        // reason SettingsWindow's PickSoundFileAsync/BrowseForProfileDir are
        // — there is no headless storage provider to satisfy
        // OpenFolderPickerAsync. BrowseForFolder's own branching (which of
        // the two paths runs) is covered through ChooseFolderForTests
        // instead.
        [ExcludeFromCodeCoverage]
        private async Task<string?> PickFolderAsync()
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null) return null;

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder",
                AllowMultiple = false
            });

            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }

        private async Task StartClicked()
        {
            if (_selectedCli is not { } cli)
            {
                _statusLine.Text = "Choose a CLI first.";
                return;
            }

            var folder = (_folderCombo.SelectedItem as string) is { Length: > 0 } chosen
                ? chosen
                : Environment.CurrentDirectory;

            _startButton.IsEnabled = false;
            _statusLine.Text = "Starting…";

            var priorIds = new HashSet<string>(CurrentStatuses().Keys, StringComparer.Ordinal);

            var launch = NewChatLauncher.LaunchForTests?.Invoke(cli, folder) ?? NewChatLauncher.Launch(cli, folder);
            _statusLine.Text = launch.Message;
            _startButton.IsEnabled = true;

            if (launch.Outcome != LaunchOutcome.Launched) return;

            ClaudeBuddySettings.SetNewChatLastCli(cli.ToString());

            var updatedFolders = RecentFolders.Merge(
                CurrentStatuses().Values,
                new[] { folder }.Concat(ClaudeBuddySettings.NewChatRecentFolders).ToList());
            ClaudeBuddySettings.SetNewChatRecentFolders(updatedFolders);

            StartWatch(priorIds, cli, folder);
        }

        private void StartWatch(HashSet<string> priorIds, NewChatCli cli, string folder)
        {
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);

            _watchTimer?.Stop();
            _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watchTimer.Tick += OnWatchTick;
            _watchTimer.Start();
        }

        // Named rather than an inline lambda so a test can invoke it
        // directly with the (sender, EventArgs) shape a real DispatcherTimer
        // tick would use, the same pattern LocalCliChatSessionTests' own
        // comment describes for its debounce timer — no real ~20s wall-clock
        // wait needed to cover either branch below.
        internal void OnWatchTick(object? sender, EventArgs e)
        {
            if (_watchCli is not { } cli || _watchFolder is not { } folder || _watchPriorIds is not { } prior)
            {
                _watchTimer?.Stop();
                return;
            }

            var match = NewChatOrbWatch.FindMatch(CurrentStatuses(), prior, cli, folder);
            if (match is not null)
            {
                _statusLine.Text = NewChatLauncher.DisplayName(cli) + " is running — its orb should be on screen.";
                _watchTimer?.Stop();
                return;
            }

            if (DateTime.UtcNow >= _watchDeadlineUtc)
            {
                _statusLine.Text =
                    "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.";
                _watchTimer?.Stop();
            }
        }

        // Test seam: sets the watch's own fields without going through
        // StartClicked's full launch flow, so OnWatchTick's two branches can
        // be driven directly.
        internal void ArmWatchForTests(HashSet<string> priorIds, NewChatCli cli, string folder, DateTime deadlineUtc)
        {
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = deadlineUtc;
        }
    }
}
