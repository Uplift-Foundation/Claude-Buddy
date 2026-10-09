namespace Orbweaver.Tests;

// A settings directory of the test's own for as long as it is held, and the
// previous one back afterwards.
//
// ClaudeBuddySettings is one process-wide static, and ORBWEAVER_SETTINGS_DIR
// is the only thing that decides which file it reads. A class that writes
// settings without its own directory writes into whatever the last class left
// pointed at; one that reloads without its own directory reads whatever that
// was. The pair of those hung the suite: SettingsWindowRowTests left Grok usage
// switched on in the shared file, SettingsVoicePreviewTests picked it up, and
// the Settings window it then showed contained the one help text headless
// Avalonia could not lay out (see SettingsWindow.HelpText). Restoring on
// Dispose is what keeps a scoped class from becoming the next leak itself.
internal sealed class ScopedSettingsDir : IDisposable
{
    private const string Variable = "ORBWEAVER_SETTINGS_DIR";
    private readonly string? _previous;

    public ScopedSettingsDir(string purpose)
    {
        _previous = Environment.GetEnvironmentVariable(Variable);
        Dir = Path.Combine(Path.GetTempPath(), $"cb-{purpose}-" + Guid.NewGuid());
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable(Variable, Dir);
        OrbweaverSettings.ReloadForTests();
    }

    public string Dir { get; }

    public void Dispose()
    {
        OrbweaverSettings.FlushPendingSave();
        Environment.SetEnvironmentVariable(Variable, _previous);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}
