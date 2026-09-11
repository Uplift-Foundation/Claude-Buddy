using Xunit;

namespace ClaudeBuddy.Tests;

// CB-144: a fenced code block is an *example*, and no arm of the grammar may
// read a field out of one — with exactly one exception, CB-141's marked `yaml`
// block, which is the one fence a writer has explicitly declared to be a
// persona.
//
// These leaks are pre-existing. They were found during CB-141's QA, they are
// identical on `develop`, and CB-142 did not cause them — what CB-142 did was
// add a fence check to *one new arm* while four older paths went on leaking,
// which is what made the narrow shape visibly wrong and got the rule moved to
// one gate. The four measured holes, all with no persona heading anywhere:
//
//     ```yaml                    ```markdown              ```markdown
//     - name: Build the thing    - **Voice:** af_bella    | Voice | af_bella |
//     ```                        ```                      ```
//
//     ```markdown
//     **Voice:** af_bella
//     ```
//
// The first is the one that matters, and it is the reason guarding only the
// standalone-bold and table arms was insufficient: the **bullet** arm calls
// `BoldField`/`FieldAfterColon` itself on `trimmed[1..]` rather than going
// through those arms, so it was untouched by a per-arm guard. `- name: ...` is
// an ordinary GitHub Actions step. Pasting a workflow into a CLAUDE.md renamed
// the user's orb after a build step.
//
// **The pairing in this file is the point.** Every refusal below is matched by
// a positive control, because a guard that refused *everything* would satisfy
// the refusals on its own and would be a far worse bug than the one being
// fixed — it would silently stop every shipped profile resolving. Read the two
// halves together or neither means anything.
public class PersonaFencedFieldTests
{
    // --- the four measured leaks, each its own case ------------------------

