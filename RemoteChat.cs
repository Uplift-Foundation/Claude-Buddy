using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeBuddy
{
    // What the chat panel needs from a session it can talk to, and nothing more.
    //
    // An interface here rather than the panel reaching into OpenClawSessions
    // directly, for one practical reason: it lets the whole panel — layout,
    // keyboard, streaming, autoscroll, the mic — be built and watched against an
    // in-memory fake before the gateway can send a word. The transport is then
    // swapping one implementation for another rather than the moment everything
    // is first tried at once.
    //
    // Four requirements on any implementation, each of which the panel relies on
    // and none of which it can check:
    //
    //  1. Every event is raised on the UI thread. The implementation does its
    //     own Dispatcher.Post. The alternative is every consumer hopping threads
    //     by hand and a comment on each explaining why.
    //  2. TurnUpdated carries the whole turn, already mutated — not a delta. A
    //     dropped or coalesced event then costs nothing, because the panel
    //     re-reads Text. Deltas make the view desyncable with no way to notice.
    //     (The gateway obliges: its `agent` events carry data.text as a full
    //     snapshot alongside data.delta.)
    //  3. SendAsync raises TurnAdded for the user's own turn. The panel never
    //     inserts optimistically, so exactly one thing owns the transcript and a
    //     failed send leaves no ghost behind.
    //  4. History is already bounded and ordered oldest to newest. The panel
    //     shows what it is given and never trims or pages.
    public enum ChatRole { User, Assistant, System }

    public enum RemoteChatState { Disconnected, Connecting, Connected, Error }

    // Mutable on purpose: a streaming reply updates Text in place and raises
    // TurnUpdated, so the list never recreates the item. Recreating it would
    // re-template the row, which is the one thing that could steal focus from
    // the input mid-sentence.
    public sealed class ChatTurn : INotifyPropertyChanged
    {
        private string _text = "";

        public ChatRole Role { get; init; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                Raise();
            }
        }

        public bool IsComplete { get; set; }

        // When this turn happened, in local time. The gateway records a
        // timestamp per message and the panel shows it, so a conversation that
        // has been going on across Discord and a terminal all day reads as
        // something with a shape rather than a flat wall.
        //
        // Defaulted to now, which is right for a turn created live and is
        // overwritten from the backlog for one that wasn't.
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        // A picture sent in the conversation, as a path on the gateway rather
        // than bytes: a transcript can hold a dozen of them and only the ones
        // actually scrolled to are worth a megabyte each. The panel resolves it.
        public string? ImageUrl { get; init; }

        public string ImageAlt { get; init; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public interface IRemoteChatSession
    {
        string SessionId { get; }
        string DisplayName { get; }
        RemoteChatState State { get; }
        IReadOnlyList<ChatTurn> History { get; }

        event Action<ChatTurn>? TurnAdded;
        event Action<ChatTurn>? TurnUpdated;
        event Action<RemoteChatState>? StateChanged;

        Task SendAsync(string text);

        // Stops the reply in flight. Separate from dismissing the panel: closing
        // a window should never cancel work someone asked for.
        void Cancel();
    }

}
