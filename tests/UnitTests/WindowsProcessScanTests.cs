using Xunit;

namespace ClaudeBuddy.Tests
{
    // Which Win32_Process rows are a running Claude Desktop instance.
    //
    // Three rules, each of which fails quietly. Missing an instance offers
    // Launch for a profile that is already running; inventing one offers Focus
    // for a window that does not exist; and reading the profile directory off the
    // wrong process reports Default as a real profile match.
    //
    // The file's own header comment is the source for every case here, including
    // the measurement it rests on: verified against a live Default-profile
    // instance whose main process command line carries no arguments at all, while
    // its children all carry an explicit --user-data-dir pointing at the same
    // resolved default directory.
    //
    // CA1416 is suppressed for the same reason as WindowsAppLookupTests: the
    // class-level [SupportedOSPlatform("windows")] is about the WMI and process
    // work elsewhere in the file, and this function is string matching that runs
    // on either platform. Gating it would leave the rules untested on the leg
    // where nobody can check them by running the app.
#pragma warning disable CA1416
    public class WindowsProcessScanTests
    {
        private const string Packaged =
            @"C:\Program Files\WindowsApps\AnthropicClaude_0.14.5.0_x64__4mzk8j1sv1ndm\claude.exe";

        // --- the packaged-path rule ---

        [Fact]
        public void ThePackagedExecutableIsAnInstance()
        {
            var instance = WindowsProcessScan.MapRow(Packaged, "", 4242);

            Assert.NotNull(instance);
            Assert.Equal(4242, instance!.Value.Pid);
        }

        // claude.exe is also the name of the Claude Code CLI, installed under
        // %USERPROFILE%\.local\bin. Matching on process name alone would report
        // every CLI invocation as a Desktop instance — which is the whole reason
        // the marker exists.
        [Theory]
        [InlineData(@"C:\Users\warren\.local\bin\claude.exe")]
        [InlineData(@"C:\dev\claude-desktop\out\claude.exe")]
        [InlineData("")]
        [InlineData(null)]
        public void AnythingOutsideWindowsAppsIsNotAnInstance(string? path)
        {
            Assert.Null(WindowsProcessScan.MapRow(path, "", 1));
        }

        // The marker is matched case-insensitively, because a path read back off
        // WMI is not guaranteed to keep the casing of the folder on disk.
        [Fact]
        public void TheMarkerIsMatchedWithoutRegardToCase()
        {
            Assert.NotNull(WindowsProcessScan.MapRow(
                @"C:\program files\windowsapps\AnthropicClaude_x64__abc\claude.exe", "", 1));
        }

        // --- the main-process rule ---

        // Electron's children share the parent's executable path and carry
        // --type=, so without this filter one instance reads as five.
        [Theory]
        [InlineData("--type=renderer --user-data-dir=C:\\p")]
        [InlineData("--type=gpu-process")]
        [InlineData("\"claude.exe\" --type=utility --user-data-dir=C:\\p")]
        public void AChildProcessIsNotAnInstance(string commandLine)
        {
            Assert.Null(WindowsProcessScan.MapRow(Packaged, commandLine, 1));
        }

        // --- the profile-directory rule ---

        [Fact]
        public void AnUnquotedProfileDirectoryIsRead()
        {
            var instance = WindowsProcessScan.MapRow(
                Packaged, @"--user-data-dir=C:\Users\warren\AppData\Roaming\Claude-work", 7);

            Assert.Equal(@"C:\Users\warren\AppData\Roaming\Claude-work", instance!.Value.UserDataDir);
        }

        // A quoted value is the case that matters, because a real profile path
        // contains spaces. Reading it unquoted would truncate at the first one
        // and report a directory that does not exist.
        [Fact]
        public void AQuotedProfileDirectoryKeepsItsSpaces()
        {
            var instance = WindowsProcessScan.MapRow(
                Packaged, @"--user-data-dir=""C:\Users\warren\Application Support\Claude""", 7);

            Assert.Equal(@"C:\Users\warren\Application Support\Claude", instance!.Value.UserDataDir);
        }

        // No flag at all means the default profile, read off the *main* process —
        // which is the measurement the file's comment records, and the reason the
        // --type= filter has to come first. Null is how Default is represented.
        [Fact]
        public void NoFlagOnTheMainProcessMeansTheDefaultProfile()
        {
            Assert.Null(WindowsProcessScan.MapRow(Packaged, "", 7)!.Value.UserDataDir);
        }

        // An empty value is Default too, rather than a profile whose directory is
        // the empty string.
        [Fact]
        public void AnEmptyProfileDirectoryIsTheDefaultProfile()
        {
            Assert.Null(WindowsProcessScan.MapRow(Packaged, @"--user-data-dir=""""", 7)!.Value.UserDataDir);
        }

        [Fact]
        public void TheFlagIsFoundAmongOtherArguments()
        {
            var instance = WindowsProcessScan.MapRow(
                Packaged,
                @"""claude.exe"" --no-sandbox --user-data-dir=C:\p --disable-gpu",
                7);

            Assert.Equal(@"C:\p", instance!.Value.UserDataDir);
        }

        // A row that is an instance always carries its pid through unchanged —
        // it is what Focus and Quit are aimed at.
        [Fact]
        public void ThePidIsCarriedThrough()
        {
            Assert.Equal(31337, WindowsProcessScan.MapRow(Packaged, "", 31337)!.Value.Pid);
        }
    }
#pragma warning restore CA1416
}
