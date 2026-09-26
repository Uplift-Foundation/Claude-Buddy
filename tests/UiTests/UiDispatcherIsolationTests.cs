using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Pins CB-183's fix: this assembly runs every [AvaloniaFact] against one
// dispatcher, so a pool thread can never be handed the UI thread between tests.
// See the AvaloniaTestIsolation attribute in TestAppBuilder.cs for the race
// itself.
//
// Under the default PerTest isolation, the two facts sharing the static below
// cannot both pass: whichever runs second sees a dispatcher the reset built
// after the first one finished. Neither depends on which of them runs first.
public class UiDispatcherIsolationTests
{
    private static Dispatcher? s_firstSeen;

    [Fact]
    public void The_assembly_asks_for_one_dispatcher_for_every_test()
    {
        var attribute = typeof(ClaudeBuddy.Tests.TestAppBuilder).Assembly
            .GetCustomAttribute<AvaloniaTestIsolationAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AvaloniaTestIsolationLevel.PerAssembly, attribute!.IsolationLevel);
    }

    [AvaloniaFact]
    public void First_test_to_look_sees_the_same_dispatcher_as_the_other()
        => AssertSameDispatcherAsEveryEarlierTest();

    [AvaloniaFact]
    public void Second_test_to_look_sees_the_same_dispatcher_as_the_other()
        => AssertSameDispatcherAsEveryEarlierTest();

    [AvaloniaFact]
    public async Task A_pool_thread_asking_for_the_ui_thread_is_given_this_one()
    {
        // The race's other half, from the thread that used to win it. A pool
        // thread reading Dispatcher.UIThread must get the dispatcher this test
        // body runs on, not a new one owned by itself.
        var mine = Dispatcher.UIThread;

        var (seen, poolOwnsIt) = await Task.Run(
            () => (Dispatcher.UIThread, Dispatcher.UIThread.CheckAccess()));

        Assert.Same(mine, seen);
        Assert.False(poolOwnsIt);
        Assert.True(Dispatcher.UIThread.CheckAccess());
    }

    private static void AssertSameDispatcherAsEveryEarlierTest()
    {
        var current = Dispatcher.UIThread;
        var first = Interlocked.CompareExchange(ref s_firstSeen, current, null) ?? current;

        Assert.Same(first, current);
        Assert.True(current.CheckAccess());
    }
}
