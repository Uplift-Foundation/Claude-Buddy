using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.UiTests;

// RemoteControlSessions' three remaining decisions: which sessions the orb scan
// sees, when a session going busy is worth telling anyone about, and when an
// unused relay has sat long enough to be shut down.
//
// Here rather than in tests/UnitTests because RaiseWorkingTransitions delivers
// through Dispatcher.UIThread.Post — it runs on the poll thread and the panel it
// notifies is a control.
//
// The relay table and the working-transition memory are process-wide statics, so
// this runs on the settings lane and clears both around every case.
[Collection("Settings")]
public class RemoteControlTransitionTests : IDisposable
{
    public RemoteControlTransitionTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        RemoteControlSessions.ResetForTests();
    }

    private static RemoteControlSessions.Remote Remote(
        string name, string status, string account = "work@example.com") =>
        new(Name: name, Ref: "bridge:session_01", Status: status,
            Seen: DateTime.UtcNow, Account: account);

    // ---- what counts as working ------------------------------------------

    // "running" is the word the peer list actually uses. The other two are
    // tolerance rather than observation — the file says so, and says it is
    // exactly the mistake this repo's fixture rule exists to prevent: taking a
    // vocabulary from the wrong source instead of from the output being parsed.
    [Fact]
    public void TheStatusWordsThatMeanBusy()
    {
        Assert.True(Remote("nova", "running").Working);
        Assert.True(Remote("nova", "busy").Working);
        Assert.True(Remote("nova", "working").Working);
        Assert.False(Remote("nova", "idle").Working);
        Assert.False(Remote("nova", "").Working);
    }

    [Fact]
    public void TheStatusMatchIsCaseInsensitive()
    {
        Assert.True(Remote("nova", "RUNNING").Working);
        Assert.True(Remote("nova", "Working on it").Working);
    }

    // ---- transitions -----------------------------------------------------

    // ---- Republish -------------------------------------------------------

    [Fact]
    public void RepublishWithNoRelaysLeavesAnEmptyList()
    {
        RemoteControlSessions.Republish();

        Assert.Empty(RemoteControlSessions.SnapshotForTests);
    }

    // ---- the idle shutdown ----------------------------------------------

    // ---- a colour learned after the fact -----------------------------------

}
