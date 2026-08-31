using System;
using Avalonia.Input;

namespace ClaudeBuddy
{
    // What Cmd+= and Cmd+- mean, and what the step either side of the current
    // size is. Pure, and deliberately window-free for the same reason
    // OrbArrangement and OrbGlyph are: the interesting part of a zoom gesture is
    // the arithmetic and the key mapping, and neither of those needs a chat
    // panel to be constructed before it can be checked.
    //
    // The scale is a multiplier over whatever size a piece of chat text ships
    // at, not a point size. Bubble text, system lines, code blocks and the
    // composer are all different sizes on purpose, and a user pressing Cmd+
    // is asking for "bigger", not for one number that flattens the four of them
    // into the same thing.
    public static class ChatZoom
    {
        public const double Default = 1.0;

        // Named steps rather than a multiplier applied repeatedly. Multiplying
        // by 1.1 each press means the sizes a user can reach depend on how many
        // times they have pressed the key since the last reset, which makes
        // "back to where it was yesterday" impossible to hit and makes the
        // stored number an unreadable 1.2100000000000002. A fixed ladder gives
        // eight reachable sizes, each of which is a number you can read in the
        // settings file and set by hand.
        //
        // Wider steps at the top: the difference between 1.0 and 1.15 is
        // obvious, while the same 0.15 added to 1.75 barely reads, so the
        // ladder opens up as it climbs.
        public static readonly double[] Steps = { 0.8, 0.9, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0 };

        public static double Min => Steps[0];

        public static double Max => Steps[^1];

        // Anything at all, including a hand-edited settings file and a NaN, maps
        // onto a usable multiplier. A scale of zero would draw nothing and a
        // negative one is not a size; both read as "the setting is broken", and
        // the honest answer to a broken size is the shipped one.
        public static double Clamp(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale)) return Default;

            return Math.Clamp(scale, Min, Max);
        }

        // The next step up from where we are, not from where the ladder thinks
        // we should be: a value that landed between two steps — a hand-edited
        // file, or a ladder that changed between versions — moves to the
        // nearest step above it rather than snapping downwards first, which
        // would make the first press of Cmd+ look like it did nothing or, worse,
        // made the text smaller.
        public static double Bigger(double scale) => Step(scale, up: true);

        public static double Smaller(double scale) => Step(scale, up: false);

        private static double Step(double scale, bool up)
        {
            var from = Clamp(scale);

            // Epsilon, not equality: the value has been through JSON and back,
            // and 1.15 does not always come out of a parser as the same double
            // the array holds. Half a step is far wider than that error and far
            // narrower than the gap between two rungs.
            const double Slack = 0.001;

            if (up)
            {
                foreach (var step in Steps)
                    if (step > from + Slack) return step;

                return Max;
            }

            for (var i = Steps.Length - 1; i >= 0; i--)
                if (Steps[i] < from - Slack) return Steps[i];

            return Min;
        }

        // Which rung a scale is standing on, nearest wins. The settings slider
        // moves over indices rather than over the numbers themselves, because
        // the ladder is deliberately uneven and a slider snapped to an even
        // tick could otherwise land between two rungs — leaving the keyboard
        // and the slider disagreeing about what "one step bigger" means.
        public static int IndexOf(double scale)
        {
            var from = Clamp(scale);
            var best = 0;

            for (var i = 1; i < Steps.Length; i++)
                if (Math.Abs(Steps[i] - from) < Math.Abs(Steps[best] - from)) best = i;

            return best;
        }

        public static double At(int index) => Steps[Math.Clamp(index, 0, Steps.Length - 1)];

        public enum Command
        {
            None,
            Bigger,
            Smaller,
            Reset
        }

        // Command on macOS, Control everywhere else — the same split every
        // other app makes, and the reason this is a property rather than a
        // constant folded into Gesture: a test can then check both platforms'
        // gestures on whichever runner it happens to be on, and check
        // separately that the right one is chosen here.
        public static KeyModifiers Accelerator =>
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        public static Command Gesture(Key key, KeyModifiers modifiers) =>
            Gesture(key, modifiers, Accelerator);

        public static Command Gesture(Key key, KeyModifiers modifiers, KeyModifiers accelerator)
        {
            // Exactly the accelerator and nothing else of consequence. Shift is
            // excluded from the comparison rather than forbidden because on a US
            // layout "+" *is* Shift and "=" — Cmd+Shift+= is how most people
            // will actually type Cmd++, and refusing it would break the gesture
            // for the half of users who reach for the plus sign they can see.
            // Alt and the opposite platform's modifier are not ignored: Cmd+Alt+-
            // belongs to whatever binds it, not to us.
            var held = modifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt);
            if (held != accelerator) return Command.None;

            return key switch
            {
                Key.OemPlus or Key.Add => Command.Bigger,
                Key.OemMinus or Key.Subtract => Command.Smaller,

                // Cmd+0 is "actual size" in every browser and most editors, and
                // it is the only way back for someone who has zoomed past the
                // point where they can find the setting.
                Key.D0 or Key.NumPad0 => Command.Reset,
                _ => Command.None
            };
        }

        // What a Command does to a scale. Kept here rather than in the panel so
        // the whole of "what the keystroke means" is one testable thing, and so
        // the panel's handler stays a lookup and an assignment.
        public static double Apply(Command command, double scale) => command switch
        {
            Command.Bigger => Bigger(scale),
            Command.Smaller => Smaller(scale),
            Command.Reset => Default,
            _ => Clamp(scale)
        };
    }
}
