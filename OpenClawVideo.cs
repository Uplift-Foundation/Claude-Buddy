using System.Text.Json;

namespace Orbweaver
{
    // CB-272: a video an OpenClaw agent posts, shown in the chat panel as a
    // tile rather than played inline.
    //
    // The decision was made before this file existed and is not reopened here:
    // the panel draws a clickable tile sized to the clip, with a play glyph and
    // its duration, and a click fetches the bytes and hands them to the OS's
    // default player through OpenClawMedia.Open. Inline playback would mean
    // shipping a video decoder into a menu-bar app, and the system player does
    // everything a viewer needs already — the same argument OpenClawMedia makes
    // for pictures.
    //
    // **A separate kind, never a widened image list.** The picture path decodes
    // a bitmap out of whatever bytes it is handed, and Avalonia's decoder does
    // not throw on rubbish (see TurnView.DecodeAtDrawWidth), so video bytes
    // reaching it would not fail loudly — they would produce a plausible garbage
    // bitmap. So a video turn never reaches LoadImageBytes: ChatTurn.IsVideo is
    // what routes it, and OpenClawSessions.ImageExtensions is untouched.
    //
    // Pure where it can be — the extension rule, the meta parse, the duration
    // text and the tile's proportions are all one function call away from a
    // test. The three delegates at the bottom are the only seams with the
    // outside world, and exist so a headless test can drive a tile and a click
    // without a gateway or a real process launch.
    internal static class OpenClawVideo
    {
        internal static readonly string[] Extensions = { ".mp4", ".mov", ".webm", ".m4v" };

        internal static bool HasVideoExtension(string text) =>
            Array.Exists(Extensions, ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Whether a path names a video, by extension alone. This is what
        // ChatTurn.IsVideo reads off ImageSourcePath, rather than a flag every
        // producer and every one of the turn-copying sites would have to carry
        // beside the path: the path is already set with the route by each
        // producer, so the kind cannot drift from it.
        internal static bool IsVideoPath(string? path) =>
            !string.IsNullOrEmpty(path) && HasVideoExtension(path!);

        // The gateway's `&meta=1` answer for a servable clip, e.g.
        //
        //   {"available":true,"mimeType":"video/mp4","sizeBytes":1042813,
        //    "playback":"native","durationMs":12750,"width":544,"height":800,
        //    "mediaTicket":"..."}
        //
        // Every field but `available` is optional: a gateway that cannot probe
        // a clip says so by leaving the numbers out, and the tile then falls
        // back to a default shape and no duration rather than a made-up one.
        internal readonly record struct Meta(
            bool Available, long? DurationMs, int? Width, int? Height);

        // Null when there is no answer at all (the gateway did not reply, or
        // replied with something that is not a JSON object) — distinct from an
        // answer of available:false, so a caller can tell "couldn't ask" from
        // "asked and refused". Both then explain themselves through
        // OpenClawMediaRefusal, which does its own parse of the same text.
        internal static Meta? ParseMeta(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var available = root.TryGetProperty("available", out var a)
                                && a.ValueKind == JsonValueKind.True;

                return new Meta(
                    available,
                    LongOrNull(root, "durationMs"),
                    (int?)LongOrNull(root, "width"),
                    (int?)LongOrNull(root, "height"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static long? LongOrNull(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n)
            && n >= 0
                ? n
                : null;

        // m:ss, the way a player shows it — "0:13" for 12.75 seconds, rounded
        // to the nearest second rather than truncated so a 12.75s clip does not
        // read as shorter than the one the player will report. Minutes are not
        // wrapped into hours: a chat clip over an hour is not a case worth a
        // second format, and "75:00" is still true.
        //
        // Null for an unknown or zero duration, which draws no label at all
        // rather than "0:00" claiming a clip with nothing in it.
        internal static string? FormatDuration(long? durationMs)
        {
            if (durationMs is not > 0) return null;

            var seconds = (long)Math.Round(durationMs.Value / 1000.0, MidpointRounding.AwayFromZero);
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        // The tile's width, matching the 228 a picture is drawn at so a clip
        // and a picture in the same conversation line up.
        internal const double TileWidth = 228;

        // The tile's height for a clip of this shape, at TileWidth. A portrait
        // 544x800 clip is a tall tile and a 16:9 one a wide, short tile —
        // which is the point of asking the gateway for the dimensions at all —
        // clamped so an extreme clip (a banner, a phone-held sliver) cannot
        // make a tile too thin to carry its glyph or tall enough to push the
        // whole conversation off screen. Unknown dimensions get 16:9, the
        // overwhelmingly common shape, rather than a square that would be
        // wrong for nearly every clip.
        internal static double TileHeight(int? width, int? height)
        {
            const double MinHeight = 96;
            const double MaxHeight = 320;

            if (width is not > 0 || height is not > 0) return TileWidth * 9 / 16;

            var h = TileWidth * height.Value / width.Value;
            return Math.Clamp(h, MinHeight, MaxHeight);
        }

        // ---- seams with the outside world ----
        //
        // Static and replaceable for the same reason OpenClawSessions' own
        // fetchers are static: TurnView is built by the panel, not handed its
        // collaborators. Tests swap these and put them back; every test that
        // does is in the "Settings" collection, which serialises it, so one
        // test's fake can never be another's gateway.

        internal static Func<string, CancellationToken, Task<string?>> FetchMeta =
            OpenClawSessions.FetchLocalMediaMetaAsync;

        internal static Func<string, CancellationToken, Task<byte[]?>> FetchBytes =
            OpenClawSessions.FetchMediaAsync;

        internal static Action<byte[], string> Open = OpenClawMedia.Open;
    }
}
