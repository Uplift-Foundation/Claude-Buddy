using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers which credential store each platform uses.
//
// The platform is an argument rather than something SourceFor asks the runtime,
// for the same reason OrbGlyph takes the two-letter setting instead of reading
// it: a decision that reads its own environment can only be tested on the
// machine that happens to agree with it, and CI runs both legs. This way the
// Windows arm is verified on a Mac and the macOS arm on Windows.
//
// Nothing here touches the real Keychain or a real credential file. Constructing
// KeychainCredentialSource does not query anything — the query happens in
// Stamp() and Read(), which these tests do not call.
public class ClaudeCloudCredentialPlatformTests
{
    private static string Home => Path.Combine("/Users", "someone");

    [Fact]
    public void MacOsUsesTheKeychain()
    {
        Assert.IsType<KeychainCredentialSource>(
            ClaudeCliCredentials.SourceFor(isMacOS: true, Home));
    }

    [Fact]
    public void EverywhereElseUsesTheCredentialsFile()
    {
        var source = ClaudeCliCredentials.SourceFor(isMacOS: false, Home);

        var file = Assert.IsType<FileCredentialSource>(source);
        Assert.Equal(Path.Combine(Home, ".claude", ".credentials.json"), file.Path);
    }

    // **Inside the config root, unlike UsageAccounts' `.claude.json`, which is a
    // sibling of it.** The two files are neighbours with different rules, and
    // reasoning from one to the other is a trap that has already cost this
    // repository once — see UsageAccountsTests for the version of it that
    // produces no error at all.
    [Fact]
    public void TheCredentialsFileLivesInsideTheConfigRoot()
    {
        var root = Path.Combine(Home, ".claude-work");

        Assert.Equal(Path.Combine(root, ".credentials.json"),
            ClaudeCliCredentials.CredentialsFilePath(root));
    }
}
