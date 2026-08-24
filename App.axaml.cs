using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ClaudeBuddy
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // App.axaml declares the macOS theme, which is right for a Mac and
            // wrong for Windows. Fluent is Microsoft's own design language, so
            // it's the correct answer there — and restyling AppKit's controls by
            // hand was the alternative, which kept landing close-but-wrong
            // because their metrics and states aren't published anywhere to copy.
            if (!OperatingSystem.IsMacOS())
            {
                Styles.Clear();
                Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            }
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Prevent launching multiple buddies by accident.
                _singleInstanceMutex = new Mutex(true, "ClaudeBuddy_SingleInstance_Mutex", out bool isNew);
                if (!isNew)
                {
                    desktop.Shutdown();
                    return;
                }

                // Orb windows come and go with sessions; the app itself only
                // exits via the context menu.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                new SessionManager().Start();

                // Claude Desktop's URL schemes resolve to a bundle *id*, and
                // every tinted clone shares Claude Desktop's — so a sign-in
                // callback cannot say which profile it belongs to and always
                // lands in Default. Claude Buddy claims the schemes and
                // forwards each link to the right instance instead; see
                // ClaudeDesktopUrlRouting. A no-op off macOS, and a no-op with
                // fewer than two profiles, where there is nothing to route.
                ClaudeDesktopUrlRouter.Start();

                // The other half of that: the links themselves. Avalonia
                // surfaces macOS protocol activation here, so no Apple Event
                // plumbing of our own is needed.
                if (ApplicationLifetime is IActivatableLifetime activatable)
                {
                    activatable.Activated += (_, args) =>
                    {
                        if (args is ProtocolActivatedEventArgs protocol)
                        {
                            ClaudeDesktopUrlRouter.Handle(protocol.Uri.ToString());
                        }
                    };
                }

                // An upgrade leaves the previous release's speech engine on disk
                // and this build looking for its own. Speaking still works from
                // the old one, so nothing here is urgent — but the matching
                // engine has to be fetched by *something*, or the app runs a
                // steadily older engine until the user happens to toggle the
                // setting. This is that something, and it is a no-op unless the
                // feature is already enabled and already installed.
                //
                // The voice list is cached for the process lifetime and is built
                // by asking the engine, so it has to be dropped once a different
                // engine is in place — otherwise the picker keeps showing the old
                // engine's answer until restart.
                _ = NeuralSpeech.EnsureCurrentAsync()
                    .ContinueWith(_ => TextToSpeech.InvalidateVoiceCache(), TaskScheduler.Default);

                // Development entry point: `ClaudeBuddy --settings` opens the
                // settings window at launch. It is otherwise only reachable by
                // clicking the status-bar menu, which is awkward when the thing
                // being changed *is* that window.
                if (desktop.Args?.Contains("--settings") == true)
                {
                    SettingsWindow.Toggle();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
