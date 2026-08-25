using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Xml;
using Microsoft.Win32;

namespace ClaudeBuddy
{
    // Resolves Claude Desktop's AUMID (Application User Model ID) without
    // hardcoding it, so a reinstall under a different publisher id or a
    // rename doesn't quietly strand the launcher.
    //
    // An AUMID is "<PackageFamilyName>!<AppId>". Neither half is stored
    // together anywhere convenient, and there's no supported way to enumerate
    // installed packages without going through WinRT (Windows.Management.
    // Deployment.PackageManager), which this project doesn't reference and
    // isn't worth the TFM change for one lookup. Both halves are readable
    // straight from disk instead:
    //
    //   - The package repository registry key lists every installed package
    //     by its *full* name (Name_Version_Arch_ResourceId_PublisherId) and
    //     gives PackageRootFolder, the install directory — readable without
    //     elevation even though listing WindowsApps itself is access-denied
    //     (verified on this machine: Get-ChildItem on the folder is refused,
    //     reading a specific file inside it by path is not).
    //   - The family name is Name + "_" + PublisherId, i.e. the full name
    //     with the version/architecture/resource-id segments dropped — no
    //     hashing needed, those are already the same PublisherId string.
    //   - The AppId comes from that package's AppxManifest.xml, whose
    //     <Application Id="..."> is what ActivateApplication expects.
    [SupportedOSPlatform("windows")]
    // Excluded from coverage, as a class. Every member reads the Windows registry
    // — the AppModel package repository, to find where a Store-installed
    // application actually lives — and three already carried the attribute
    // individually. What was left uncovered was the cache fields they share,
    // which exist only because those members do.
    //
    // Marking the class rather than the members is also what makes those fields
    // countable at all: a field initializer belongs to the type initializer, and
    // with beforefieldinit the runtime does not run it until a static field is
    // touched — which nothing outside the excluded members ever does.
    [ExcludeFromCodeCoverage]
    internal static class WindowsAppLookup
    {
        private const string PackageRepositoryKey =
            @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        // The package Name segment (before the first underscore in the full
        // name) that identifies Claude Desktop, as opposed to some unrelated
        // vendor's package that also starts with "Claude".
        private const string PackageName = "Claude";

        // Re-resolved at most this often: cheap enough that a stale null
        // (app not installed yet, or a previous resolution failed) doesn't
        // linger, without re-touching the registry and a manifest file on
        // every poll while everything is fine.
        private const long CacheMs = 30_000;

        private static readonly object Gate = new();
        private static string? _cached;
        private static long _cachedAt = long.MinValue;

        // Excluded from coverage: caches the registry lookup below.
        [ExcludeFromCodeCoverage]
        public static string? ResolveAumid()
        {
            if (!OperatingSystem.IsWindows()) return null;

            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (_cached is not null && now - _cachedAt < CacheMs) return _cached;
            }

            var resolved = Resolve();

            lock (Gate)
            {
                _cached = resolved;
                _cachedAt = now;
            }

            return resolved;
        }

        // Excluded from coverage: opens HKEY_CLASSES_ROOT and reads an
        // AppxManifest.xml from under Program Files\WindowsApps.
        [ExcludeFromCodeCoverage]
        private static string? Resolve()
        {
            try
            {
                using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
                using var packages = classesRoot.OpenSubKey(PackageRepositoryKey);
                if (packages is null) return null;

                foreach (var fullName in packages.GetSubKeyNames())
                {
                    var nameEnd = fullName.IndexOf('_');
                    if (nameEnd <= 0 || !string.Equals(fullName[..nameEnd], PackageName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var entry = packages.OpenSubKey(fullName);
                    var root = entry?.GetValue("PackageRootFolder") as string;
                    if (string.IsNullOrEmpty(root)) continue;

                    var appId = ReadAppId(Path.Combine(root, "AppxManifest.xml"));
                    if (appId is null) continue;

                    var familyName = FamilyNameFromFullName(fullName);
                    if (familyName is null) continue;

                    return $"{familyName}!{appId}";
                }
            }
            catch
            {
                // Falls through to null — same as "not installed".
            }

            return null;
        }

        // Full name is Name_Version_Architecture_ResourceId_PublisherId; the
        // family name drops everything but Name and PublisherId, which are
        // always the first and last underscore-separated segments.
        // internal, not private: a package family name is what
        // ActivateApplication is handed, so getting it wrong means launching
        // nothing with no error to show. Pure string work, and the only part of
        // this file that is.
        internal static string? FamilyNameFromFullName(string fullName)
        {
            var parts = fullName.Split('_');
            return parts.Length >= 2 ? $"{parts[0]}_{parts[^1]}" : null;
        }

        // Excluded from coverage: loads a real AppxManifest.xml from disk.
        [ExcludeFromCodeCoverage]
        private static string? ReadAppId(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath)) return null;

                using var stream = File.OpenRead(manifestPath);
                var document = new XmlDocument();
                document.Load(stream);

                var namespaces = new XmlNamespaceManager(document.NameTable);
                namespaces.AddNamespace("a", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");

                var node = document.SelectSingleNode("//a:Applications/a:Application", namespaces)
                           ?? document.SelectSingleNode("//*[local-name()='Applications']/*[local-name()='Application']");

                return node?.Attributes?["Id"]?.Value is { Length: > 0 } id ? id : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
