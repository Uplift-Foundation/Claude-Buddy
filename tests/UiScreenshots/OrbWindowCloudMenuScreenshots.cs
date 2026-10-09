using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-225: a cloud orb's right-click menu with its Archive and Delete rows, in
// the states a reviewer needs to see — offered, Delete armed with its
// can't-be-undone warning, and a refusal written on the row in the
// endpoint's terms.
//
// The real menu on a shown orb, opened and captured the way
// OrbWindowOpenClawMenuScreenshots does it and for the reasons it gives — the
// headless platform hosts a context menu inside its window, so the window is
// widened for the capture alone. The action is a fake; no endpoint is asked.
//
// [Collection("Settings")]: the rows are offered only with cloud sessions
// switched on, which is a process-wide setting.
[Collection("Settings")]
public class OrbWindowCloudMenuScreenshots
{
    private static OrbWindow CloudOrb(Func<Task<CloudLifecycleResult>>? answer = null)
    {
        var orb = new OrbWindow("cloud:session_01abc")
        {
            CloudAction = (_, _, _) => answer?.Invoke()
                ?? Task.FromResult(new CloudLifecycleResult(CloudLifecycleVerdict.Done)),
        };

        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCloud,
            Kind = SessionKind.Cloud,
            State = "idle",
            Title = "Refactor the parser",
            Cwd = "",
            Url = "https://claude.ai/code/session_01abc",
        });

        return orb;
    }

    private static void CaptureMenu(OrbWindow orb, string fileName)
    {
        // Wider than the OpenClaw capture's 360: a refusal sentence on these
        // rows runs longer, and a capture that clipped it would be a picture of
        // a defect the real popup, which sizes itself, does not have.
        orb.Show();
        orb.Width = 460;
        orb.Height = 560;
        ScreenshotHelper.Flush();

        var root = orb.FindControl<Grid>("Root")!;
        var menu = root.ContextMenu!;
        menu.Open(root);
        ScreenshotHelper.Flush();

        try
        {
            ScreenshotHelper.CaptureControl(menu, fileName);
        }
        finally
        {
            menu.Close();
        }
    }

    private static async Task WithCloudOn(Func<Task> body)
    {
        var was = OrbweaverSettings.ClaudeCloudEnabled;
        try
        {
            OrbweaverSettings.ClaudeCloudEnabled = true;
            await body();
        }
        finally
        {
            OrbweaverSettings.ClaudeCloudEnabled = was;
        }
    }

    [AvaloniaFact]
    public Task ACloudOrbsMenuOffersArchiveAndDelete() => WithCloudOn(() =>
    {
        CaptureMenu(CloudOrb(), "orb-menu-cloud.png");
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public Task TheDeleteRowArmedSaysItCannotBeUndone() => WithCloudOn(async () =>
    {
        var orb = CloudOrb();
        await orb.CloudActionClickAsync(CloudLifecycleAction.Delete);

        CaptureMenu(orb, "orb-menu-cloud-delete-armed.png");
    });

    [AvaloniaFact]
    public Task ARefusedArchiveShowsWhyOnTheRow() => WithCloudOn(async () =>
    {
        var orb = CloudOrb(() => Task.FromResult(
            new CloudLifecycleResult(CloudLifecycleVerdict.Refused, CloudOutcomes.AccountBlockedDetail)));

        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);
        await orb.CloudActionClickAsync(CloudLifecycleAction.Archive);

        CaptureMenu(orb, "orb-menu-cloud-archive-refused.png");
    });
}
