using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the half of the usage feature that decides what the CLI's answer
// *means*.
//
// The fixtures below keep the exact shape of a real `get_usage` control
// response — captured off this machine by piping the control request into
// `claude -p --input-format stream-json --output-format stream-json` — with the
// account's own percentages and reset times replaced. The shape is the part
// under test and the part that can break; the numbers are nobody's business.
// Written from a capture rather than from memory for the reason the transcript
// suite states: this repo's permission-dialog parser was first written against
// an invented fixture and failed on every real dialog.
//
// Two things here are worth more than the rest. The first is the *scale*:
// `utilization` in this payload is 0-100, while the statusline payload spells
// the same quantity 0-1. A parser that picks the wrong one still draws a
// perfectly plausible ring, so the assertion that 84 stays 84 is load-bearing.
// The second is that every failure mode has to come back null rather than a
// zeroed reading — an account whose answer could not be understood must not be
// drawn as an account with no usage.
public class UsageParseTests
{
    private static readonly DateTimeOffset ReadAt =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    // The CLI answers in JSONL: one message, one line, however long. The
    // fixtures below are wrapped so a person can read them and are folded back
    // to a single line here, so what the parser actually sees is the shape it
    // will see on the wire rather than a prettier one. Safe because JSON
    // whitespace between tokens is insignificant and no string in these
    // fixtures spans a line.
    private static string OneLine(string json) =>
        json.Replace("\r", "").Replace("\n", " ");

    // A live subscription account, extra usage switched off at the org level.
    // That last part is not a contrived edge case: it is the state of every
    // account on the machine this was developed against, and it is why the
    // inner ring had to become an absence rather than a gauge reading zero.
    private static readonly string Live = OneLine("""
    {"type":"control_response","response":{"subtype":"success","request_id":"cb-usage","response":{
      "subscription_type":"team",
      "rate_limits_available":true,
      "session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},
      "rate_limits":{
        "five_hour":{"utilization":33,"resets_at":"2026-08-30T17:29:00+00:00",
                     "limit_dollars":null,"used_dollars":null,"remaining_dollars":null,"locked_reason":null},
        "seven_day":{"utilization":84,"resets_at":"2026-09-03T04:59:59.643579+00:00",
                     "limit_dollars":null,"used_dollars":null,"remaining_dollars":null,"locked_reason":null},
        "seven_day_opus":null,
        "seven_day_sonnet":null,
        "nimbus_quill":{"utilization":0,"resets_at":null},
        "extra_usage":{"is_enabled":false,"monthly_limit":null,"used_credits":null,
                       "utilization":null,"currency":"USD","decimal_places":2,
                       "disabled_reason":"org_level_disabled_until","user_disabled":false,
                       "spend_limit_reached":true,"credits_ever_enabled":true,
                       "daily":null,"weekly":null},
        "spend":{"used":{"amount_minor":0,"currency":"USD","exponent":2},
                 "limit":null,"percent":0,"severity":"normal","enabled":false,
                 "disabled_reason":"org_level_disabled_until","cap":null,"balance":null,
                 "auto_reload":null,"can_purchase_credits":false,"can_toggle":false},
        "limits":[{"kind":"weekly_all","group":"weekly","percent":84,"severity":"warning",
                   "resets_at":"2026-09-03T04:59:59.643579+00:00","scope":null,"is_active":true}],
        "member_dashboard_available":false
      },
      "behaviors":{"requests_this_week":0}
    }}}
    """);

    [Fact]
    public void ReadsBothWindowsOnTheScaleTheyArriveOn()
    {
        var usage = UsageParse.FromStream(Live, null, "wthompson", ReadAt);

        Assert.NotNull(usage);
        Assert.True(usage!.Available);
        Assert.Equal("team", usage.SubscriptionType);

        // 84, not 0.84 and not 8400. The whole reason UsageWindow names its
        // scale in a comment rather than taking a bare double.
        Assert.Equal(33, usage.Session!.Percent);
        Assert.Equal(84, usage.Weekly!.Percent);
    }

