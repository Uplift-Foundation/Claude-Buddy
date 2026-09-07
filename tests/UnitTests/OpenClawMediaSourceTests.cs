using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-109: the query string this app sends when it asks the gateway for a
// picture, and the identity that has to be on it.
//
// The bug was invisible from inside the app. Every fetch went out without a
// `sessionKey`, the gateway resolved its media policy against a server-side
// *default* agent, and a file sitting in the agent's own workspace came back
// `outside-allowed-folders` — a refusal that reads exactly like a real
// permissions problem rather than like the client having failed to say who was
// asking. Measured on a healthy gateway, one file, one token: without the key
// it is refused, with the key alone it answers `available:true` and then 200
// with the bytes.
//
// So these are string assertions on purpose. The route is the whole interface
// to that endpoint, it is a format nobody here controls, and the difference
// between working and refused is one query parameter — the same argument that
// keeps the transcript parsers covered against fixtures rather than read.
//
// The three "carries the session" cases at the bottom are load-bearing rather
// than coverage: OpenClawMediaSource deliberately still *permits* a null
// session (the no-session route is the byte-identical shape the app sent
// before this ticket, and pinning it is what proves nothing already working
// changed), so the invariant that production never builds one lives at the
// call sites. These are what fail if a fourth producer is added without a key.
//
// Serialised because the TurnsFromHistory cases at the bottom resolve speaker
// names and colours through the process-wide identity table.
[Collection("Settings")]
public class OpenClawMediaSourceTests
{
    // A real key, in the shape the gateway actually uses:
    // agent:<id>:<surface>:<kind>:<id>.
    private const string Key = "agent:comfyui:discord:direct:100000000000000001";

    private const string Png = "/Users/w/.openclaw/workspace-sample-agent/outputs/a.png";

    // ---- the trap, documented and made unreachable -----------------------

