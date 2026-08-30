using Xunit;

namespace ClaudeBuddy.Tests;

// What one crash entry says (CB-44).
//
// Worth asserting sentence by sentence, because the entire value of this file
// is that somebody reading it months later learns what happened. The two .ips
// reports this replaces contained a stack of question marks and a timestamp,
// and cost a day between them; an entry that named the exception would have
// cost a `cat`.
public class CrashLogFormatTests
{
    private static readonly DateTimeOffset When =
        new(2026, 8, 28, 18, 48, 40, TimeSpan.FromHours(-7));

    private static Exception Thrown(string message = "the calling thread cannot access this object")
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception caught)
        {
            // Thrown rather than constructed, so there is a real stack on it —
            // a constructed exception has StackTrace null, which is the arm
            // below rather than the ordinary case.
            return caught;
        }
    }

    [Fact]
    public void Names_when_where_and_what_on_the_first_two_lines()
    {
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());
        var lines = entry.Split('\n');

        Assert.StartsWith("=== 2026-08-28 18:48:40.000 -07:00", lines[0]);
        Assert.Contains("AppDomain.UnhandledException", lines[0]);
        Assert.Contains("pid " + Environment.ProcessId, lines[0]);

        Assert.Contains("System.InvalidOperationException", lines[1]);
        Assert.Contains("the calling thread cannot access this object", lines[1]);
    }

    [Fact]
    public void Carries_the_stack_when_there_is_one()
    {
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());

        Assert.Contains(nameof(CrashLogFormatTests), entry);
        Assert.Contains("   at ", entry);
    }

    [Fact]
    public void Survives_an_exception_that_was_never_thrown()
    {
        // A constructed exception has no stack. The entry still has to be
        // readable rather than ending mid-sentence.
        var entry = CrashLog.Format(When, "TaskScheduler.UnobservedTaskException",
            new TimeoutException("the relay never answered"));

        Assert.Contains("System.TimeoutException", entry);
        Assert.Contains("the relay never answered", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void Says_so_when_the_runtime_hands_over_something_that_is_not_an_exception()
    {
        // AppDomain.UnhandledException's ExceptionObject is typed `object`, and
        // a non-Exception is legal there. "Something went wrong and it was not
        // an Exception" is still more than the nothing this replaces.
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", error: null);

        Assert.Contains("AppDomain.UnhandledException", entry);
        Assert.Contains("no exception object", entry);
    }

    [Fact]
    public void Unwraps_the_inner_exception_that_usually_is_the_answer()
    {
        // TargetInvocationException over the real fault is the shape this app
        // hits most, and the outer one says nothing useful.
        var entry = CrashLog.Format(
            When,
            "Dispatcher.UnhandledException",
            new InvalidOperationException(
                "an error occurred",
                new UnauthorizedAccessException("Local Network permission")));

        Assert.Contains("System.InvalidOperationException: an error occurred", entry);
        Assert.Contains("---> System.UnauthorizedAccessException: Local Network permission", entry);
    }

    [Fact]
    public void Stops_unwrapping_rather_than_following_a_cycle_of_wrappers()
    {
        // Five deep is more than any real stack of wrappers and is a bound
        // rather than a judgement: an entry is worth nothing if writing it takes
        // long enough for the process to be killed first.
        Exception error = new InvalidOperationException("innermost");

        for (var i = 0; i < 40; i++) error = new InvalidOperationException("wrapper " + i, error);

        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", error);

        Assert.Equal(5, entry.Split("--->").Length - 1);
        Assert.DoesNotContain("innermost", entry);
    }

    [Fact]
    public void Starts_every_entry_with_its_own_marker()
    {
        // The file is append-only and holds several crashes, so an entry has to
        // be findable by eye. `===` at column zero, and everything else
        // indented, is the whole of that.
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());

        Assert.StartsWith("===", entry);
        Assert.All(
            entry.Split('\n').Skip(1).Where(l => l.Length > 0),
            line => Assert.StartsWith(" ", line));
    }
}