    [Fact]
    public void ReadsTheIsoResetInstant()
    {
        var usage = UsageParse.FromStream(Live, null, "wthompson", ReadAt);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 17, 29, 0, TimeSpan.Zero),
            usage!.Session!.ResetsAt);
    }

    [Fact]
    public void CarriesTheConfigDirAndLabelThroughUntouched()
    {
        var usage = UsageParse.FromStream(Live, "/Users/x/.claude-board", "board", ReadAt);

        Assert.Equal("/Users/x/.claude-board", usage!.ConfigDir);
        Assert.Equal("board", usage.Label);
        Assert.Equal(ReadAt, usage.ReadAt);
    }

    // Disabled extra usage is a state, not a zero. An account with no cap has
    // nothing to be a percentage of, and saying so is the difference between
    // "you have not spent anything" and "you cannot spend anything".
    [Fact]
    public void DisabledExtraUsageKeepsItsReasonAndHasNoPercentage()
    {
        var usage = UsageParse.FromStream(Live, null, "wthompson", ReadAt);

        Assert.False(usage!.Extra!.Enabled);
        Assert.Equal("org_level_disabled_until", usage.Extra.DisabledReason);
        Assert.Equal("USD", usage.Extra.Currency);
        Assert.Equal(2, usage.Extra.DecimalPlaces);
        Assert.Null(usage.Extra.Percent);
    }

    [Fact]
    public void EnabledExtraUsageIsMoneyInMinorUnits()
    {
        var spending = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "rate_limits_available":true,
          "rate_limits":{
            "extra_usage":{"is_enabled":true,"monthly_limit":2000,"used_credits":6114,
                           "currency":"USD","decimal_places":2,"disabled_reason":null},
            "spend":{"used":{"amount_minor":6114,"currency":"USD","exponent":2},
                     "limit":2000,"enabled":true,"disabled_reason":null}
          }
        }}}
        """);

        var usage = UsageParse.FromStream(spending, null, "x", ReadAt);

        Assert.True(usage!.Extra!.Enabled);
        Assert.Equal(6114, usage.Extra.UsedMinor);
        Assert.Equal(2000, usage.Extra.LimitMinor);

        // Over the cap, and reported as such rather than clamped: $61.14 against
        // a $20.00 limit really is 305%.
        Assert.Equal(305.7, usage.Extra.Percent!.Value, 1);
    }

    // An API-key, Bedrock or Vertex account answers successfully and simply has
    // no subscription windows. That is a real reading and has to survive as one,
    // because the alternative is an orb that looks broken for an account that
    // is working perfectly.
    [Fact]
    public void AnAccountWithNoWindowsIsStillAReading()
    {
        var apiKey = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "subscription_type":null,"rate_limits_available":false,"rate_limits":null
        }}}
        """);

        var usage = UsageParse.FromStream(apiKey, null, "work", ReadAt);

        Assert.NotNull(usage);
        Assert.False(usage!.Available);
        Assert.Null(usage.Session);
        Assert.Null(usage.Weekly);
    }

    // Everything below is a way for the read to fail. Every one of them must
    // come back null — "no reading", which callers treat as change-nothing —
    // and never a reading full of zeroes.

    [Fact]
    public void AnErrorResponseIsNoReading()
    {
        var error = OneLine("""
        {"type":"control_response","response":{"subtype":"error","error":"nope"}}
        """);

        Assert.Null(UsageParse.FromStream(error, null, "x", ReadAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\"")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}")]
    public void UnreadableOutputIsNoReading(string stdout)
    {
        Assert.Null(UsageParse.FromStream(stdout, null, "x", ReadAt));
    }

    [Fact]
    public void FindsTheResponseAmongTheOtherRowsPrintModeEmits()
    {
        var stream = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\"}\n"
                     + Live + "\n"
                     + "{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":0}\n";

        var usage = UsageParse.FromStream(stream, null, "x", ReadAt);

        Assert.Equal(84, usage!.Weekly!.Percent);
    }

    // The upstream schema is `.passthrough()` at every level and the request is
    // documented as experimental, so new keys will show up unannounced. They
    // must be ignored rather than throwing — the rings going dark because
    // Anthropic added a field would be a bad way to find out.
    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var future = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "rate_limits_available":true,
          "some_new_top_level_thing":{"nested":[1,2,3]},
          "rate_limits":{
            "five_hour":{"utilization":12,"resets_at":"2026-08-30T17:29:00+00:00",
                         "brand_new_field":"whatever"},
            "a_window_nobody_has_seen":{"utilization":99}
          }
        }}}
        """);

        var usage = UsageParse.FromStream(future, null, "x", ReadAt);

        Assert.Equal(12, usage!.Session!.Percent);
    }

    [Fact]
    public void AWindowWithNoUtilizationIsAbsentRatherThanZero()
    {
        var nulled = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "rate_limits_available":true,
          "rate_limits":{"five_hour":{"utilization":null,"resets_at":null},"seven_day":null}
        }}}
        """);

        var usage = UsageParse.FromStream(nulled, null, "x", ReadAt);

        Assert.NotNull(usage);
        Assert.Null(usage!.Session);
        Assert.Null(usage.Weekly);
    }

    // Usage runs past a cap in the ordinary course of events. Clamping it here
    // would hide the one reading anybody actually needs to see.
    [Fact]
    public void UsagePastTheCapSurvivesUnclamped()
    {
        var over = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "rate_limits_available":true,
          "rate_limits":{"seven_day":{"utilization":104.5,"resets_at":null}}
        }}}
        """);

        var usage = UsageParse.FromStream(over, null, "x", ReadAt);

        Assert.Equal(104.5, usage!.Weekly!.Percent);
    }

    // The statusline spells resets_at as epoch seconds while this payload spells
    // it ISO-8601. Both are accepted, because the two sources having disagreed
    // about it once is reason enough not to bet on which is on the wire.
    [Fact]
    public void EpochSecondsAreAcceptedForTheResetInstant()
    {
        var epoch = OneLine("""
        {"type":"control_response","response":{"subtype":"success","response":{
          "rate_limits_available":true,
          "rate_limits":{"five_hour":{"utilization":50,"resets_at":1787954400}}
        }}}
        """);

        var usage = UsageParse.FromStream(epoch, null, "x", ReadAt);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1787954400),
            usage!.Session!.ResetsAt);
    }
}
