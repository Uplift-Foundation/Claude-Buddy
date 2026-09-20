using System.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ClaudeBuddy
{
    // What the speaker reads: the whole reply, or two or three sentences of it.
    //
    // The motivation is length. A reply that takes minutes to read aloud is not
    // something anyone listens to, so the speaker is most useful for knowing
    // *what happened* without reading — and that is a different artefact from
    // the reply itself, not a shorter rendering of it.
    internal enum SpeakScope
    {
        // Everything the assistant said, which is what this app has always done
        // and remains the default. Nobody's speaker changes character on
        // upgrade.
        Full,

        // The requester's own phrase, kept rather than sanded down to "Summary":
        // it says what the mode is for better than the neutral word does.
        Summary,
    }

    // The decision about *which text* to speak, with no process, no settings and
    // no speech engine behind it.
    //
    // Split out of ChatPanel.SpeakLatest deliberately. The utterance itself is
    // excluded from coverage — TextToSpeech.Speak "starts a speech engine and
    // makes the machine make a noise" — and leaving the choice inside the method
    // that utters means the choice is excluded along with it. Pure, it gets a
    // case per outcome.
    internal static class SpeechPlan
    {
        // What to do about one reply: the text to work from, and whether it has
        // to be summarised first.
        internal sealed record Plan(string? Text, bool NeedsSummary)
        {
            // Nothing to say. Distinct from a plan whose Text is empty only in
            // that callers read this rather than re-deriving it.
            internal bool Silent => string.IsNullOrWhiteSpace(Text);
        }

        internal static readonly Plan Nothing = new(null, false);

        // Below this, summarising costs more than it saves.
        //
        // The round trip was measured at 6.3-7.3 seconds across four samples on
        // one machine. A reply already about as long as the summary would be
        // therefore buys several seconds of silence in exchange for nothing, and
        // the mode would read as broken on exactly the short replies where it is
        // least needed. 400 characters is roughly three spoken sentences.
        //
        // Deliberately a character count rather than a sentence count: sentence
        // splitting on model output is its own guessing game (abbreviations,
        // code, ellipses), and being approximately right here costs one
        // avoidable round trip at worst.
        internal const int ShortEnoughChars = 400;

        internal static Plan For(string? lastAssistantText, SpeakScope scope)
        {
            if (string.IsNullOrWhiteSpace(lastAssistantText)) return Nothing;

            if (scope == SpeakScope.Full) return new Plan(lastAssistantText, false);

            // Trimmed before measuring: a reply padded with blank lines is not a
            // long reply, and the whitespace is not spoken either way.
            var trimmed = lastAssistantText.Trim();
            return trimmed.Length <= ShortEnoughChars
                ? new Plan(trimmed, false)
                : new Plan(trimmed, true);
        }
    }

    // Producing those two or three sentences.
    //
    // **It does not ask the user's own session.** The CLI is right there and
    // already holds the context, which makes it the tempting answer and the
    // wrong one: it would inject a turn into the conversation this very panel is
    // displaying, spend the user's tokens and context, and appear in their
    // history as something they did not say. For a feature whose whole point is
    // to be ambient, that is close to disqualifying.
    //
    // So this is a separate, throwaway `claude -p` — a different process, a
    // different conversation, nothing written into the user's transcript. It
    // needs no credential of its own, which is the reason this shape wins over a
    // direct API call: the CLI is already authenticated, and UsagePoller has
    // established the pattern of spawning it for an account-scoped answer rather
    // than this app ever holding a token.
    internal static class SpeechSummary
    {
        // Haiku, explicitly. The job is compression, not reasoning, and the
        // latency is the whole design constraint — this runs between a reply
        // landing and the first spoken word.
        internal const string Model = "haiku";

        // Generous against the 6.3-7.3s measured, and for the same reason
        // UsagePoller's is: the machines most likely to be slow are the ones
        // where the answer matters. Past this the mode says so rather than
        // waiting further.
        internal const int TimeoutMs = 30000;

        // A bound on what is sent, not on what comes back. A very long reply is
        // exactly the case this mode exists for, but the tail of one adds little
        // to a three-sentence summary and costs latency on the leg that is
        // already the bottleneck.
        internal const int MaxSourceChars = 24000;

        // Pure, so the wording is assertable without spawning anything. The
        // instruction is blunt about form because the output is spoken, not
        // read: a preamble ("Here's a summary:") is three wasted seconds of
        // audio, and markdown is read aloud as punctuation.
        internal static string Prompt(string reply)
        {
            var source = reply.Length > MaxSourceChars
                ? reply[..MaxSourceChars]
                : reply;

            return "Summarise the following assistant reply in two or three sentences, "
                + "for someone who will hear it read aloud rather than read it.\n\n"
                + "Say what was done or found, not what the reply is about. "
                + "No preamble, no heading, no markdown, no bullet points, no code. "
                + "Plain sentences only.\n\n"
                + "----\n" + source;
        }

        // Model output is not a summary until the preamble it was told not to
        // write has been taken off it anyway. Pure, and lenient: anything this
        // does not recognise is passed through rather than mangled, because a
        // slightly untidy summary is a far better outcome than an empty one.
        internal static string? Clean(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var text = output.Trim();

            // A fenced block: the instruction said no code, and a model that
            // wrapped its prose in one anyway would otherwise have the fence
            // spoken as "backtick backtick backtick".
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..];
                if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
                text = text.Trim();
            }

            // The preamble it was told not to write. Only removed when a colon
            // ends it within the first line and something follows, so a summary
            // whose own first sentence happens to contain a colon survives.
            const string marker = ":";
            var newline = text.IndexOf('\n');
            var firstLine = newline < 0 ? text : text[..newline];
            if (firstLine.Length <= 40
                && firstLine.EndsWith(marker, StringComparison.Ordinal)
                && newline >= 0
                && !string.IsNullOrWhiteSpace(text[(newline + 1)..]))
            {
                text = text[(newline + 1)..].Trim();
            }

            // Spoken, so the paragraph breaks are not information. Collapsing
            // them keeps the engines from pausing as if a new topic had started.
            var collapsed = new StringBuilder(text.Length);
            var lastWasSpace = false;
            foreach (var ch in text)
            {
                var isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!lastWasSpace) collapsed.Append(' ');
                }
                else
                {
                    collapsed.Append(ch);
                }

                lastWasSpace = isSpace;
            }

            var result = collapsed.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        // What the speaker says when the summary could not be produced.
        //
        // Deliberately not "fall back to the full text". Somebody who chose this
        // mode chose it to avoid a five-minute reading, and handing them one
        // because the summariser failed is the opposite of what they asked for.
        // Deliberately not silence either: the acceptance criteria call for a
        // stated failure, and a speaker that simply says nothing is
        // indistinguishable from a broken one.
        internal const string Unavailable = "Summary unavailable.";

        // The seam. UI and unit tests drive the whole path through this without
        // ever spawning a CLI; null means the real one.
        internal static Func<string, Task<string?>>? SummarizerForTests;

        internal static Task<string?> SummarizeAsync(string reply)
        {
            var seam = SummarizerForTests;
            return seam is not null ? seam(reply) : RunAsync(reply);
        }

        // Everything the summary leg decides, with the utterance left to the
        // caller. Separated for the reason OrbWindowSpeakTests' header sets out
        // at length: TextToSpeech.Speak starts a real speech engine, so no
        // headless test may reach it, and a decision left inside the method that
        // speaks is a decision no test can see. This returns the exact string
        // that will be spoken, and is where every arm of "what happens when the
        // summariser does not work" is actually pinned.
        //
        // Never returns null. A failure is a different sentence, not silence.
        internal static async Task<string> SummarizeOrSayWhyAsync(string reply)
        {
            try
            {
                var summary = await SummarizeAsync(reply).ConfigureAwait(true);
                return string.IsNullOrWhiteSpace(summary) ? Unavailable : summary;
            }
            catch (Exception ex)
            {
                // A summariser that threw is a summariser that failed, and the
                // answer is the same as any other failure. Surfaced rather than
                // swallowed, for the same reason the speak command's stderr is:
                // this is the only explanation anyone gets for why they heard a
                // sentence instead of their summary.
                Console.Error.WriteLine($"Claude Buddy: couldn't summarise for speech: {ex.Message}");
                return Unavailable;
            }
        }

        // Where the summariser is run from, and why it is not wherever the app
        // happens to be.
        //
        // `claude -p` discovers the CLAUDE.md at or above its working
        // directory. Inheriting the app's cwd therefore hands the throwaway
        // summariser the whole project's instructions — and CB-174 is what that
        // cost: asked to summarise a reply, it answered *in the project's
        // persona*, declined to summarise without more context, and volunteered
        // an unrelated sentence about a release build earlier that day. All of
        // which was then read aloud.
        //
        // The design CB-165 argued for was "a different process, a different
        // conversation" — this is the line that makes that true rather than
        // merely intended. Measured both ways on one machine, same binary, same
        // model, same stdin: from the project directory a persona reply, from a
        // neutral directory a clean two-sentence summary.
        //
        // The temp directory, because it is the one place guaranteed to have no
        // CLAUDE.md at or above it that belongs to any project. A home-level
        // ~/.claude/CLAUDE.md was checked and does not colour the output here.
        internal static string NeutralWorkingDirectory => Path.GetTempPath();

        // The invocation, as a value, so a test can assert it without spawning
        // anything. Extracted for the reason OrbWindowSpeakTests gives about
        // Speak: a decision left inside the method that performs the side
        // effect is a decision nothing can assert — which is exactly how CB-174
        // shipped, since the prompt and the cleaning were both covered while
        // the thing actually wrong was never looked at.
        internal static ProcessStartInfo StartInfoFor(string claude)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = claude,
                WorkingDirectory = NeutralWorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(Model);

            return startInfo;
        }

        // Excluded from coverage: spawns the Claude Code CLI as a subprocess.
        // Everything it decides — the prompt, the tidying, the failure answer,
        // and now the working directory — is pure and covered above; what
        // remains here is process plumbing of the same shape UsagePoller already
        // carries, and running it in a test would make a real billed request on
        // the developer's own account.
        [ExcludeFromCodeCoverage]
        private static async Task<string?> RunAsync(string reply)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            try
            {
                var startInfo = StartInfoFor(claude);

                using var proc = new Process { StartInfo = startInfo };
                if (!proc.Start()) return null;

                await proc.StandardInput.WriteAsync(Prompt(reply)).ConfigureAwait(false);
                proc.StandardInput.Close();

                var stdout = proc.StandardOutput.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeoutMs);
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return null;
                }

                if (proc.ExitCode != 0) return null;

                return Clean(await stdout.ConfigureAwait(false));
            }
            // The same two arms UsagePoller keeps, and for the same reason: a
            // summariser that throws into the UI thread would take the panel
            // with it, and every failure here has one answer anyway.
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
