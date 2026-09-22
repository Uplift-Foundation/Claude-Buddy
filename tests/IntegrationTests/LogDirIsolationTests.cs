using Xunit;

namespace ClaudeBuddy.Tests;

// The property that replaced an enumeration (CB — see CrashLog.ScopeForTests).
//
// Three pull requests in a row tried to stop CrashLogFileTests racing over
// CLAUDE_BUDDY_LOG_DIR by listing the test classes that can reach
// PersonaFiles.Reject and moving each into one xUnit collection. Each of the
// three found a class the previous one had missed, twice because the list was
// built by searching for an API name and the call is two frames deep. A list
// that has to be rebuilt correctly every time somebody writes a test, and that
// fails silently when it is not, is not a fix.
//
// So the claim under test here is not "these classes are isolated" — that is
// the claim that kept going stale. It is the stronger one the scope actually
// buys: **a scratch log directory opened as a scope cannot be created by code
// running outside that flow, whoever writes it and whatever it reaches.** If
// that holds, a new persona-resolving test needs no audit, because forgetting
// to isolate can no longer touch anybody's directory.
//
// Each case below is paired with its own negative control, on the same
// directory and the same writer, differing only in which seam is used. Without
// the pair, a green test here is equally consistent with the writer simply not
// having written anything — which is the shape of confident-negative this
// repository has been bitten by before. The controls are the assertions that
// prove the harness works at all.
[Collection("LogDir")]
public class LogDirIsolationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-logdir-isolation-" + Guid.NewGuid().ToString("N"));

    private readonly string? _envWas;

    public LogDirIsolationTests()
    {
        Directory.CreateDirectory(_root);
        _envWas = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _envWas);
        PersonaLog.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A thread with execution-context flow suppressed: the closest thing to
    // "another test class, running in parallel, in its own flow". Nothing an
    // AsyncLocal holds here is visible in there.
    private static void OnAnUnrelatedFlow(Action work)
    {
        Exception? failed = null;
        Thread thread;

        using (ExecutionContext.SuppressFlow())
        {
            thread = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { failed = e; }
            });
            thread.Start();
        }

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the unrelated flow did not finish");
        if (failed is not null) throw failed;
    }

    [Fact]
    public void A_scoped_log_directory_is_invisible_to_a_writer_on_another_flow()
    {
        var mine = Path.Combine(_root, "scoped");
        var theirs = Path.Combine(_root, "env");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", theirs);

        using (CrashLog.ScopeForTests(mine))
        {
            Assert.Equal(mine, CrashLog.Directory);

            // The exact transitive write the whole defect is made of, performed
            // by somebody who never heard of this test.
            OnAnUnrelatedFlow(() => PersonaLog.Record("a picture was ignored, from elsewhere"));
        }

        // This is the assertion CrashLogFileTests needs to be able to make.
        Assert.False(Directory.Exists(mine));

        // ...and the control, which is what says the writer really did write:
        // it landed on the environment variable, the shared floor nothing
        // asserts about, exactly where a forgetful test is meant to end up.
        Assert.True(Directory.Exists(theirs));
    }

    [Fact]
    public void The_environment_variable_alone_is_the_control_and_it_does_race()
    {
        // The negative control for the case above, and the reproduction of the
        // original flake in three lines. Same directory, same writer, same
        // unrelated flow — the only difference is that the directory was
        // published through the process-wide variable instead of a scope, and
        // that is enough for a stranger to create it.
        var mine = Path.Combine(_root, "published");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", mine);

        Assert.False(Directory.Exists(mine));

        OnAnUnrelatedFlow(() => PersonaLog.Record("a picture was ignored, from elsewhere too"));

        // CrashLogFileTests.Writes_the_entry_into_a_directory_it_creates
        // asserted the opposite of this, about a directory it had published the
        // same way. That is the whole flake, deterministic rather than 1-in-18.
        Assert.True(Directory.Exists(mine));
    }

    [Fact]
    public void A_scope_wins_over_the_variable_and_puts_it_back_afterwards()
    {
        var env = Path.Combine(_root, "from-env");
        var outer = Path.Combine(_root, "outer");
        var inner = Path.Combine(_root, "inner");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", env);
        Assert.Equal(env, CrashLog.Directory);

        using (CrashLog.ScopeForTests(outer))
        {
            Assert.Equal(outer, CrashLog.Directory);

            // Nesting restores the class's own directory rather than dropping
            // to the variable — PersonaRealFileTests points one case at an
            // unwritable path and the cases after it must not inherit that.
            using (CrashLog.ScopeForTests(inner)) Assert.Equal(inner, CrashLog.Directory);

            Assert.Equal(outer, CrashLog.Directory);
        }

        Assert.Equal(env, CrashLog.Directory);
    }

    [Fact]
    public void A_flow_that_scopes_nothing_still_gets_the_variable_not_the_real_log_directory()
    {
        // The forgetful case, stated as a test so it stays true: a class that
        // sets up no isolation at all writes to the assembly-wide scratch
        // directory TestBootstrap points the variable at, never to the
        // developer's own ~/Library/Logs/ClaudeBuddy.
        var floor = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");

        Assert.False(string.IsNullOrEmpty(floor));
        Assert.Equal(floor, CrashLog.Directory);
        Assert.StartsWith(Path.GetTempPath(), CrashLog.Directory, StringComparison.Ordinal);
    }
}

// The single fact the whole design rests on, pinned as its own class because
// it cannot be asserted from inside LogDirIsolationTests above.
//
// Every class converted to CrashLog.ScopeForTests opens its scope in a
// *constructor* and does its writing and reading in a *test method*. If xUnit
// did not flow execution context between the two, CrashLog.Directory would
// quietly fall back to the environment-variable floor — and almost every
// converted test would still pass, because the write and the read would both
// use the floor and agree with each other. A green suite is therefore not
// evidence that the flow works; it is consistent with the seam being inert.
//
// This class is the assertion that separates those two worlds. Its constructor
// opens a scope and nothing else, and its cases assert that the scope is what
// CrashLog.Directory reports — synchronously, after an await, and on a pooled
// thread. If xUnit ever stops flowing the constructor's context, this goes red
// here rather than surfacing as a flake somewhere else three weeks later.
[Collection("LogDir")]
public class LogDirScopeFlowsFromConstructorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-logdir-flow-" + Guid.NewGuid().ToString("N"));

    private readonly IDisposable _scope;

    public LogDirScopeFlowsFromConstructorTests() => _scope = CrashLog.ScopeForTests(_dir);

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void The_constructors_scope_is_visible_in_the_test_method()
    {
        Assert.Equal(_dir, CrashLog.Directory);

        // ...and it is genuinely the scope rather than a coincidence: the
        // environment variable says something else entirely.
        Assert.NotEqual(_dir, Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR"));
    }

    [Fact]
    public async Task It_survives_an_await()
    {
        Assert.Equal(_dir, CrashLog.Directory);
        await Task.Yield();
        Assert.Equal(_dir, CrashLog.Directory);

        await Task.Delay(1);
        Assert.Equal(_dir, CrashLog.Directory);
    }

    [Fact]
    public async Task It_reaches_work_the_test_hands_to_the_thread_pool()
    {
        // CrashLog.Record is called from background work in the app and from
        // Parallel.For in CrashLogFileTests, so the scope has to survive the
        // hop. It does, because execution context flows into pooled work
        // unless somebody suppresses it — which is exactly what the foreign
        // flow in LogDirIsolationTests does on purpose.
        Assert.Equal(_dir, await Task.Run(() => CrashLog.Directory));
    }
}
