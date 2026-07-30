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
