using System.Text;

namespace Orbweaver
{
    internal enum PeerInboundWriteKind { Written, Unchanged, Refused, Failed }

    internal readonly record struct PeerInboundWrite(PeerInboundWriteKind Kind, string? Reason);

    // CB-267: reads and writes <root>/settings.json's crossSessionInbound.
    //
    // This edits a file Claude Code owns and the user may have hand-tuned, so the
    // bar is: touch nothing we cannot fully account for. Never create the root
    // (a missing root is a wrong path, not a fresh install), never write what we
    // could not parse, back up the original first, and replace the file by
    // moving a complete temp file over it so a crash leaves old or new, never
    // half. A symlinked settings.json (dotfile managers do this) stays a symlink:
    // the temp file goes next to the *target* and replaces the target.
    internal static class PeerInboundSettingsFile
    {
        internal const string BackupSuffix = ".orbweaver-backup";

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        internal static string PathFor(string root) => Path.Combine(root, "settings.json");

        // The file's text, or null when it is missing or cannot be read.
        internal static string? ReadText(string root)
        {
            try
            {
                var path = PathFor(root);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static PeerInboundWrite Apply(string root, bool accept) => ApplyCore(root, accept, null);

        // beforeCommit runs after the edit is computed and before the file is
        // replaced: the window in which another writer (Claude Code itself, an
        // installer) can change the file under us. Tests use it to be that writer.
        internal static PeerInboundWrite ApplyCore(string root, bool accept, Action<int>? beforeCommit)
        {
            if (!Directory.Exists(root))
                return Fail(PeerInboundWriteKind.Refused, $"{root} does not exist, so nothing was changed.");

            var path = PathFor(root);
            string target;
            try
            {
                // Null means "not a link" (a missing path throws instead, handled below), so the common case falls through to
                // the path itself. Resolving in one call rather than testing for
                // a link first leaves no window for the link to be swapped for a
                // file between the two.
                target = File.ResolveLinkTarget(path, true)?.FullName ?? path;
                if (target != path && !File.Exists(target))
                    return Fail(PeerInboundWriteKind.Refused, $"{path} is a link to a file that is not there, so it was left alone.");
            }
            catch (FileNotFoundException)
            {
                // Nothing at the path at all: the create-it case, no link to follow.
                target = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail(PeerInboundWriteKind.Failed, e.Message);
            }

            string? temp = null;
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var info = new FileInfo(target);
                    var existed = info.Exists;
                    long length = 0;
                    DateTime mtime = default;
                    string? text = null;
                    if (existed)
                    {
                        length = info.Length;
                        mtime = info.LastWriteTimeUtc;
                        try { text = StrictUtf8.GetString(File.ReadAllBytes(target)); }
                        catch (DecoderFallbackException)
                        {
                            return Fail(PeerInboundWriteKind.Refused, "settings.json is not valid UTF-8, so it was left alone.");
                        }
                    }

                    var edit = accept ? PeerInbound.Enable(text) : PeerInbound.Disable(text);
                    if (edit.Kind == SettingsEditKind.Unchanged)
                        return new PeerInboundWrite(PeerInboundWriteKind.Unchanged, null);
                    if (edit.Kind == SettingsEditKind.Refused)
                        return Fail(PeerInboundWriteKind.Refused, edit.Reason);

                    beforeCommit?.Invoke(attempt);

                    var now = new FileInfo(target);
                    var moved = now.Exists != existed
                        || (existed && (now.Length != length || now.LastWriteTimeUtc != mtime));
                    if (moved)
                    {
                        if (attempt == 0) continue;
                        return Fail(PeerInboundWriteKind.Failed, "settings.json kept changing while it was being edited, so nothing was written.");
                    }

                    var dir = Path.GetDirectoryName(target)!;
                    temp = Path.Combine(dir, $".settings.json.orbweaver-{Guid.NewGuid():N}.tmp");
                    File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(edit.Text!));

                    if (existed)
                    {
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
                        File.Copy(target, path + BackupSuffix, overwrite: true);
                    }

                    File.Move(temp, target, overwrite: existed);
                    temp = null;
                    return new PeerInboundWrite(PeerInboundWriteKind.Written, null);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail(PeerInboundWriteKind.Failed, e.Message);
            }
            finally
            {
                if (temp is not null)
                {
                    try { File.Delete(temp); } catch (Exception) { /* best effort; a leftover temp is harmless */ }
                }
            }
        }

        private static PeerInboundWrite Fail(PeerInboundWriteKind kind, string? reason) => new(kind, reason);
    }
}
