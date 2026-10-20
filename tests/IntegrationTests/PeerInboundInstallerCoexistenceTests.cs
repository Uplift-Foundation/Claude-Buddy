using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: the hook installers rewrite ~/.claude/settings.json. The inbound key
// is ours too, so an install (or uninstall) run after the user switched it on
// must not lose it, and our write must not disturb the installer's hooks.
//
// Real installer, real subprocess, scratch HOME: the macOS one on macOS (it needs
// osascript), the Windows one on Windows. The Windows leg is written but was NOT
// run from the Mac this was built on.
public class PeerInboundInstallerCoexistenceTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "install-macos-hooks.sh"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    private static (int Exit, string Output) Run(string exe, string[] args, Dictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env)
        {
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(120))) { p.Kill(true); Assert.Fail("installer hung"); }
        p.WaitForExit();
        return (p.ExitCode, o.Result + e.Result);
    }

    private static Dictionary<string, string?> Env(string home) => new()
    {
        ["HOME"] = home, ["USERPROFILE"] = home,
        ["LOCALAPPDATA"] = Path.Combine(home, "local"), ["APPDATA"] = Path.Combine(home, "roaming"),
        ["CODEX_HOME"] = null, ["GROK_HOME"] = null, ["CLAUDE_CONFIG_DIR"] = null,
        [BrandEnv.Name(BrandEnv.SettingsDir)] = null,
        [BrandEnv.LegacyPrefix + BrandEnv.SettingsDir] = null,
    };

    private static string Installed(string settingsPath) => File.ReadAllText(settingsPath);

    private static void AssertKeyAndHooks(string settingsPath)
    {
        var root = JsonNode.Parse(Installed(settingsPath))!.AsObject();
        Assert.Equal("accept", (string?)root[PeerInbound.SettingKey]);
        Assert.Equal("opus", (string?)root["model"]);
        Assert.NotNull(root["hooks"]);
        Assert.Contains("OrbweaverHook", root["hooks"]!.ToJsonString());
    }

    private static string Seed(string home)
    {
        var claude = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, "settings.json"), "{\n  \"model\": \"opus\"\n}\n");
        return claude;
    }

    [MacInstallerFact]
    public void MacInstallerKeepsTheKeyAndOurWriteKeepsTheHooks()
    {
        var home = Directory.CreateTempSubdirectory("orbweaver-coexist-").FullName;
        try
        {
            var claude = Seed(home);
            var settings = Path.Combine(claude, "settings.json");

            // key first, then installer
            Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(claude, true).Kind);
            var r = Run("/bin/bash", new[] { Path.Combine(RepoRoot, "tools", "install-macos-hooks.sh") }, Env(home));
            Assert.True(r.Exit == 0, r.Output);
            AssertKeyAndHooks(settings);

            // installer again (idempotent), key still there
            r = Run("/bin/bash", new[] { Path.Combine(RepoRoot, "tools", "install-macos-hooks.sh") }, Env(home));
            Assert.True(r.Exit == 0, r.Output);
            AssertKeyAndHooks(settings);

            // our write over an installed file keeps the hooks; turning it off removes only the key
            Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(claude, false).Kind);
            var after = JsonNode.Parse(Installed(settings))!.AsObject();
            Assert.False(after.ContainsKey(PeerInbound.SettingKey));
            Assert.Contains("OrbweaverHook", after["hooks"]!.ToJsonString());

            // uninstall with the key on leaves the key alone
            Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(claude, true).Kind);
            r = Run("/bin/bash", new[] { Path.Combine(RepoRoot, "tools", "install-macos-hooks.sh"), "--uninstall" }, Env(home));
            Assert.True(r.Exit == 0, r.Output);
            var un = JsonNode.Parse(Installed(settings))!.AsObject();
            Assert.Equal("accept", (string?)un[PeerInbound.SettingKey]);
            Assert.DoesNotContain("OrbweaverHook", un.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [WindowsFact]
    public void WindowsInstallerKeepsTheKey()
    {
        var home = Directory.CreateTempSubdirectory("orbweaver-coexist-").FullName;
        try
        {
            var claude = Seed(home);
            var settings = Path.Combine(claude, "settings.json");
            Assert.Equal(PeerInboundWriteKind.Written, PeerInboundSettingsFile.Apply(claude, true).Kind);

            var r = Run("powershell", new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(RepoRoot, "tools", "install-windows-hooks.ps1"),
            }, Env(home));

            Assert.True(r.Exit == 0, r.Output);
            AssertKeyAndHooks(settings);
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }
}
