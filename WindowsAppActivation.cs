using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Starts a packaged (MSIX) app with a command line, unelevated, from the
    // interactive desktop session — the real implementation of what
    // tools/windows-activate-test.ps1 probed. A packaged app's payload isn't
    // directly executable, and Invoke-CommandInDesktopPackage needs
    // elevation, so IApplicationActivationManager::ActivateApplication is the
    // only remaining route. It's IUnknown-only (no IDispatch), which is why
    // this needs to be a COM interop call rather than anything reachable from
    // managed script.
    //
    // Confirmed E_ACCESSDENIED from a non-interactive window station (an SSH
    // session) and working from the interactive one — this class has no way
    // to tell those apart itself, so a caller seeing a failure here should
    // rule that out before concluding the feature is broken.
    [ExcludeFromCodeCoverage]
    internal static class WindowsAppActivation
    {
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            int ActivateApplication(
                [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments,
                int options,
                out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManagerClass
        {
        }

        // arguments is the Chromium-style command-line tail, e.g.
        // "--user-data-dir=C:\...", or "" to launch with none.
        public static bool TryActivate(string appUserModelId, string arguments, out uint processId)
        {
            processId = 0;
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                var manager = (IApplicationActivationManager)(object)new ApplicationActivationManagerClass();
                return manager.ActivateApplication(appUserModelId, arguments, 0, out processId) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
