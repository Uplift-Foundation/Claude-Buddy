namespace ClaudeBuddy
{
    // The little CLI mark an orb wears so Claude, Codex and Grok read apart
    // from across the room.
    //
    // A coloured disc with a white glyph, not a letter: the orb's own initials
    // already occupy the letter channel, and a "C" on a Claude orb would be
    // the session's name half the time. Colour is the long-range signal
    // (terracotta / green / black); the glyph is what you get when you look
    // closer. Bottom-left, because the other three corners are already spoken
    // for (presence, heart, kind).
    //
    // Hidden for OpenClaw and remote-control orbs: those already have a kind
    // badge, and a fourth logo on a gateway session would be a mark on almost
    // every remaining orb, which is the thing KindBadge's own comment exists
    // to prevent.
    internal readonly record struct CliMarkStyle(string Name, string FillHex, string GlyphPath);

    internal static class CliMark
    {
        // 22 DIP, against the kind badge's 16. The kind glyphs are 8–9pt
        // punctuation and were sized for that; a logo at 16px is the size the
        // user asked us not to ship. 22 is big enough to read from the other
        // side of the desk and still leaves the centred initials alone.
        internal const double Size = 22;

        // The white Path inside the disc. Scaled with the disc on a team
        // member so it does not overflow the smaller badge.
        internal const double GlyphSize = 14;

        // Anthropic terracotta, a four-point spark. The spark is the mark
        // Claude Code's own UI uses; the colour is what you see first.
        internal static readonly CliMarkStyle Claude = new(
            "claude",
            "#D97757",
            "M8,1.2 L9.15,6.55 L14.8,8 L9.15,9.45 L8,14.8 L6.85,9.45 L1.2,8 L6.85,6.55 Z");

        // OpenAI green, a six-point star. Distinct from Claude's four-point
        // spark at a glance, and green-on-black is the pair Codex's own TUI
        // already sits on.
        internal static readonly CliMarkStyle Codex = new(
            "codex",
            "#10A37F",
            "M8,1.4 L8.95,6.2 L13.8,4.2 L10.1,8 L13.8,11.8 L8.95,9.8 L8,14.6 "
            + "L7.05,9.8 L2.2,11.8 L5.9,8 L2.2,4.2 L7.05,6.2 Z");

        // Near-black, a heavy X. Grok/xAI's mark is an X; at 22px a four-point
        // star rotated 45° is the same idea and does not collapse into Claude's
        // spark.
        internal static readonly CliMarkStyle Grok = new(
            "grok",
            "#1A1A1A",
            "M3.2,2.4 L8,7.2 L12.8,2.4 L13.6,3.2 L8.8,8 L13.6,12.8 L12.8,13.6 "
            + "L8,8.8 L3.2,13.6 L2.4,12.8 L7.2,8 L2.4,3.2 Z");

        internal static CliMarkStyle? For(SessionSource source) => source switch
        {
            SessionSource.ClaudeCode => Claude,
            SessionSource.Codex => Codex,
            SessionSource.Grok => Grok,
            _ => null
        };

        // Every account source is a CLI, so this is never null — unlike
        // SessionSource, which has OpenClaw and remote-control orbs that
        // already wear a kind badge instead.
        internal static CliMarkStyle For(AccountUsageSource source) => source switch
        {
            AccountUsageSource.Codex => Codex,
            AccountUsageSource.Grok => Grok,
            _ => Claude
        };
    }
}
