using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// The two rules the hygiene sweep rests on. What is on the other side of them is
// File.Delete against a file the app does not own, so both are worth stating
// case by case.
//
// The sweep exists because nothing but the SessionEnd hook's own `rm -f` ever
// deleted a status file, and SessionEnd only fires on a graceful exit — so a
// Ctrl+C'd session's file stayed for good, and a finished background job's
// stayed *and could never be caught*, because its pooled worker is kept alive on
// purpose and the pid answers forever. Six dead-pid files and one per finished
// job were in a real status directory when this was written.
public class SweepRulesTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    // --- EvidenceOfDeath -----------------------------------------------------

    // The process that wrote the file has exited. The Ctrl+C case, which fires
    // no SessionEnd.
    // Looped rather than a [Theory] throughout this file: both JobPhase and
    // SessionManager.ScanVerdict are internal, and a public xUnit test method
    // may not take an internal parameter type.
    [Fact]
    public void AGoneProcessIsEvidenceWhateverTheDaemonSays()
    {
        foreach (var phase in new[] { JobPhase.NotAJob, JobPhase.Unknown })
        {
            Assert.True(SessionPresence.EvidenceOfDeath(
                SessionManager.ScanVerdict.ProcessGone, phase));
        }
    }

    // The other half, and the one no liveness rule can ever reach: a finished
    // job's worker stays alive by design, so its pid keeps answering and its
    // verdict is about having no terminal rather than about being over.
    [Fact]
    public void AFinishedJobIsEvidenceWhateverVerdictItsOrbGot()
    {
        var verdicts = new[]
        {
            SessionManager.ScanVerdict.Keep,
            SessionManager.ScanVerdict.NoTerminal,
            SessionManager.ScanVerdict.NotALiveJob,
            SessionManager.ScanVerdict.Expired,
            SessionManager.ScanVerdict.Superseded,
        };

        foreach (var verdict in verdicts)
        {
            Assert.True(SessionPresence.EvidenceOfDeath(verdict, JobPhase.Done));
        }
    }

    // Everything else is a statement about *us*, not about the session, and
    // sweeping on any of it would delete a live session's file:
    //
    // - Expired is the user's own "Keep orbs for" setting. A quiet session is
    //   still a session, and its file is the only place its terminal
    //   coordinates and its colour live.
    // - NoTerminal and NotALiveJob are about whether a click could go anywhere.
    // - Superseded is about which of several files for one live process is the
    //   current one — and an Agent View pid legitimately hosts several live
    //   sessions at once, so it says nothing about any of them being over.
    // - Unknown means the CLI could not be asked at all.
    [Fact]
    public void NoOtherReasonForDroppingAnOrbIsEvidenceOfDeath()
    {
        var cases = new (SessionManager.ScanVerdict Verdict, JobPhase Phase)[]
        {
            (SessionManager.ScanVerdict.Expired, JobPhase.NotAJob),
            (SessionManager.ScanVerdict.Expired, JobPhase.Parked),
            (SessionManager.ScanVerdict.NoTerminal, JobPhase.NotAJob),
            (SessionManager.ScanVerdict.NotALiveJob, JobPhase.NotAJob),
            (SessionManager.ScanVerdict.NotALiveJob, JobPhase.Unknown),
            (SessionManager.ScanVerdict.Superseded, JobPhase.Working),
            (SessionManager.ScanVerdict.Superseded, JobPhase.Unknown),
            (SessionManager.ScanVerdict.Keep, JobPhase.Parked),
            (SessionManager.ScanVerdict.Keep, JobPhase.Unknown),
            (SessionManager.ScanVerdict.Keep, JobPhase.NotAJob),
        };

        foreach (var (verdict, phase) in cases)
        {
            Assert.False(SessionPresence.EvidenceOfDeath(verdict, phase));
        }
    }

    // The third fact, and like the other two it is a statement about the
    // session: its own transcript records the turn being handed to a
    // background job, and nothing has happened in it since. The file it
    // deletes is one no hook will ever write again — the conversation fires
    // its hooks under the fork's session id now — so without this the husk
    // outlived the "Keep orbs for = Forever" setting indefinitely.
    [Fact]
    public void AHandedOffHuskIsEvidenceWhateverTheDaemonSays()
    {
        foreach (var phase in new[] { JobPhase.NotAJob, JobPhase.Unknown })
        {
            Assert.True(SessionPresence.EvidenceOfDeath(
                SessionManager.ScanVerdict.Backgrounded, phase));
        }
    }

    // --- SweepDue ------------------------------------------------------------

    [Fact]
    public void TheGracePeriodHasToActuallyElapse()
    {
        var grace = TimeSpan.FromMinutes(10);

        Assert.False(SessionPresence.SweepDue(Now, Now, grace));
        Assert.False(SessionPresence.SweepDue(Now - TimeSpan.FromMinutes(9.5), Now, grace));

        // Exactly at the boundary counts as due, unlike the lifetime timer's
        // strict `>`. Nothing rides on which way this one goes — the scan runs
        // every two seconds either way — and `>=` is what makes a grace of zero
        // mean "the next scan", which is the only way a test can reach the
        // delete without waiting ten minutes for it.
        Assert.True(SessionPresence.SweepDue(Now - grace, Now, grace));
        Assert.True(SessionPresence.SweepDue(Now - TimeSpan.FromHours(3), Now, grace));
    }

    [Fact]
    public void AGraceOfZeroIsDueOnTheNextSightingRatherThanTheFirst()
    {
        // The scan records the moment it first saw the evidence and only checks
        // this on a *later* pass, so even a zero grace needs the evidence to
        // survive two consecutive scans. Asserted here as the boundary it is.
        Assert.True(SessionPresence.SweepDue(Now, Now, TimeSpan.Zero));
    }

    // A clock that has gone backwards — an NTP correction, a laptop waking with
    // a corrected time — reads as "not due" rather than as a very old file.
    // Wrong in the direction that keeps a file rather than deleting one.
    [Fact]
    public void AClockThatWentBackwardsDoesNotSweepAnything()
    {
        Assert.False(SessionPresence.SweepDue(
            Now + TimeSpan.FromHours(1), Now, TimeSpan.FromMinutes(10)));
    }
}