    // The GitHub Actions step. Written as the real thing rather than as a
    // minimal repro, because "a workflow pasted into a CLAUDE.md" is the
    // actual event and `- name:` only looks like persona metadata by accident.
    [Fact]
    public void AFencedWorkflowStepDoesNotNameThePersona()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Working in this repository",
            "",
            "Our build runs:",
            "",
            "```yaml",
            "steps:",
            "  - name: Build the thing",
            "    run: dotnet build",
            "```",
        });

        Assert.Null(fields.Name);
    }

    // The same line on its own, which is the arm-level statement: a bullet
    // inside a fence is not a field, however well-formed it looks.
    [Fact]
    public void AFencedBulletNameIsNotAField()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "```yaml",
            "- name: Build the thing",
            "```",
        }).Name);
    }

    [Fact]
    public void AFencedBulletedBoldVoiceIsNotAField()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "```markdown",
            "- **Voice:** af_bella",
            "```",
        }).Voice);
    }

    [Fact]
    public void AFencedTableVoiceIsNotAField()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "```markdown",
            "| Voice | af_bella |",
            "```",
        }).Voice);
    }

    [Fact]
    public void AFencedBoldVoiceIsNotAField()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "```markdown",
            "**Voice:** af_bella",
            "```",
        }).Voice);
    }

    // A picture too, since the same arms carry one and a leak here would put a
    // file path from somebody's example onto an orb.
    [Fact]
    public void AFencedPictureIsNotAField()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "```markdown",
            "- Avatar: leota.png",
            "```",
        });

        Assert.Null(fields.Avatar);

        // RawAvatar too: a value reported as "named but unreadable" would put
        // an example's path into persona.log as though somebody had meant it.
        Assert.Null(fields.RawAvatar);
    }

    // Tilde fences are fences. Cheap to get wrong, since every fixture in the
    // suite happens to use backticks.
    [Fact]
    public void ATildeFenceHidesFieldsJustAsABacktickFenceDoes()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "~~~markdown",
            "- Name: Aurora",
            "~~~",
        }).Name);
    }

    // --- the positive controls, which matter as much ------------------------

    // Unfenced, the very same lines are fields. Without this the refusals
    // above are satisfied by a parser that reads nothing at all.
    [Theory]
    [InlineData("- Name: Aurora", "Aurora", null, null)]
    [InlineData("- **Name:** Aurora", "Aurora", null, null)]
    [InlineData("**Voice:** af_bella", null, "af_bella", null)]
    [InlineData("| Voice | af_bella |", null, "af_bella", null)]
    [InlineData("- **Voice:** af_bella", null, "af_bella", null)]
    [InlineData("- Avatar: leota.png", null, null, "leota.png")]
    [InlineData("| Portrait | leota.png |", null, null, "leota.png")]
    [InlineData("**Portrait:** leota.png", null, null, "leota.png")]
    public void TheSameLineOutsideAFenceIsStillAField(
        string line, string? name, string? voice, string? avatar)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Equal(name, fields.Name);
        Assert.Equal(voice, fields.Voice);
        Assert.Equal(avatar, fields.Avatar);
    }

    // A fence that opens and closes, then a real field after it. This is the
    // shape a "stop reading at the first fence" mistake would break, and it
    // would break it while passing every refusal above.
    [Fact]
    public void AFieldAfterAClosedFenceIsStillRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Notes",
            "",
            "```bash",
            "dotnet build",
            "```",
            "",
            "- Name: Aurora",
            "- Voice: af_bella",
        });

        Assert.Equal("Aurora", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
    }

    // Front matter is not a fence and must keep working — it is the shape
    // profile-gen's standalone template writes.
    [Fact]
    public void FrontMatterIsUnaffectedByTheFenceRule()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "name: \"Aurora\"",
            "voice: \"af_bella\"",
            "image: \"aurora.png\"",
            "---",
        });

        Assert.Equal("Aurora", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("aurora.png", fields.Avatar);
    }

    // --- the hazard, asserted directly (CB-144 acceptance criterion 3) ------

    // **The front-matter arm runs while `inFence` is true, on purpose.** That
    // is the entire mechanism of CB-141's marked block: a ```yaml fence inside
    // a `profile-gen` region is read as fields. A later change that "adds the
    // fence guard to the arms" uniformly would silently revert CB-141, and
    // every test in the repository would stay green except this one — which is
    // why it is written as its own case with this comment on it rather than
    // being left implied by the end-to-end tests.
    //
    // This is a rendered `profile.embedded.md.j2`, which is what the skill
    // actually emits.
    [Fact]
    public void AMarkedYamlBlockIsTheOneFenceStillReadAsFields()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=aurora -->",
            "### Aurora",
            "",
            "![Aurora](aurora.png)",
            "",
            "```yaml",
            "schema_version: 1",
            "name: \"Aurora\"",
            "slug: \"aurora\"",
            "image: \"aurora.png\"",
            "voice: \"af_bella\"",
            "nsfw: false",
            "```",
            "",
            "<!-- profile-gen:end slug=aurora -->",
        });

        Assert.Equal("Aurora", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("aurora.png", fields.Avatar);
    }

    // ...and the negative control that keeps the exception narrow: the same
    // yaml, not inside a marked block, is an ordinary example and stays one.
    // Without this, "the fence rule has an exception" and "the fence rule does
    // not apply to yaml" look identical.
    [Fact]
    public void TheSameYamlOutsideAMarkedBlockIsStillJustAnExample()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "",
            "Write your profile like this:",
            "",
            "```yaml",
            "name: \"Aurora\"",
            "voice: \"af_bella\"",
            "image: \"aurora.png\"",
            "```",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // The repository's own persona file, which uses no fence at all and must
    // be completely unaffected. CB-133 shipped with every test green while
    // this exact file resolved to nothing, so it earns a line in any ticket
    // that touches what the parser skips.
    [Fact]
    public void TheRepositorysOwnPersonaFileIsUnaffected()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Claude Buddy Persona",
            "",
            "I'm a female AI Architect who built this cute little Claudy Buddy Agentic AI harness.  ",
            "",
            "I'm the CTO in charge of the project and I give the orders.",
            "",
            "## Attributes",
            "",
            "Name Jennifer",
            "Profile Photo cto.png",
            "Voice is 50% sky and 50% nicole",
        });

        Assert.Equal("Jennifer", fields.Name);
        Assert.Equal("cto.png", fields.Avatar);
        Assert.Equal("50% sky and 50% nicole", fields.Voice);
    }

    // OpenClaw's real IDENTITY.md shape — a bare bulleted list, no heading, no
    // fence. The bullet arm is the one CB-144 actually changed the reach of,
    // so the shape that depends on it most is asserted here explicitly.
    [Fact]
    public void AnOpenClawIdentityFileIsUnaffected()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- **Name:** Annabel Lee",
            "- **Voice:** `af_nicole` (Kokoro TTS, rate 1.3)",
            "- **Avatar:** avatars/annabel-lee.png",
        });

        Assert.Equal("Annabel Lee", fields.Name);
        Assert.Equal("af_nicole", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
        Assert.Equal("avatars/annabel-lee.png", fields.Avatar);
    }
}
