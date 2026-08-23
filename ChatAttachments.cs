using Avalonia.Media.Imaging;

namespace ClaudeBuddy
{
    // Where a picture pasted into the chat panel is written before its path
    // is typed into a terminal — the outgoing counterpart of OpenClawMedia,
    // which writes a picture *out of* a conversation so something can open
    // it. This writes one *into* one, as a plain file on disk, because that
    // is the one thing every local CLI already knows how to read out of a
    // dropped path.
    internal static class ChatAttachments
    {
        // Alongside OpenClawMedia's own directory but not inside it: that one
        // is swept as pictures the *gateway* sent, and mixing a picture on
        // its way out with the ones already opened would let one sweep
        // delete a file a CLI hasn't read yet.
        private static string Directory_ =>
            Path.Combine(Path.GetTempPath(), "claude_buddy_pasted_images");

        // Saves a pasted bitmap and returns its path. PNG regardless of
        // whatever the pasteboard's own format was — Bitmap.Save always
        // writes PNG, and a second lossless encode costs nothing a paste
        // needs to be fast against.
        public static string Save(Bitmap bitmap)
        {
            System.IO.Directory.CreateDirectory(Directory_);
            Sweep();

            var path = Path.Combine(Directory_, $"paste-{Guid.NewGuid():N}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            return path;
        }

        // Anything from a previous sitting. Swept on the way in rather than
        // the way out, the same reasoning as OpenClawMedia: there is no
        // reliable "on the way out" once the app can simply be killed.
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
                // A file we can't delete is one still being read.
            }
        }
    }
}
