using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// What happens to a message arriving from another machine: is it shown to the
// person reading the panel, or is it an answer to a question the app asked on
// their behalf?
//
// The second case is the one worth testing, and it is a decision with a stated
// reason: a colour or capability answer is swallowed whether or not it parses,
// because showing someone a fumbled answer to a question they never asked is
// worse than showing nothing. Get that wrong and the panel fills with
// "CB-INFO: color=#ff0000" lines nobody sent.
//
// In the UI suite because a real message is delivered through
// Dispatcher.UIThread.Post — it arrives on the relay's reader thread and the
// panel it reaches is a control.
[Collection("Settings")]
public class RemoteControlInboundTests
{
    private const string Account = "work@example.com";

    private static BridgeProtocol.InboundMessage Message(string body, string from = "zara") =>
        new(FromName: from, From: "bridge:session_01SX9H", Mode: "prompting", Body: body);

    private static List<BridgeProtocol.InboundMessage> Delivered(
        string body, string from = "zara")
    {
        var seen = new List<BridgeProtocol.InboundMessage>();
        void Watch(BridgeProtocol.InboundMessage m) => seen.Add(m);

        RemoteControlSessions.MessageReceived += Watch;
        try
        {
            RemoteControlSessions.OnMessage(Account, Message(body, from));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            RemoteControlSessions.MessageReceived -= Watch;
        }

        return seen;
    }

    // ---- an ordinary message --------------------------------------------

    [AvaloniaFact]
    public void AnOrdinaryMessageIsDelivered()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        var seen = Delivered("the build is green");

        var message = Assert.Single(seen);
        Assert.Equal("the build is green", message.Body);
    }

    // The account is stamped on the way through, because the message itself does
    // not carry one and a name alone no longer identifies a session once there is
    // more than one account.
    [AvaloniaFact]
    public void TheAccountIsStampedOnTheMessage()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        var message = Assert.Single(Delivered("the build is green"));

        Assert.Equal(Account, message.Account);
    }

    // ---- an answer to a question the app asked --------------------------

    [AvaloniaFact]
    public void AnInfoReplyIsNotShownToAnyone()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Delivered(BridgeProtocol.InfoMarker + " color=#ff0000"));
    }

    [AvaloniaFact]
    public void AnInfoReplysColourIsRemembered()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Delivered(BridgeProtocol.InfoMarker + " color=#ff0000");

        Assert.Equal("#ff0000", RemoteControlSessions.ColourFor(Account, "zara"));
    }

    // Remembered per account as well as per name, since two accounts can hold
    // identically-named sessions and one answering must not colour the other.
    [AvaloniaFact]
    public void ColoursAreRememberedPerAccountNotJustPerName()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        RemoteControlSessions.OnMessage(Account,
            Message(BridgeProtocol.InfoMarker + " color=#ff0000"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#ff0000", RemoteControlSessions.ColourFor(Account, "zara"));
        Assert.Null(RemoteControlSessions.ColourFor("home@example.com", "zara"));
    }

    // The important half: an info reply that does NOT parse is still swallowed.
    // IsInfoReply is deliberately separate from ParseColorReply for exactly this
    // — the person never asked the question, so they should not see the fumbled
    // answer either.
    [AvaloniaFact]
    public void AnUnparseableInfoReplyIsStillSwallowed()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Delivered(BridgeProtocol.InfoMarker + " I'm not sure what you mean"));
        Assert.Null(RemoteControlSessions.ColourFor(Account, "zara"));
    }

    // The marker is matched case-insensitively, because it comes back through a
    // model that may have retyped it.
    [AvaloniaFact]
    public void TheMarkerIsRecognisedWhateverItsCase()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(Delivered("cb-info: color=#00ff00"));
        Assert.Equal("#00ff00", RemoteControlSessions.ColourFor(Account, "zara"));
    }

    // A message that merely mentions a colour is not an info reply — it is
    // somebody talking about colours, and it has to reach the panel.
    [AvaloniaFact]
    public void AMessageAboutColoursIsStillAMessage()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        var seen = Delivered("I set color=#ff0000 on the orb, looks good");

        Assert.Single(seen);
        Assert.Null(RemoteControlSessions.ColourFor(Account, "zara"));
    }

    // ---- capabilities ----------------------------------------------------

    [AvaloniaFact]
    public void AnInfoReplysCommandsAreRemembered()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Delivered(BridgeProtocol.InfoMarker + " commands=/deploy,/rollback");

        var commands = RemoteControlSessions.CommandsFor(Account, "zara");

        Assert.NotEmpty(commands);
    }

    // An unanswered session offers nothing rather than a guess. The list is asked
    // for rather than assumed because only a custom command can actually be
    // followed, and offering commands that quietly do nothing when accepted is
    // worse than offering none.
    [AvaloniaFact]
    public void AnUnansweredSessionOffersNoCommands()
    {
        RemoteControlSessions.ForgetAnswersForTests();

        Assert.Empty(RemoteControlSessions.CommandsFor(Account, "never-answered"));
    }
}
