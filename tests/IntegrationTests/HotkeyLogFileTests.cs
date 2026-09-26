using Xunit;

namespace ClaudeBuddy.Tests;

// hotkeys.log on a real disk: the line a lost hotkey collision leaves
// (CB-196), and the three rules it shares with persona.log — once per
// message, a ceiling, and never a throw.
//
// Scoped with CrashLog.ScopeForTests rather than CLAUDE_BUDDY_LOG_DIR, for
// the reason that method gives. HotkeyLog.Said is process-wide, but nothing
// else in this assembly writes to it, and one class's tests run one at a
// time, so no collection is needed.
public class HotkeyLogFileTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "cb-hotkey-log-" + Guid.NewGuid());
    private readonly IDisposable _logScope;

    public HotkeyLogFileTests()
    {
        _logScope = CrashLog.ScopeForTests(_logDir);
        HotkeyLog.ResetForTests();
    }

    public void Dispose()
    {
        _logScope.Dispose();
        HotkeyLog.ResetForTests();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string[] Lines() =>
        File.Exists(HotkeyLog.Path_) ? File.ReadAllLines(HotkeyLog.Path_) : Array.Empty<string>();

    [Fact]
    public void ANoteIsWrittenOnceBesideCrashLog()
    {
        HotkeyLog.Record("OpenNewChat: no hotkey registered");
        HotkeyLog.Record("OpenNewChat: no hotkey registered");

        Assert.Equal(Path.Combine(_logDir, "hotkeys.log"), HotkeyLog.Path_);
        var line = Assert.Single(Lines());
        Assert.EndsWith("  OpenNewChat: no hotkey registered", line);
    }

    [Fact]
    public void ADifferentNoteIsStillWritten()
    {
        HotkeyLog.Record("first");
        HotkeyLog.Record("second");

        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public void AFileAtTheCeilingIsNotAppendedTo()
    {
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(HotkeyLog.Path_, new string('x', (int)HotkeyLog.MaxBytes));

        HotkeyLog.Record("over the ceiling");

        Assert.Equal(HotkeyLog.MaxBytes, new FileInfo(HotkeyLog.Path_).Length);
    }

    [Fact]
    public void ALogDirectoryThatIsSomebodysFileIsSurvivedInSilence()
    {
        Directory.CreateDirectory(_logDir);
        var blocked = Path.Combine(_logDir, "not-a-directory");
        File.WriteAllText(blocked, "I am a file");
        using var onAFile = CrashLog.ScopeForTests(blocked);

        HotkeyLog.Record("a hotkey lost a collision");

        Assert.Equal("I am a file", File.ReadAllText(blocked));
    }
}
