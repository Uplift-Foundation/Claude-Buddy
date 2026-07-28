# Probe: can a packaged (MSIX) app be activated with a command line, unelevated?
#
# Claude Desktop on Windows ships as MSIX. Its payload is readable but not
# executable in place, and Invoke-CommandInDesktopPackage needs elevation — so
# the only remaining way to start it with a per-profile --user-data-dir is
# IApplicationActivationManager::ActivateApplication, which takes an arguments
# string. That interface is IUnknown-only (no IDispatch), so it can't be reached
# by PowerShell late binding; hence the inline C#.
#
# Throwaway probe, not part of the app. If it works, the real implementation is
# the same COM call from C# inside ClaudeBuddy.

param(
    [string] $Aumid = 'Claude_pzs8sxrjxfjjc!Claude',
    [Parameter(Mandatory = $true)] [string] $ProfileDir
)

$source = @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager
{
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        int options,
        out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
public class ApplicationActivationManagerClass { }

public static class PackagedLauncher
{
    public static uint Activate(string aumid, string arguments)
    {
        var manager = (IApplicationActivationManager)(object)new ApplicationActivationManagerClass();
        uint pid;
        var hr = manager.ActivateApplication(aumid, arguments, 0, out pid);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return pid;
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp

New-Item -ItemType Directory -Path $ProfileDir -Force | Out-Null

try {
    $pid_ = [PackagedLauncher]::Activate($Aumid, "--user-data-dir=$ProfileDir")
    Write-Output "ACTIVATED pid=$pid_"
}
catch {
    Write-Output "FAILED $($_.Exception.GetType().Name): $($_.Exception.Message)"
    exit 1
}

Start-Sleep -Seconds 18

$count = (Get-ChildItem -LiteralPath $ProfileDir -ErrorAction SilentlyContinue).Count
Write-Output "PROFILE_DIR_ENTRIES=$count"
Write-Output ("VERDICT=" + $(if ($count -gt 0) { 'ARGUMENTS_HONORED' } else { 'ARGUMENTS_IGNORED' }))
