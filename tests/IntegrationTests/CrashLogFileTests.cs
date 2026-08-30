using Xunit;

namespace ClaudeBuddy.Tests;

// The crash log as a file on a real disk (CB-44).
//
// Here rather than in UnitTests because every claim worth making about it is
// about the filesystem: that the directory is created, that a second crash does
// not overwrite the first, that a runaway file rolls over instead of growing
// without bound, and — the one that matters most — that a write which cannot
// happen fails silently rather than throwing inside a process that is already
// dying.
public class CrashLogFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _was;

    public CrashLogFileTests()
    {
        _was = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        _dir = Path.Combine(Path.GetTempPath(), "cb-crashlog-" + Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _was);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Writes_the_entry_into_a_directory_it_creates()
    {
        // Nothing creates this directory at install time, and the first crash is
        // not the moment to discover that.
        Assert.False(Directory.Exists(_dir));

        CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("boom"));

        Assert.True(File.Exists(CrashLog.Path_));
        Assert.Contains("boom", File.ReadAllText(CrashLog.Path_));
    }

    [Fact]
    public void Keeps_the_earlier_crash_when_a_second_one_arrives()
    {
        // Append, not rewrite. A crash loop where each entry erased the last
        // would hide the first failure, which is the one that explains the rest.
        CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("first"));
        CrashLog.Record("Dispatcher.UnhandledException", new TimeoutException("second"));

        var text = File.ReadAllText(CrashLog.Path_);

        Assert.Contains("first", text);
        Assert.Contains("second", text);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal)
                  < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Rolls_the_file_over_once_it_is_big_enough_and_keeps_one_generation()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CrashLog.Path_, new string('x', (int)CrashLog.MaxBytes + 1));

        CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("after the roll"));

        // The oversized file is the previous generation now, and the live file
        // holds the new entry rather than a quarter-megabyte of history.
        Assert.True(File.Exists(CrashLog.PreviousPath));
        Assert.Contains("xxx", File.ReadAllText(CrashLog.PreviousPath));

        var current = File.ReadAllText(CrashLog.Path_);
        Assert.Contains("after the roll", current);
        Assert.DoesNotContain("xxx", current);
    }

    [Fact]
    public void Replaces_the_previous_generation_rather_than_piling_them_up()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CrashLog.PreviousPath, "an older crash nobody is reading");
        File.WriteAllText(CrashLog.Path_, new string('x', (int)CrashLog.MaxBytes + 1));

        CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("newest"));

        Assert.DoesNotContain("older crash", File.ReadAllText(CrashLog.PreviousPath));
        Assert.Single(Directory.GetFiles(_dir, "crash.log*"), f => f.EndsWith(".1", StringComparison.Ordinal));
    }

    [Fact]
    public void Says_nothing_and_throws_nothing_when_it_cannot_write_at_all()
    {
        // The rule this whole class exists for: a crash logger that throws turns
        // a diagnosable crash into a crash inside the diagnosis. A file where
        // the directory should be is the cheapest way to make the write
        // impossible on both platforms.
        var blocked = Path.Combine(Path.GetTempPath(), "cb-crashlog-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not a directory");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", Path.Combine(blocked, "logs"));

        try
        {
            CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("unwritable"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _dir);
            try { File.Delete(blocked); } catch { }
        }
    }

    [Fact]
    public void Survives_several_threads_crashing_at_once()
    {
        // Not far-fetched: an AppDomain handler and a dispatcher handler can
        // both fire while a faulted task is being collected, and a torn write or
        // a sharing violation there would lose the entry that explains the rest.
        Parallel.For(0, 24, i =>
            CrashLog.Record("AppDomain.UnhandledException", new InvalidOperationException("thread " + i)));

        var text = File.ReadAllText(CrashLog.Path_);

        Assert.Equal(24, text.Split("===").Length - 1);
    }
}
