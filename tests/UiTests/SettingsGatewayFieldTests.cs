using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The gateway address and token fields, and the voice name the picker shows
// before anything has been enumerated.
//
// Safe to drive with the feature switched OFF, which is the whole reason these
// are testable: OpenClawSessions.Restart() returns as soon as it sees either
// OpenClawEnabled false or no host set, so nothing here opens a socket. Every
// test below keeps it off deliberately — turning it on with a host set would
// start a real supervisor task against a real address.
[Collection("Settings")]
public class SettingsGatewayFieldTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    private static void Offline()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = false;
    }

    // ---- the address field ----------------------------------------------

    // Committed on losing focus rather than on every keystroke, so the app is not
    // handed half-written addresses to reconnect to.
    [AvaloniaFact]
    public void TheAddressIsCommittedWhenTheFieldLosesFocus()
    {
        Offline();
        var window = NewWindow();
        var box = (TextBox)window.GatewayHostBox();

        box.Text = "gateway.example.com";
        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal("gateway.example.com", ClaudeBuddySettings.OpenClawHost);
    }

    // Trimmed, because an address pasted from a terminal or a chat message
    // arrives with whitespace and would otherwise fail to resolve for a reason
    // nothing on screen explains.
    [AvaloniaFact]
    public void TheAddressIsTrimmed()
    {
        Offline();
        var window = NewWindow();
        var box = (TextBox)window.GatewayHostBox();

        box.Text = "  gateway.example.com  ";
        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal("gateway.example.com", ClaudeBuddySettings.OpenClawHost);
    }

    // Losing focus without having changed anything does nothing at all. Worth its
    // own case: the handler's early return is what stops tabbing through the
    // window from dropping and rebuilding a working connection.
    [AvaloniaFact]
    public void LosingFocusWithoutAChangeDoesNotReconnect()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "gateway.example.com";
        ClaudeBuddySettings.OpenClawFingerprint = "ab:cd:ef";

        var window = NewWindow();
        var box = (TextBox)window.GatewayHostBox();

        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        // The fingerprint survives, which is the observable difference: changing
        // the address clears it.
        Assert.Equal("ab:cd:ef", ClaudeBuddySettings.OpenClawFingerprint);
    }

    // Changing the address clears the pinned certificate fingerprint. It has to:
    // a fingerprint is pinned to the host it was seen on, and carrying it over
    // would either reject the new gateway or, worse, appear to trust it.
    [AvaloniaFact]
    public void ChangingTheAddressForgetsThePinnedCertificate()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "old.example.com";
        ClaudeBuddySettings.OpenClawFingerprint = "ab:cd:ef";

        NewWindow().OnGatewayHostChanged("new.example.com");

        Assert.Equal("new.example.com", ClaudeBuddySettings.OpenClawHost);
        Assert.Equal("", ClaudeBuddySettings.OpenClawFingerprint);
    }

    // Clearing the address is a legitimate act — it is how you switch the feature
    // off without losing the token — and must not be mistaken for "no change".
    [AvaloniaFact]
    public void TheAddressCanBeCleared()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "gateway.example.com";

        var window = NewWindow();
        var box = (TextBox)window.GatewayHostBox();

        box.Text = "";
        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal("", ClaudeBuddySettings.OpenClawHost);
    }

    // ---- the token field -------------------------------------------------

    // Stored against the host it belongs to, not globally: someone with two
    // gateways has two tokens, and using one against the other reads as a refused
    // credential rather than as a mix-up.
    [AvaloniaFact]
    public void TheTokenIsStoredAgainstItsOwnHost()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "one.example.com";

        NewWindow().OnGatewayTokenChanged("one.example.com", "token-one");

        Assert.Equal("token-one", OpenClawIdentity.GatewayTokenFor("one.example.com"));
        Assert.NotEqual("token-one", OpenClawIdentity.GatewayTokenFor("two.example.com"));
    }

    [AvaloniaFact]
    public void TheTokenFieldShowsWhatIsAlreadyStoredForThatHost()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "one.example.com";
        OpenClawIdentity.SetGatewayTokenFor("one.example.com", "token-one");

        var box = (TextBox)NewWindow().GatewayTokenBox();

        Assert.Equal("token-one", box.Text);
    }

    // With no address there is no host to file a token under, so the field opens
    // empty rather than showing whichever token happened to be stored last.
    [AvaloniaFact]
    public void TheTokenFieldIsEmptyWithNoAddressSet()
    {
        Offline();
        ClaudeBuddySettings.OpenClawHost = "";

        var box = (TextBox)NewWindow().GatewayTokenBox();

        Assert.True(string.IsNullOrEmpty(box.Text));
    }

    // ---- the voice placeholder -------------------------------------------

    // Read straight from settings rather than through TextToSpeech.SelectedVoice(),
    // which calls AllVoiceOptions() and so performs exactly the scan this
    // placeholder exists to avoid. That is the point of the method, so each engine
    // gets a case.
    [AvaloniaFact]
    public void ThePlaceholderNamesTheVoiceForWhicheverEngineIsSelected()
    {
        ClaudeBuddySettings.ReloadForTests();

        ClaudeBuddySettings.SpeakEngine = "custom";
        ClaudeBuddySettings.SpeakCommandVoice = "a-custom-voice";
        Assert.Equal("a-custom-voice", SettingsWindow.SavedVoiceNameForPlaceholder());

        ClaudeBuddySettings.SpeakEngine = "neural";
        ClaudeBuddySettings.NeuralVoice = "af_heart";
        Assert.Equal("af_heart", SettingsWindow.SavedVoiceNameForPlaceholder());

        ClaudeBuddySettings.SpeakEngine = "system";
        ClaudeBuddySettings.SpeakVoice = "Daniel";
        Assert.Equal("Daniel", SettingsWindow.SavedVoiceNameForPlaceholder());
    }

    // An engine name this version does not know falls through to the system
    // voice rather than returning nothing — the picker still has something to
    // show, which is all a placeholder has to do.
    [AvaloniaFact]
    public void AnUnknownEngineFallsBackToTheSystemVoice()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.SpeakEngine = "something-new";
        ClaudeBuddySettings.SpeakVoice = "Daniel";

        Assert.Equal("Daniel", SettingsWindow.SavedVoiceNameForPlaceholder());
    }

    // ---- the relay account list --------------------------------------------

}
