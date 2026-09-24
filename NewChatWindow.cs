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
        public static void Toggle(NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            _open = new NewChatWindow(prefillCli, prefillCwd, prefillAgentId);
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

        // The OpenClaw slot's four seams, one per real call it makes —
        // availability, the agent list, starting the conversation, and
        // finding the orb the scan eventually creates for it. Same shape as
        // every other seam here: a test points these at a canned answer
        // instead of ClaudeBuddySettings/OpenClawSessions/SessionManager.
        internal static Func<OpenClawNewChatAvailability>? OpenClawAvailabilityForTests;
        internal static Func<IReadOnlyList<(string Id, string Name)>>? KnownAgentsForTests;

        internal static Func<string, CancellationToken, Task<(IRemoteChatSession? Session, string? Failure)>>?
            StartOpenClawConversationForTests;

        internal static Func<string, OrbWindow?>? OrbForTests;

        private static IReadOnlyDictionary<string, SessionStatus> CurrentStatuses() =>
            CurrentStatusesForTests?.Invoke()
            ?? SessionManager.Instance?.AllStatuses
            ?? new Dictionary<string, SessionStatus>();

        private static OrbWindow? OrbForSession(string sessionId) =>
            OrbForTests?.Invoke(sessionId) ?? SessionManager.Instance?.OrbFor(sessionId);

        // One agent row in the OpenClaw picker. ToString() rather than a
        // DisplayMemberBinding, the same reason the folder combo's plain
        // strings need neither — ComboBox falls back to ToString() with no
        // binding configured, and a ValueTuple's field names (Item1/Item2)
        // aren't reachable by a binding path anyway.
        internal sealed record AgentItem(string Id, string Name)
        {
            public override string ToString() => Name;
        }

        // The sentinel a RadioButton.Tag holds for the OpenClaw row, so the
        // same IsCheckedChanged handler that reads a boxed NewChatCli for
        // the three local rows can tell the fourth one apart by reference
        // rather than by a value that could collide with a real enum member.
        // internal rather than private: NewChatWindowTests needs it to find
        // the OpenClaw row's own reason text via ReasonTextFor, the same way
        // it finds a local row's by NewChatCli.
        internal static readonly object OpenClawTag = new();

        private readonly StackPanel _cliList = new() { Spacing = 6 };
        private readonly StackPanel _folderSection = new() { Spacing = 6 };
        private readonly StackPanel _agentSection = new() { Spacing = 6, IsVisible = false };
        private readonly ComboBox _folderCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _agentCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _statusLine = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly Button _startButton = new() { Content = "Start" };
        private readonly Button _browseButton = new() { Content = "Browse…" };
        private readonly NewChatCli? _prefillCli;
        private readonly string? _prefillCwd;
        private readonly string? _prefillAgentId;

        private NewChatCli? _selectedCli;
        private bool _openClawSelected;
        private HashSet<string>? _watchPriorIds;
        private NewChatCli? _watchCli;
        private string? _watchFolder;
        private string? _watchOpenClawSessionId;
        private IRemoteChatSession? _watchOpenClawSession;
        private DateTime _watchDeadlineUtc;
        private DispatcherTimer? _watchTimer;

        // Parameterless-looking default args rather than an overload, so the
        // reflection seam every smoke test in this app uses
        // (GetConstructor(..., types: new[] { typeof(NewChatCli?),
        // typeof(string), typeof(string) })) reaches exactly one constructor
        // — the same reasoning SettingsWindowSmokeTest's own header gives
        // for reaching this privately rather than through Toggle().
        private NewChatWindow(NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
        {
            _prefillCli = prefillCli;
            _prefillCwd = prefillCwd;
            _prefillAgentId = prefillAgentId;

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

            // Folder (local CLIs) and Agent (OpenClaw) occupy the same slot —
            // "OpenClaw selected: an agent picker replaces the folder field"
            // is the plan's own wording — so both sections are built once
            // and visibility toggles between them rather than one replacing
            // the other's controls in the tree.
            _folderSection.Children.Add(new TextBlock { Text = "Folder", FontWeight = FontWeight.SemiBold });

            var folderRow = new DockPanel();
            DockPanel.SetDock(_browseButton, Dock.Right);
            _browseButton.Margin = new Avalonia.Thickness(8, 0, 0, 0);
            _browseButton.Click += async (_, _) => await BrowseForFolder();
            folderRow.Children.Add(_browseButton);
            BuildFolderCombo();
            folderRow.Children.Add(_folderCombo);
            _folderSection.Children.Add(folderRow);
            root.Children.Add(_folderSection);

            _agentSection.Children.Add(new TextBlock { Text = "Agent", FontWeight = FontWeight.SemiBold });
            _agentSection.Children.Add(_agentCombo);
            root.Children.Add(_agentSection);

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
        internal StackPanel FolderSection => _folderSection;
        internal ComboBox AgentCombo => _agentCombo;
        internal StackPanel AgentSection => _agentSection;
        internal TextBlock StatusLine => _statusLine;
        internal Button StartButton => _startButton;
        internal Button BrowseButton => _browseButton;
        internal NewChatCli? SelectedCli => _selectedCli;
        internal bool OpenClawSelected => _openClawSelected;

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
                // this, so at most one of the two is ever shown. Stated on
                // screen, not only as a tooltip: a reason nobody can see
                // without hovering isn't a stated reason, and a screenshot
                // can't prove a tooltip exists.
                var statedText = !option.Enabled ? option.Reason : option.Warning;
                if (statedText is { Length: > 0 } tip) ToolTip.SetTip(radio, tip);

                var capturedCli = option.Cli;
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked != true) return;
                    _selectedCli = capturedCli;
                    _openClawSelected = false;
                    UpdateTargetSectionVisibility();
                };

                _cliList.Children.Add(radio);

                if (statedText is { Length: > 0 } shown)
                {
                    _cliList.Children.Add(ReasonText(shown, option.Cli));
                }
            }

            var openClawAvailability = OpenClawAvailabilityForTests?.Invoke() ?? OpenClawNewChat.AvailabilityFor(
                ClaudeBuddySettings.OpenClawEnabled, ClaudeBuddySettings.OpenClawHost,
                ClaudeBuddySettings.OpenClawReplyEnabled);
            var openClawReady = openClawAvailability == OpenClawNewChatAvailability.Ready;

            var openClawRadio = new RadioButton
            {
                GroupName = "NewChatCli",
                Content = "OpenClaw",
                IsEnabled = openClawReady,
                Tag = OpenClawTag
            };

            var openClawReasonText = openClawReady ? null : OpenClawNewChat.ReasonFor(openClawAvailability);
            if (openClawReasonText is { Length: > 0 } openClawTip) ToolTip.SetTip(openClawRadio, openClawTip);

            openClawRadio.IsCheckedChanged += (_, _) =>
            {
                if (openClawRadio.IsChecked != true) return;
                _selectedCli = null;
                _openClawSelected = true;
                BuildAgentCombo();
                UpdateTargetSectionVisibility();
            };

            _cliList.Children.Add(openClawRadio);

            if (openClawReasonText is { Length: > 0 } shownOpenClawReason)
            {
                _cliList.Children.Add(ReasonText(shownOpenClawReason, OpenClawTag));
            }

            // Preference order: an explicit OpenClaw agent prefill (an
            // OpenClaw orb's own "New chat here") wins outright when the
            // slot is actually usable — it names one specific agent, which
            // is more specific than any local prefill or last-used choice
            // could be. Failing that: a local prefill (an orb's own CLI),
            // then whatever was chosen last time, then the first enabled
            // local row, then OpenClaw if it's the only usable option — so
            // the dialog never opens with nothing selected when at least one
            // entry is available.
            if (_prefillAgentId is { Length: > 0 } wantedAgent && openClawReady)
            {
                _openClawSelected = true;
                openClawRadio.IsChecked = true;
                BuildAgentCombo(wantedAgent);
                UpdateTargetSectionVisibility();
                return;
            }

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
            else if (openClawReady)
            {
                _openClawSelected = true;
                openClawRadio.IsChecked = true;
                BuildAgentCombo();
            }

            UpdateTargetSectionVisibility();
        }

        private void UpdateTargetSectionVisibility()
        {
            _folderSection.IsVisible = !_openClawSelected;
            _agentSection.IsVisible = _openClawSelected;
        }

        // The stated reason/warning line under a CLI row — small, secondary
        // text, indented to read as belonging to the row above it. Tag
        // carries the same value the row's own RadioButton.Tag does (a
        // NewChatCli, or OpenClawTag for the fourth row), so a test can find
        // "the reason text for this row" the same way it finds the row
        // itself, without depending on sibling order in _cliList.Children.
        private static TextBlock ReasonText(string text, object tag) => new()
        {
            Text = text,
            Tag = tag,
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(24, -2, 0, 4)
        };

        // internal for NewChatWindowTests: the reason/warning line for a
        // given row's tag, or null when that row has none (an enabled row
        // with no warning, most local CLIs most of the time).
        internal TextBlock? ReasonTextFor(object tag) =>
            _cliList.Children.OfType<TextBlock>().SingleOrDefault(t => Equals(t.Tag, tag));

        private void BuildAgentCombo(string? preferredAgentId = null)
        {
            var agents = KnownAgentsForTests?.Invoke() ?? OpenClawSessions.KnownAgents();
            var items = agents.Select(a => new AgentItem(a.Id, a.Name)).ToList();

            _agentCombo.ItemsSource = items;

            var preferredIndex = preferredAgentId is { Length: > 0 } wanted
                ? items.FindIndex(a => a.Id == wanted)
                : -1;

            _agentCombo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : (items.Count > 0 ? 0 : -1);
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
            if (_openClawSelected)
            {
                await StartOpenClawClicked();
                return;
            }

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

        // OpenClaw's Start: no launch-and-wait-for-a-hook the way a local
        // CLI needs — sessions.create either hands back a real session right
        // away or it doesn't, so there's no "launched but no orb yet" middle
        // state the way a terminal spawn has. What the watch still has to
        // wait for is the scan discovering the new session and building its
        // orb — SessionManager.OrbFor is the only place that orb is ever
        // created, so this never builds its own (see OrbFor's own comment on
        // why that would be a genuine duplicate).
        private async Task StartOpenClawClicked()
        {
            if (_agentCombo.SelectedItem is not AgentItem agent)
            {
                _statusLine.Text = "Choose an agent first.";
                return;
            }

            _startButton.IsEnabled = false;
            _statusLine.Text = "Starting…";

            var (session, failure) = StartOpenClawConversationForTests is { } seam
                ? await seam(agent.Id, CancellationToken.None)
                : await OpenClawSessions.StartConversationAsync(agent.Id, CancellationToken.None);

            _startButton.IsEnabled = true;

            if (session is null)
            {
                _statusLine.Text = failure ?? "Couldn't start the conversation.";
                return;
            }

            _statusLine.Text = "Conversation started with " + agent.Name + ". Waiting for its orb…";
            StartOpenClawWatch(session);
        }

        private void StartWatch(HashSet<string> priorIds, NewChatCli cli, string folder)
        {
            _watchOpenClawSessionId = null;
            _watchOpenClawSession = null;
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);

            ArmTimer();
        }

        private void StartOpenClawWatch(IRemoteChatSession session)
        {
            _watchCli = null;
            _watchFolder = null;
            _watchPriorIds = null;
            _watchOpenClawSessionId = session.SessionId;
            _watchOpenClawSession = session;
            _watchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);

            ArmTimer();
        }

        private void ArmTimer()
        {
            _watchTimer?.Stop();
            _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watchTimer.Tick += OnWatchTick;
            _watchTimer.Start();
        }

        // Named rather than an inline lambda so a test can invoke it
        // directly with the (sender, EventArgs) shape a real DispatcherTimer
        // tick would use, the same pattern LocalCliChatSessionTests' own
        // comment describes for its debounce timer — no real ~20s wall-clock
        // wait needed to cover any branch below. Local-CLI and OpenClaw
        // watches are mutually exclusive (Start*Watch above clears the
        // other's fields), so exactly one of the two blocks below ever has
        // anything to check.
        internal void OnWatchTick(object? sender, EventArgs e)
        {
            if (_watchOpenClawSessionId is { } sessionId)
            {
                OnOpenClawWatchTick(sessionId);
                return;
            }

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

        private void OnOpenClawWatchTick(string sessionId)
        {
            if (OrbForSession(sessionId) is { } orb)
            {
                ChatPanel.OpenFor(orb, _watchOpenClawSession!);
                _statusLine.Text = "Its orb should be on screen.";
                _watchTimer?.Stop();
                return;
            }

            if (DateTime.UtcNow >= _watchDeadlineUtc)
            {
                _statusLine.Text = "Conversation created; no orb yet — check your gateway connection.";
                _watchTimer?.Stop();
            }
        }

        // Test seam: sets the watch's own fields without going through
        // StartClicked's full launch flow, so OnWatchTick's branches can be
        // driven directly.
        internal void ArmWatchForTests(HashSet<string> priorIds, NewChatCli cli, string folder, DateTime deadlineUtc)
        {
            _watchOpenClawSessionId = null;
            _watchOpenClawSession = null;
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = deadlineUtc;
        }

        internal void ArmOpenClawWatchForTests(IRemoteChatSession session, DateTime deadlineUtc)
        {
            _watchCli = null;
            _watchFolder = null;
            _watchPriorIds = null;
            _watchOpenClawSessionId = session.SessionId;
            _watchOpenClawSession = session;
            _watchDeadlineUtc = deadlineUtc;
        }
    }
}
