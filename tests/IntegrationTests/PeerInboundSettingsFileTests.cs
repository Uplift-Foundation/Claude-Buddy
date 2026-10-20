using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: PeerInboundSettingsFile against real files in a scratch directory.
// Nothing here points at ~/.claude: every root is a fresh temp directory.
public sealed class PeerInboundSettingsFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("orbweaver-peerinbound-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Settings => PeerInboundSettingsFile.PathFor(_root);
    private string Backup => Settings + PeerInboundSettingsFile.BackupSuffix;

    private const string Sample = "{\n  \"model\": \"opus\",\n  \"permissions\": { \"defaultMode\": \"bypassPermissions\" },\n  \"ratio\": 1.0\n}\n";

    private string[] Leftovers() =>
        Directory.GetFileSystemEntries(_root)
            .Select(Path.GetFileName)
            .Where(n => n!.EndsWith(".tmp", StringComparison.Ordinal))
            .ToArray()!;

    [Fact]
    public void PathForIsSettingsJsonInTheRoot() =>
        Assert.Equal(Path.Combine("a", "settings.json"), PeerInboundSettingsFile.PathFor("a"));

    [Fact]
    public void ReadTextIsNullForAMissingFileAndTheTextOtherwise()
    {
        Assert.Null(PeerInboundSettingsFile.ReadText(_root));
        File.WriteAllText(Settings, Sample);
        Assert.Equal(Sample, PeerInboundSettingsFile.ReadText(_root));
    }

    [Fact]
    public void ReadTextIsNullWhenTheRootIsNotReadable()
    {
        // A directory where settings.json should be: File.Exists is false for it.
        Directory.CreateDirectory(Settings);
        Assert.Null(PeerInboundSettingsFile.ReadText(_root));
    }

    [Fact]
    public void EnableWritesTheKeyAndLeavesAByteIdenticalBackup()
    {
        File.WriteAllText(Settings, Sample);
        var before = File.ReadAllBytes(Settings);

        var result = PeerInboundSettingsFile.Apply(_root, accept: true);

        Assert.Equal(PeerInboundWriteKind.Written, result.Kind);
        Assert.Equal(before, File.ReadAllBytes(Backup));
        Assert.Equal(InboundSetting.Accepting, PeerInbound.ReadSetting(File.ReadAllText(Settings)));
        Assert.Contains("\"ratio\": 1.0", File.ReadAllText(Settings));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void EnablingTwiceWritesNothingTheSecondTime()
    {
        File.WriteAllText(Settings, Sample);
        Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(_root, true).Kind);
        File.Delete(Backup);
        var after = File.ReadAllBytes(Settings);
        var mtime = File.GetLastWriteTimeUtc(Settings);

        var second = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Unchanged, second.Kind);
        Assert.Equal(after, File.ReadAllBytes(Settings));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(Settings));
        Assert.False(File.Exists(Backup));
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[1]")]
    public void ARefusalLeavesTheFileAndTheDirectoryExactlyAsFound(string text)
    {
        File.WriteAllText(Settings, text);
        File.SetLastWriteTimeUtc(Settings, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var bytes = File.ReadAllBytes(Settings);
        var mtime = File.GetLastWriteTimeUtc(Settings);

        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Refused, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(Settings));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileThatIsNotUtf8IsRefused()
    {
        File.WriteAllBytes(Settings, new byte[] { (byte)'{', 0xFF, 0xFE, (byte)'}' });

        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Refused, result.Kind);
        Assert.Contains("UTF-8", result.Reason);
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void AMissingFileIsCreatedWithOnlyTheKeyAndNoBackup()
    {
        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Written, result.Kind);
        var obj = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        Assert.Single(obj);
        Assert.Equal("accept", (string?)obj[PeerInbound.SettingKey]);
        Assert.False(File.Exists(Backup));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void DisablingWithNoFileDoesNothing()
    {
        Assert.Equal(PeerInboundWriteKind.Unchanged, PeerInboundSettingsFile.Apply(_root, false).Kind);
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public void ARootThatDoesNotExistIsRefusedAndNotCreated()
    {
        var missing = Path.Combine(_root, "nope", "deeper");

        var result = PeerInboundSettingsFile.Apply(missing, true);

        Assert.Equal(PeerInboundWriteKind.Refused, result.Kind);
        Assert.Contains(missing, result.Reason);
        Assert.False(Directory.Exists(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void EnableThenDisableRestoresTheOriginalDocument()
    {
        File.WriteAllText(Settings, Sample);

        Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(_root, true).Kind);
        Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(_root, false).Kind);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Sample), JsonNode.Parse(File.ReadAllText(Settings))));
        Assert.Equal(PeerInboundWriteKind.Unchanged, PeerInboundSettingsFile.Apply(_root, false).Kind);
    }

    [Fact]
    public void ATextEditorBomIsDroppedNotDoubled()
    {
        File.WriteAllBytes(Settings, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{}\n")).ToArray());

        Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(_root, true).Kind);

        var bytes = File.ReadAllBytes(Settings);
        Assert.NotEqual((byte)0xEF, bytes[0]);
        Assert.Equal(InboundSetting.Accepting, PeerInbound.ReadSetting(File.ReadAllText(Settings)));
    }

    [UnixFact]
    public void ASymlinkedSettingsFileStaysASymlinkAndItsTargetIsEdited()
    {
        var real = Path.Combine(_root, "dotfiles");
        Directory.CreateDirectory(real);
        var target = Path.Combine(real, "claude-settings.json");
        File.WriteAllText(target, Sample);
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var link = PeerInboundSettingsFile.PathFor(home);
        File.CreateSymbolicLink(link, target);

        var result = PeerInboundSettingsFile.Apply(home, true);

        Assert.Equal(PeerInboundWriteKind.Written, result.Kind);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(InboundSetting.Accepting, PeerInbound.ReadSetting(File.ReadAllText(target)));
        Assert.Equal(Sample, File.ReadAllText(link + PeerInboundSettingsFile.BackupSuffix));
        Assert.Empty(Directory.GetFiles(real, "*.tmp"));
        Assert.Empty(Directory.GetFiles(home, "*.tmp"));
    }

    [UnixFact]
    public void ADanglingSymlinkIsRefused()
    {
        File.CreateSymbolicLink(Settings, Path.Combine(_root, "not-there.json"));

        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Refused, result.Kind);
        Assert.Contains("link", result.Reason);
        Assert.False(File.Exists(Path.Combine(_root, "not-there.json")));
    }

    [UnixFact]
    public void ASymlinkLoopFailsWithAReasonInsteadOfThrowing()
    {
        File.CreateSymbolicLink(Settings, Settings);

        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Failed, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [UnixFact]
    public void FilePermissionsSurviveTheReplace()
    {
        File.WriteAllText(Settings, Sample);
        File.SetUnixFileMode(Settings, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(_root, true).Kind);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Settings));
    }

    // ---- another writer changes the file under us ----------------------------------

    [Fact]
    public void AChangeBetweenReadAndWriteIsRereadOnceAndTheNewContentIsKept()
    {
        File.WriteAllText(Settings, Sample);
        var calls = new List<int>();

        var result = PeerInboundSettingsFile.ApplyCore(_root, true, attempt =>
        {
            calls.Add(attempt);
            if (attempt == 0) File.WriteAllText(Settings, "{\"other\":\"writer, longer than before\"}\n");
        });

        Assert.Equal(PeerInboundWriteKind.Written, result.Kind);
        Assert.Equal(new[] { 0, 1 }, calls);
        var obj = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        Assert.Equal("writer, longer than before", (string?)obj["other"]);
        Assert.Equal("accept", (string?)obj[PeerInbound.SettingKey]);
    }

    [Fact]
    public void AFileThatKeepsChangingFailsInsteadOfWritingOverIt()
    {
        File.WriteAllText(Settings, Sample);
        var n = 0;

        var result = PeerInboundSettingsFile.ApplyCore(_root, true,
            _ => File.WriteAllText(Settings, "{\"n\":\"" + new string('x', 10 + ++n) + "\"}\n"));

        Assert.Equal(PeerInboundWriteKind.Failed, result.Kind);
        Assert.Contains("changing", result.Reason);
        Assert.Contains("xxxx", File.ReadAllText(Settings));
        Assert.DoesNotContain(PeerInbound.SettingKey, File.ReadAllText(Settings));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void AFileAppearingWhereNoneWasIsTreatedAsAChange()
    {
        var result = PeerInboundSettingsFile.ApplyCore(_root, true, attempt =>
        {
            if (attempt == 0) File.WriteAllText(Settings, "{\"mine\":true}\n");
        });

        Assert.Equal(PeerInboundWriteKind.Written, result.Kind);
        var obj = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        Assert.True((bool)obj["mine"]!);
        Assert.Equal("accept", (string?)obj[PeerInbound.SettingKey]);
    }

    // ---- failures ---------------------------------------------------------------------

    // Measured on macOS (not assumed): .NET maps FileShare.None to an advisory
    // flock on Unix too, and our own read honours it, so a held file fails the
    // same way it does on Windows rather than being written over. The plan
    // expected Written here; the measurement said otherwise.
    [Fact]
    public void AFileHeldOpenWithoutSharingFailsWithAReasonAndWritesNothing()
    {
        File.WriteAllText(Settings, Sample);
        using var hold = new FileStream(Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = PeerInboundSettingsFile.Apply(_root, true);

        Assert.Equal(PeerInboundWriteKind.Failed, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(Leftovers());
        Assert.False(File.Exists(Backup));
    }

    [UnixFact]
    public void AnUnwritableDirectoryFailsAndLeavesNoTempFile()
    {
        if (Environment.UserName == "root") return; // root ignores mode bits
        File.WriteAllText(Settings, Sample);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = PeerInboundSettingsFile.Apply(_root, true);

            Assert.Equal(PeerInboundWriteKind.Failed, result.Kind);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.Equal(Sample, File.ReadAllText(Settings));
            Assert.Empty(Leftovers());
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [UnixFact]
    public void AnUnreadableSettingsFileFailsWithoutTouchingIt()
    {
        if (Environment.UserName == "root") return;
        File.WriteAllText(Settings, Sample);
        File.SetUnixFileMode(Settings, UnixFileMode.None);
        try
        {
            var result = PeerInboundSettingsFile.Apply(_root, true);

            Assert.Equal(PeerInboundWriteKind.Failed, result.Kind);
            Assert.Null(PeerInboundSettingsFile.ReadText(_root));
            Assert.False(File.Exists(Backup));
        }
        finally
        {
            File.SetUnixFileMode(Settings, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [UnixFact]
    public void AnUnstattableRootEntryFailsRatherThanThrows()
    {
        // settings.json inside a directory we cannot search: stat itself throws.
        var inner = Path.Combine(_root, "locked");
        Directory.CreateDirectory(inner);
        File.WriteAllText(PeerInboundSettingsFile.PathFor(inner), Sample);
        if (Environment.UserName == "root") return;
        File.SetUnixFileMode(inner, UnixFileMode.None);
        try
        {
            var result = PeerInboundSettingsFile.Apply(inner, true);
            Assert.True(result.Kind is PeerInboundWriteKind.Failed or PeerInboundWriteKind.Refused, result.ToString());
        }
        finally
        {
            File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