    // **This is the most important test in this file. Do not simplify it
    // away, and do not "restore" the agentId it asserts the absence of.**
    //
    // The gateway's handler reads its two parameters like so:
    //
    //   const sessionKey = url.searchParams.get("sessionKey")?.trim() || void 0;
    //   const agentId = sessionKey ? url.searchParams.get("agentId")?.trim() || void 0 : opts?.agentId;
    //
    // Two measurements, and they point the same way:
    //
    //  - **`agentId` alone is inert.** Sent without a key it is ignored, the
    //    gateway falls back to `opts.agentId` — the default agent it was
    //    started with — and answers against *that* agent's media policy. The
    //    response is an ordinary refusal with nothing in it to suggest the
    //    client sent an identity that was thrown away. Two separate agents
    //    working this ticket sent `agentId` alone, read the same
    //    `outside-allowed-folders` back, and concluded the gateway was
    //    refusing the folder. It was not. It had never been told whose folder
    //    it was.
    //
    //  - **A *mismatched* `agentId` is worse than useless.** `agentId=main`
    //    against comfyui's own session key answers 404 Not Found, while the
    //    control — `agentId=comfyui` with the same key — answers
    //    `available:true`. So the 404 isolates the disagreement, not the
    //    parameter's presence.
    //
    // With the key present and `agentId` absent the server resolves the agent
    // from the key itself, which is more authoritative than anything this
    // process can derive. So the parameter carries no information the key does
    // not, and the only thing it can do is turn a working fetch into a 404.
    // It is therefore not sent at all, and this is the test that keeps
    // somebody from carefully reintroducing it.
    [Fact]
    public void AgentIdIsNeverSentWithoutASessionKey()
    {
        var withSession = new OpenClawMediaSource(Png, Key).Route;
        var without = new OpenClawMediaSource(Png, null).Route;

        // The key goes, always.
        Assert.Contains("sessionKey=", withSession, StringComparison.Ordinal);

        // The agent id never does — with a session or without one, so there is
        // no arrangement of these two fields that produces the inert request
        // or the mismatched one.
        Assert.DoesNotContain("agentId", withSession, StringComparison.Ordinal);
        Assert.DoesNotContain("agentId", without, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionKey", without, StringComparison.Ordinal);
    }

    // The same, for a key that is present but says nothing — treated as
    // absent rather than sent as an empty key.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void AWhitespaceSessionKeyIsTreatedAsAbsent(string? key)
    {
        var route = new OpenClawMediaSource(Png, key).Route;

        Assert.DoesNotContain("sessionKey", route, StringComparison.Ordinal);
        Assert.Equal(new OpenClawMediaSource(Png, null).Route, route);
    }

    // ---- with a session -------------------------------------------------

    [Fact]
    public void ASessionKeyIsSentEscapedAndIsTheOnlyThingAdded()
    {
        var route = new OpenClawMediaSource(Png, Key).Route;

        Assert.Equal(
            OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(Png)
                + "&sessionKey=" + Uri.EscapeDataString(Key),
            route);
    }

    // Whatever shape the key has, it goes out as given. The gateway is the one
    // that resolves it, and rewriting somebody else's identifier on the way
    // past — or refusing to send one this app does not recognise — would be
    // this process second-guessing the only authority on the question.
    [Theory]
    [InlineData("agent:quill:discord:channel:9")]
    [InlineData("openclaw:agent:quill:discord:channel:9")]
    [InlineData("room:discord:900000000000000001")]
    [InlineData("notanagent")]
    public void AnyNonEmptyKeyIsSentUnaltered(string key)
    {
        var route = new OpenClawMediaSource(Png, key).Route;

        Assert.Contains("&sessionKey=" + Uri.EscapeDataString(key), route, StringComparison.Ordinal);
        Assert.DoesNotContain("agentId", route, StringComparison.Ordinal);
    }

    // ---- the no-regression pin ------------------------------------------

    // With no session this has to be *byte*-identical to the string the app
    // built before this type existed. Every picture that loads today loads
    // through a route of exactly this shape, and a type introduced to fix a
    // refusal must not quietly change the requests that were already working.
    [Fact]
    public void WithNoSessionTheRouteIsExactlyWhatTheAppBuiltBefore()
    {
        Assert.Equal(
            OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(Png),
            new OpenClawMediaSource(Png, null).Route);
    }

    // `source` first, with the identity appended after it — not for looks.
    // The app recognises one of these urls by prefix, and CB-93's refusal note
    // is what depends on that recognition. Had the identity gone in front of
    // `source`, every note would have stopped appearing with no test failing
    // and no picture visibly breaking, which is why both prefixes are asserted
    // here and why OpenClawMediaRefusalTests asserts the guard against a
    // fully-formed url as well.
    [Fact]
    public void SourceIsAlwaysTheFirstParameter()
    {
        var route = new OpenClawMediaSource(Png, Key).Route;

        Assert.StartsWith(OpenClawSessions.AssistantMediaRoute, route, StringComparison.Ordinal);
        Assert.StartsWith(OpenClawSessions.AssistantMediaPathPrefix, route, StringComparison.Ordinal);
        Assert.True(route.IndexOf("source=", StringComparison.Ordinal)
                    < route.IndexOf("sessionKey=", StringComparison.Ordinal));
    }

    // ---- escaping -------------------------------------------------------

    // The characters that break a query string if they go out raw, in one
    // path: a space, an `&` (which would otherwise start a parameter of its
    // own and truncate the path), a `#` (a fragment, which never reaches the
    // server at all), a `%` (the escape character itself) and a non-ASCII
    // character.
    [Fact]
    public void ThePathIsPercentEncoded()
    {
        const string awkward = "/media/a drop & 100% #1 ünicode.png";
        var route = new OpenClawMediaSource(awkward, null).Route;

        Assert.Contains("%20", route, StringComparison.Ordinal);
        Assert.Contains("%26", route, StringComparison.Ordinal);
        Assert.Contains("%23", route, StringComparison.Ordinal);
        Assert.Contains("%25", route, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", route, StringComparison.Ordinal);
        Assert.DoesNotContain("#", route, StringComparison.Ordinal);

        // And it survives the round trip, which is the property that actually
        // matters — the gateway unescapes this back into a filesystem path.
        Assert.Equal(awkward,
            Uri.UnescapeDataString(route[OpenClawSessions.AssistantMediaRoute.Length..]));
    }

    // The session key's colons are escaped too. They are legal in a query
    // value, so this is not a bug being fixed — it is one rule for both
    // fields instead of a per-field judgement about what this particular
    // server tolerates, and the gateway `trim()`s and compares the unescaped
    // value either way.
    [Fact]
    public void TheSessionKeysColonsArePercentEncoded()
    {
        var route = new OpenClawMediaSource(Png, Key).Route;

        Assert.Contains("%3A", route, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionKey=agent:", route, StringComparison.Ordinal);

        var sent = route[(route.IndexOf("&sessionKey=", StringComparison.Ordinal) + 12)..];
        sent = sent.Split('&')[0];
        Assert.Equal(Key, Uri.UnescapeDataString(sent));
    }

    // ---- MetaRoute ------------------------------------------------------
    //
    // Appended to Route rather than assembled separately, so the two can
    // never end up asking about different things. Both shapes, because the
    // interesting half is that the flag lands *after* the identity rather
    // than in the middle of it.

    [Fact]
    public void TheMetaRouteIsTheRoutePlusTheFlagWithNoSession()
    {
        var source = new OpenClawMediaSource(Png, null);
        Assert.Equal(source.Route + "&meta=1", source.MetaRoute);
    }

    [Fact]
    public void TheMetaRouteIsTheRoutePlusTheFlagWithASession()
    {
        var source = new OpenClawMediaSource(Png, Key);

        Assert.Equal(source.Route + "&meta=1", source.MetaRoute);
        Assert.EndsWith("&meta=1", source.MetaRoute, StringComparison.Ordinal);
        Assert.Contains("&sessionKey=", source.MetaRoute, StringComparison.Ordinal);
    }

    // ---- the traversal guard, still shut --------------------------------
    //
    // Reached through LocalMediaPathFrom, which is the only way into
    // LooksLikeAnImagePath. Restated here rather than left to CB-89's own
    // tests because of what CB-109 measured: supplying a session does not
    // scope a request to the asking agent's own workspace, it disables the
    // gateway's folder allowlist outright — a workspace-sample-agent file
    // serves under agent:main's session, and ~/Desktop, /tmp and other agents'
    // workspaces all clear the folder check and fail only on existence or
    // media type.
    //
    // Owner has signed off on that trust model, so there is no client-side
    // root check and this app fetches whatever path an agent named. **That
    // makes these two refusals the only structural guard left on this path.**
    // They are not redundant with a gateway allowlist any more, because for a
    // request carrying a session there effectively isn't one. The gateway does
    // still normalise before it checks — a probe with
    // `outputs/../../../../etc/passwd` answers `file-not-found` rather than a
    // served file — but a client that sends a traversal and waits to be told
    // no is asking the wrong question.

    [Theory]
    [InlineData("/Users/w/.openclaw/media/../../../../etc/passwd.png")]
    [InlineData("/media/..%2Fetc/passwd.png")]
    [InlineData("//evil.example/a.png")]
    public void ATraversalOrProtocolRelativePathIsStillRefused(string text)
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:" + text));
    }

    [Fact]
    public void AnOrdinaryRootedImagePathIsStillAccepted()
    {
        Assert.Equal(Png, OpenClawSessions.LocalMediaPathFrom("MEDIA:" + Png)?.Path);
    }

    // ---- threaded through the parser ------------------------------------
    //
    // Three arms of TurnsFromHistory build an assistant-media route, and all
    // three have to carry the session. They were three separate string
    // concatenations before this ticket, which is exactly how one of them
    // would have been missed.

    private static System.Collections.Generic.List<HistoryTurn> Turns(string json, string? key) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement, key);

    // The delivery-mirror arm: the gateway's own record of having delivered a
    // picture, whose content is a bare filename.
    [Fact]
    public void TheDeliveryMirrorArmCarriesTheSession()
    {
        var turns = Turns("""
        [{"role":"assistant","api":"openclaw-transcript","provider":"openclaw",
          "model":"delivery-mirror",
          "content":[{"type":"text","text":"sample_scene_100200300.png"}]}]
        """, Key);

        var turn = Assert.Single(turns);

        // The url is fully formed here, at construction, which is the whole
        // shape of the fix: the panel fetches it verbatim and never has to
        // know which session it belonged to.
        Assert.NotNull(turn.ImageUrl);
        Assert.Contains("&sessionKey=" + Uri.EscapeDataString(Key), turn.ImageUrl!, StringComparison.Ordinal);

        // And the clean path travels beside it, for the tooltip on a refusal.
        Assert.NotNull(turn.ImageSourcePath);
        Assert.EndsWith("sample_scene_100200300.png", turn.ImageSourcePath!, StringComparison.Ordinal);
        Assert.Equal(new OpenClawMediaSource(turn.ImageSourcePath!, Key).Route, turn.ImageUrl);
    }

    // The named-by-path arm: an agent writing "MEDIA:<path>" in its own reply.
    // This is the arm the reported bug was seen through — an agent's picture
    // in its own workspace, refused for being outside a *different* agent's
    // allowed folders.
    [Fact]
    public void TheNamedByPathArmCarriesTheSession()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":
          "here you go\nMEDIA:{{Png}}"}]}]
        """, Key);

        var turn = Assert.Single(turns);

        Assert.Equal(Png, turn.ImageSourcePath);
        Assert.Equal(new OpenClawMediaSource(Png, Key).Route, turn.ImageUrl);
        Assert.Contains("&sessionKey=", turn.ImageUrl!, StringComparison.Ordinal);
    }

    // The image-block arm, which must **not** get one. Its url came out of
    // the gateway's own image block rather than out of AssistantMediaRoute,
    // so there is no path for a media policy to be resolved against and
    // nothing for `&meta=1` to answer. Attaching an identity to it would
    // claim it is an assistant-media fetch when it is not.
    [Fact]
    public void AnImageBlocksOwnUrlGetsNoMediaSourceAndIsLeftAlone()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"image","url":"https://x/a.png","alt":"a"}]}]
        """, Key);

        var turn = Assert.Single(turns);

        Assert.Null(turn.ImageSourcePath);
        Assert.Equal("https://x/a.png", turn.ImageUrl);
    }

    // An inline image block — the shape this gateway actually emits — has no
    // url at all and equally no source: the bytes are already in hand and
    // there is nothing to fetch.
    [Fact]
    public void AnInlineImageBlockGetsNoMediaSourceEither()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"image",
          "data":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==",
          "mimeType":"image/png"}]}]
        """, Key);

        var turn = Assert.Single(turns);

        Assert.Null(turn.ImageSourcePath);
        Assert.Null(turn.ImageUrl);
        Assert.NotNull(turn.ImageBytes);
    }

    // A page read with no session — a fixture, nothing production does — still
    // parses, still carries the path, and builds the pre-CB-109 route. This is
    // the case that makes the byte-identical pin above meaningful end to end
    // rather than only at the constructor.
    [Fact]
    public void APageReadWithNoSessionStillCarriesThePathAndTheOldRoute()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":"MEDIA:{{Png}}"}]}]
        """, null);

        var turn = Assert.Single(turns);

        Assert.Equal(Png, turn.ImageSourcePath);
        Assert.Equal(OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(Png), turn.ImageUrl);
        Assert.DoesNotContain("sessionKey", turn.ImageUrl!, StringComparison.Ordinal);
    }

    // A whole page's worth, so the "one arm forgot" failure has somewhere to
    // show up: every picture turn on a page read with a session carries that
    // session, whichever arm built it.
    //
    // Named for the invariant rather than for the coverage, because that is
    // what it is. OpenClawMediaSource permits a session-less route on purpose,
    // so nothing in the type stops a producer from building one — this test
    // and its two single-arm siblings are the whole of what does. If this
    // fires, a picture is being requested without saying whose conversation
    // named it, and the gateway will answer it against a default agent and
    // refuse a file that was there all along.
    [Fact]
    public void EveryPictureArmCarriesTheSession()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":"MEDIA:{{Png}}"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"other.png"}]},
         {"role":"assistant","content":[{"type":"text","text":"just talking"}]}]
        """, Key);

        var pictures = turns.Where(t => t.ImageSourcePath is not null).ToList();

        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, t =>
            Assert.Contains("&sessionKey=" + Uri.EscapeDataString(Key), t.ImageUrl!, StringComparison.Ordinal));
        // Rebuilt from the path and the key, which is how a reader can check
        // the two halves the turn carries actually agree with each other.
        Assert.All(pictures, t =>
            Assert.Equal(new OpenClawMediaSource(t.ImageSourcePath!, Key).Route, t.ImageUrl));
    }
}
