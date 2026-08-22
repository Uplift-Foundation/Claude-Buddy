using System.Diagnostics;

namespace ClaudeBuddy
{
    // Opens a picture from a conversation in whatever this machine uses to look
    // at pictures.
    //
    // Deliberately not a viewer of our own. What is wanted from a full-size view
    // — pinch to zoom, pan, fill the screen, rotate, copy, save somewhere — is
    // everything Preview already does properly and by muscle memory, and a
    // hand-rolled Avalonia version would be a worse copy of the first two of
    // those. The app's job here is to hand over a file.
    internal static class OpenClawMedia
    {
        // Alongside the status files rather than in Application Support: these
        // are copies of something the gateway already holds, they are only
        // meaningful while the viewer is open, and the temp directory is where
        // the operating system expects to clean up after us.
        private static string Directory_ =>
            Path.Combine(Path.GetTempPath(), "claude_buddy_media");

        public static void Open(byte[] bytes, string name)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                Sweep();

                var path = Path.Combine(Directory_, SafeName(name));
                File.WriteAllBytes(path, bytes);

                if (OperatingSystem.IsMacOS())
                {
                    // /usr/bin/open, absolute: launched from Finder this app has
                    // no Homebrew PATH, the same trap BackgroundJobs documents.
                    Process.Start(new ProcessStartInfo("/usr/bin/open", $"\"{path}\"")
                    {
                        UseShellExecute = false
                    });

                    return;
                }

                // Windows and anything else: let the shell decide what opens it.
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Nothing to fall back to and nothing worth interrupting the
                // conversation for — the picture is still in the panel.
            }
        }

        // The gateway's own filename, which is what makes the viewer's title bar
        // say something useful, minus anything that could point the write
        // somewhere other than here.
        private static string SafeName(string name)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? "image.png" : Path.GetFileName(name);

            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                trimmed = trimmed.Replace(bad, '-');
            }

            if (trimmed.Length == 0) trimmed = "image.png";

            // Long enough for a generated name with its uuid, short enough to
            // stay inside a filename limit with room to spare.
            return trimmed.Length > 120 ? trimmed[^120..] : trimmed;
        }

        // Anything from a previous sitting. Deleting on the way in rather than
        // on the way out, because there is no reliable "on the way out" — the
        // app can be killed, and a viewer may still have the file open.
        private static void Sweep()
        {
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);

                foreach (var file in System.IO.Directory.EnumerateFiles(Directory_))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
            }
            catch
            {
                // A file we can't delete is one something else is using.
            }
        }
    }
}
