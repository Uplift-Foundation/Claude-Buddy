using Xunit;

namespace ClaudeBuddy.Tests;

// CB-141: a persona whose fields sit inside a ```yaml fence, which is what
// profile-gen's `claude-md` output mode writes on purpose.
//
// The grammar this suite pins is one sentence long — **a `yaml`/`yml` fence
// inside a marked persona block is read by the front-matter arm, and every
// other fence is still skipped whole** — and almost every case below is about
// the second half of it. That asymmetry is deliberate. The positive case was
// never in doubt once the arm was wired up; what the ticket's acceptance
// criterion 2 asks is whether wiring it up turned every ```yaml example in
// every CLAUDE.md on the machine into a persona declaration, and only the
// refusals can answer that.
//
// Three facts were measured against the real skill rather than reasoned about,
// because the design rests on them:
//
//   * `templates/profile.embedded.md.j2` opens with
//     `<!-- profile-gen:start slug={{ slug }} -->`, puts `### {{ name }}` on
//     line 2, opens a ```yaml fence on line 6 and closes the region with a
//     matching `:end`. So the region is genuinely delimited — there is a real
//     end, not a guessed one.
//
//   * Running the real `PersonaMarkdown.Parse` over the real template's real
//     output resolved **nothing at all** — name, voice, picture and raw
//     picture all null, on both the full and the minimal field sets.
//
//   * Running the real `PersonaSection` over that block's own heading text
//     answered `false`: `PersonaSection("Aurora Vance")` and
//     `PersonaSection("Rho")` are both false, because the heading carries the
//     persona's *name* and the section list is
//     persona/attributes/identity/character/profile/about me/who i am. That is
//     what rules out scoping this to a persona section instead — `sectionLevel`
//     is zero for the whole block, so a section-scoped rule would fix nothing.
//     Measured by calling the predicate, not by reading its list.
//
// The fixtures here are synthetic in their values and exact in their *shape*,
// the same split PersonaFrontMatterTests makes and for the same reason: this
// repository is public, and what matters is the marker, the fence and the keys
// rather than whose face is being named. The real template's real output is the
// fixture in tests/IntegrationTests, where it belongs — a grammar table cannot
// say a real file is read, which is the gap PersonaRealFileTests exists for.
public class PersonaMarkedBlockTests
{
    // profile-gen's embedded shape, with the blank lines its optional-field
    // blocks really leave behind. Keys in the order the template writes them.
    private static string[] EmbeddedBlock(
        string slug = "test-persona",
        string name = "Test Persona",
        string image = "profiles/test-persona/test-persona.png",
        string? animated = "profiles/test-persona/test-persona.gif",
        string? voice = "af_bella") =>
        new[]
        {
            "<!-- profile-gen:start slug=" + slug + " -->",
            "### " + name,
            "",
            "![" + name + "](" + image + ")",
            "",
            "```yaml",
            "schema_version: 1",
            "name: \"" + name + "\"",
            "slug: \"" + slug + "\"",
            "image: \"" + image + "\"",
            "",
            animated is null ? "" : "image_animated: \"" + animated + "\"",
            "",
            "",
            voice is null ? "" : "voice: \"" + voice + "\"",
            "",
            "nsfw: false",
            "generation:",
            "  backend: \"mock\"",
            "  model: \"mock-v1\"",
            "  prompt: \"portrait of a person\"",
            "  negative_prompt: blurry",
            "  seed: 42",
            "  gif_mode: synthetic",
            "  created_at: \"2026-09-10T00:00:00Z\"",
            "```",
            "",
            "",
            "**Personality:** Warm, precise, and a little wry.",
            "",
            "<!-- profile-gen:end slug=" + slug + " -->",
        };

    // --- the ticket's own use case ----------------------------------------

    // The headline. Every field profile-gen declares, read out of the fence it
    // declares them in, with the still winning over the animation because it is
    // written first — the same first-valid-value-wins the `file` mode already
    // relies on.
    [Fact]
    public void ProfileGensEmbeddedShapeResolvesNameVoiceAndPicture()
    {
        var fields = PersonaMarkdown.Parse(EmbeddedBlock());

        Assert.Equal("Test Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("profiles/test-persona/test-persona.png", fields.Avatar);
        Assert.Equal("profiles/test-persona/test-persona.png", fields.RawAvatar);
    }

    // The same keys through the *same arm*, which is the implementation claim
    // worth asserting rather than assuming: a marked block and real front
    // matter carrying identical keys have to produce identical fields, or there
    // are two grammars for one tool's output and they will drift.
    [Fact]
    public void AMarkedBlockAndRealFrontMatterReadTheSameKeysTheSameWay()
    {
        var marked = PersonaMarkdown.Parse(EmbeddedBlock());

        var frontMatter = PersonaMarkdown.Parse(new[]
        {
            "---",
            "schema_version: 1",
            "name: \"Test Persona\"",
            "slug: \"test-persona\"",
            "image: \"profiles/test-persona/test-persona.png\"",
            "image_animated: \"profiles/test-persona/test-persona.gif\"",
            "voice: \"af_bella\"",
            "---",
        });

        Assert.Equal(frontMatter.Name, marked.Name);
        Assert.Equal(frontMatter.Voice, marked.Voice);
        Assert.Equal(frontMatter.Rate, marked.Rate);
        Assert.Equal(frontMatter.Avatar, marked.Avatar);
        Assert.Equal(frontMatter.RawAvatar, marked.RawAvatar);
    }

    // profile-gen's minimal set: no animation, no voice. The optional-field
    // blocks leave blank lines behind where those keys would be, and a blank
    // line inside the fence must not end anything.
    [Fact]
    public void AnEmbeddedBlockWithNeitherVoiceNorAnimationStillNamesAndDrawsThePersona()
    {
        var fields = PersonaMarkdown.Parse(
            EmbeddedBlock(slug: "rho", name: "Rho", image: "profiles/rho/rho.png",
                animated: null, voice: null));

        Assert.Equal("Rho", fields.Name);
        Assert.Null(fields.Voice);
        Assert.Equal("profiles/rho/rho.png", fields.Avatar);
    }

    // `slug:` names the agent when there is no display name, inside a marked
    // block exactly as it does in front matter.
    [Fact]
    public void SlugAloneNamesTheAgentInsideAMarkedBlock()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=test-persona -->",
            "```yaml",
            "slug: \"test-persona\"",
            "```",
            "<!-- profile-gen:end slug=test-persona -->",
        });

        Assert.Equal("test-persona", fields.Name);
    }

    // The still is written first and keeps the picture, which is not a
    // consolation prize — see the header comment on `image_animated` for why
    // reading the still is the only choice that reliably produces a portrait.
    [Fact]
    public void TheStillWinsOverTheAnimationInsideAMarkedBlockToo()
    {
        var fields = PersonaMarkdown.Parse(EmbeddedBlock());

        Assert.Equal("profiles/test-persona/test-persona.png", fields.Avatar);
    }

    // A voice blend, which is the shape this repository's own persona uses, read
    // through the marked block. Front matter applies no word bound of its own —
    // a YAML scalar is one whole value — so the blend survives here for the
    // same reason it survives in `file` mode.
    [Fact]
    public void ABlendedVoiceSurvivesTheMarkedBlockUnchanged()
    {
        var fields = PersonaMarkdown.Parse(
            EmbeddedBlock(voice: "50% sky and 50% nicole"));

        Assert.Equal("50% sky and 50% nicole", fields.Voice);
    }

    // --- the false positive this must not introduce ------------------------

    // **Acceptance criterion 2, and the whole reason the marker is the signal
    // rather than the info string alone.** A CLAUDE.md showing somebody how to
    // write a config file is the commonest ```yaml block there is, and reading
    // it would rename an orb out of documentation.
    [Fact]
    public void AYamlBlockOutsideAnyMarkerIsStillSkippedWhole()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Working in this repository",
            "",
            "Configure the service like this:",
            "",
            "```yaml",
            "name: \"Sample Service\"",
            "slug: \"sample-service\"",
            "image: \"docs/diagram.png\"",
            "voice: \"af_bella\"",
            "```",
            "",
            "Then restart it.",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // The same refusal one step subtler: the example sits under a heading that
    // *does* open a persona section, so it is the case a section-scoped rule
    // would have read and this one does not.
    [Fact]
    public void AYamlExampleUnderAPersonaHeadingIsStillNotAPersona()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "",
            "A persona file looks like this:",
            "",
            "```yaml",
            "name: \"Sample Persona\"",
            "voice: \"af_bella\"",
            "```",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
    }

    // A fence inside the region whose info string is not YAML.
    //
    // **The decision, and why:** it is skipped, exactly as it would be outside
    // the region. What the marker declares is a region that *describes* a
    // persona — not that every fence inside it holds metadata. A persona block
    // is free to carry a ```bash snippet showing how the agent is launched, or
    // a ```json sample of what it returns, and neither of those is a field
    // statement however many recognised keys it happens to contain. Reading
    // them would reintroduce the false positive the marker exists to prevent,
    // one scope smaller.
    [Fact]
    public void AFenceInsideTheRegionThatIsNotYamlIsSkippedLikeAnyOther()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```bash",
            "name: \"Not A Persona\"",
            "voice: \"af_bella\"",
            "image: \"shell.png\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // A bare fence carries no info string at all, and "" is not a language any
    // caller recognises — so a second, undeclared block in the same region is
    // skipped while the yaml one above it is read.
    //
    // What this does *not* prove, and it is worth saying so rather than letting
    // the shape imply it: it is not a test that the parser clears a closed
    // fence's language. A bare "```" sets the language to "" on the way in by
    // itself, so this case would stay green under a parser that never cleared
    // anything. The claim here is only the one asserted — the language, not the
    // region, is what decides a fence.
    [Fact]
    public void AnUndeclaredFenceAfterAYamlOneInTheSameRegionIsNotReadAsYaml()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "voice: \"af_bella\"",
            "```",
            "",
            "```",
            "name: \"Not A Persona\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Null(fields.Name);
    }

    // The region closes where its `:end` says it closes, and a ```yaml block
    // after it is documentation again.
    [Fact]
    public void AYamlBlockAfterTheRegionsEndIsBackToBeingAnExample()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=test-persona -->",
            "```yaml",
            "name: \"Test Persona\"",
            "```",
            "<!-- profile-gen:end slug=test-persona -->",
            "",
            "And here is what one looks like:",
            "",
            "```yaml",
            "voice: \"af_bella\"",
            "image: \"docs/example.png\"",
            "```",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // A marker *inside* a fence is a CLAUDE.md documenting the format — the
    // profile-gen skill's own README does exactly this — and it must not open a
    // region. If it did, a document explaining the shape would declare one.
    //
    // The yaml block is deliberately *after* the example rather than nested
    // inside it, and that is the whole reason this case is a measurement. This
    // parser's fence tracking is a single toggle with no memory of which
    // character opened it, so a nested ```yaml would close the outer fence
    // rather than sit inside it — and a test built that way would pass for a
    // reason that has nothing to do with the gate under test. Written like
    // this, deleting the gate makes the shown marker open a real region and
    // this case goes red.
    [Fact]
    public void AMarkerInsideAFenceDocumentsTheFormatRatherThanDeclaringOne()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "The embedded mode opens with a marker like this:",
            "",
            "```text",
            "<!-- profile-gen:start slug=sample -->",
            "```",
            "",
            "And a persona's fields look like this:",
            "",
            "```yaml",
            "name: \"Sample Persona\"",
            "voice: \"af_bella\"",
            "```",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
    }

    // An HTML comment that is not a marker opens nothing, whatever else it
    // says. Four shapes, each refused at a different point in the read.
    [Theory]
    [InlineData("<!-- a note about the persona -->")]   // no colon at all
    [InlineData("<!-- :start -->")]                     // a colon, but no tool before it
    [InlineData("<!-- some-other-tool:start -->")]      // a tool nobody here knows
    [InlineData("<!-- persona:middle -->")]             // a known tool, an unknown verb
    [InlineData("<!-->")]                               // both ends of a comment, overlapping
    [InlineData("<!-- persona:start")]                  // an opening with no closing
    public void AnHtmlCommentThatIsNotAMarkerOpensNoRegion(string comment)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            comment,
            "```yaml",
            "name: \"Not A Persona\"",
            "voice: \"af_bella\"",
            "```",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
    }

    // A marker inside YAML front matter opens nothing either. Front matter is
    // already read as fields, so a region declared there would be a region with
    // nothing left to give — and this keeps front matter meaning exactly what
    // it meant before CB-141.
    [Fact]
    public void AMarkerInsideFrontMatterChangesNothingAboutFrontMatter()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "<!-- persona:start -->",
            "name: \"Test Persona\"",
            "---",
            "",
            "```yaml",
            "voice: \"af_bella\"",
            "```",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Null(fields.Voice);
    }

    // A stray `:end` with no `:start` before it closes nothing, because nothing
    // was open — and crucially does not *open* one by being mistaken for a
    // toggle.
    [Fact]
    public void AnEndMarkerWithNoStartBeforeItOpensNothing()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:end -->",
            "```yaml",
            "name: \"Not A Persona\"",
            "```",
        });

        Assert.Null(fields.Name);
    }

    // --- the shapes a marker can take -------------------------------------

    // The tool-neutral spelling, which is what keeps this repository's contract
    // "a marked persona block" rather than "whatever profile-gen emits". A
    // second generator, or a person writing one by hand, should not have to
    // spell another project's name to be understood.
    [Fact]
    public void TheToolNeutralPersonaSpellingIsReadTheSameWay()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "name: \"Test Persona\"",
            "voice: \"af_bella\"",
            "image: \"portrait.png\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("portrait.png", fields.Avatar);
    }

    // Case is not a signal here, for the same reason it is not a signal on any
    // other label in this grammar.
    [Fact]
    public void AMarkerIsRecognisedWhateverItsCase()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- PROFILE-GEN:START slug=test-persona -->",
            "```YAML",
            "name: \"Test Persona\"",
            "```",
            "<!-- Profile-Gen:End slug=test-persona -->",
        });

        Assert.Equal("Test Persona", fields.Name);
    }

    // Whatever a tool writes after the directive is its own bookkeeping.
    // profile-gen writes a `slug=`; a marker whose trailing attributes this
    // parser insisted on understanding would be a marker that stops working the
    // next time the tool adds one.
    [Theory]
    [InlineData("<!-- persona:start -->")]
    [InlineData("<!-- profile-gen:start slug=test-persona -->")]
    [InlineData("<!-- profile-gen:start slug=test-persona version=3 nsfw=false -->")]
    public void WhateverFollowsTheDirectiveIsTheToolsOwnBookkeeping(string marker)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            marker,
            "```yaml",
            "name: \"Test Persona\"",
            "```",
        });

        Assert.Equal("Test Persona", fields.Name);
    }

    // --- the shapes a fence can take --------------------------------------

    // Both spellings of the language, and the attribute syntax CommonMark lets
    // a fence carry after it. Only the first word is the language.
    [Theory]
    [InlineData("```yaml")]
    [InlineData("```yml")]
    [InlineData("```YAML")]
    [InlineData("```yaml {.wrap}")]
    [InlineData("```yaml title=\"persona\"")]
    [InlineData("~~~yaml")]
    [InlineData("````yaml")]
    public void EveryWayOfDeclaringAYamlFenceIsRead(string opening)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            opening,
            "name: \"Test Persona\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Equal("Test Persona", fields.Name);
    }

    // A language nobody asked for is skipped, whatever it is.
    [Theory]
    [InlineData("```json")]
    [InlineData("```bash")]
    [InlineData("```")]
    [InlineData("```yamlish")]
    public void AFenceDeclaringSomethingElseIsSkipped(string opening)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            opening,
            "name: \"Not A Persona\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Null(fields.Name);
    }

    // --- malformed input, and what it does instead of nothing --------------

    // **The decision on an unterminated region, and why:** it runs to the end
    // of the file, and the persona inside it still resolves.
    //
    // The marker is an explicit declaration by whoever wrote the file. If the
    // `:end` is gone — a hand-edit truncated the block, a merge ate the last
    // line — the fields above it were still declared on purpose, and refusing
    // them would mean a lightly-damaged file silently produces no persona,
    // which is the exact failure class this ticket exists to fix. Losing a
    // field rather than guessing at one is the rule everywhere else in this
    // grammar; here there is nothing to guess at, because the fields are
    // written out in full.
    //
    // The alternative — refuse a region with no end — also costs a second pass
    // over every CLAUDE.md up a session's directory tree, on every change, to
    // catch a malformed-input case. That is a real price for a worse answer.
    [Fact]
    public void AMarkedBlockWithNoEndRunsToTheEndOfTheFile()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=test-persona -->",
            "### Test Persona",
            "",
            "```yaml",
            "name: \"Test Persona\"",
            "voice: \"af_bella\"",
            "image: \"portrait.png\"",
            "```",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("portrait.png", fields.Avatar);
    }

    // A fence that never closes swallows the rest of the file, which is what a
    // Markdown renderer does with one too. The fields written before the damage
    // are still read; the `:end` marker that ends up inside the unterminated
    // fence closes nothing, because a marker inside a fence is not a marker.
    [Fact]
    public void AFenceThatNeverClosesStillYieldsTheFieldsWrittenInsideIt()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "name: \"Test Persona\"",
            "voice: \"af_bella\"",
            "<!-- persona:end -->",
            "",
            "Everything here is still inside the fence.",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
    }

    // Two regions in one file, which is what a CLAUDE.md looks like after
    // profile-gen has been run twice. First statement wins, the same as
    // everywhere else in this grammar — a persona does not become a different
    // persona further down the page.
    [Fact]
    public void TheFirstOfTwoMarkedBlocksWinsEveryField()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=first -->",
            "```yaml",
            "name: \"First Persona\"",
            "voice: \"af_bella\"",
            "image: \"first.png\"",
            "```",
            "<!-- profile-gen:end slug=first -->",
            "",
            "<!-- profile-gen:start slug=second -->",
            "```yaml",
            "name: \"Second Persona\"",
            "voice: \"af_nicole\"",
            "image: \"second.png\"",
            "```",
            "<!-- profile-gen:end slug=second -->",
        });

        Assert.Equal("First Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("first.png", fields.Avatar);
    }

    // A field the first region leaves out is still available to the second.
    // First-*valid-value*-wins, not first-region-wins: `??=` is per field.
    [Fact]
    public void AFieldTheFirstBlockOmitsIsStillTakenFromTheSecond()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "name: \"First Persona\"",
            "```",
            "<!-- persona:end -->",
            "<!-- persona:start -->",
            "```yaml",
            "name: \"Second Persona\"",
            "voice: \"af_nicole\"",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Equal("First Persona", fields.Name);
        Assert.Equal("af_nicole", fields.Voice);
    }

    // --- what the region does *not* change --------------------------------

    // The three lines profile-gen writes outside the fence still carry nothing,
    // which is the finding the ticket's own comments spell out: a `###` heading
    // labels what follows rather than stating it, and a Markdown inline image is
    // not a bullet, a bold field, a table row or a front-matter key. Being
    // inside a marked block does not change that — the marker widens the fence
    // rule and nothing else.
    [Fact]
    public void AHeadingAndAnInlineImageInsideTheRegionStillCarryNoField()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- profile-gen:start slug=test-persona -->",
            "### Test Persona",
            "",
            "![Test Persona](profiles/test-persona/test-persona.png)",
            "",
            "**Personality:** Warm, precise, and a little wry.",
            "",
            "<!-- profile-gen:end slug=test-persona -->",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // Prose inside a marked block is still prose, and still read on exactly the
    // terms it was read on before: this sentence is a sentence wherever it sits,
    // and the region neither enables nor suppresses it.
    [Fact]
    public void ProseInsideTheRegionMeansWhatItMeantOutsideIt()
    {
        var inside = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "Her name is Leota.",
            "<!-- persona:end -->",
        });

        var outside = PersonaMarkdown.Parse(new[] { "Her name is Leota." });

        Assert.Equal("Leota", inside.Name);
        Assert.Equal(outside.Name, inside.Name);
    }

    // The keys that are *not* fields stay not-fields inside the fence. The
    // template writes a nested `generation:` mapping whose sub-keys — `prompt`,
    // `model`, `backend`, `seed` — are ordinary YAML and name nothing here, and
    // a reader that took `prompt:` for something would put a paragraph of image
    // prompt on an orb.
    [Fact]
    public void TheGenerationBlockNamesNothing()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "schema_version: 1",
            "nsfw: false",
            "generation:",
            "  backend: \"mock\"",
            "  model: \"mock-v1\"",
            "  prompt: \"portrait of a person\"",
            "  seed: 42",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // A value YAML quotes is unquoted once, and a placeholder is refused, both
    // by the arm this shape now shares. Asserted here rather than assumed
    // because "the same arm" is the design claim and a shape that reached it
    // differently would be a second grammar wearing the first one's name.
    [Fact]
    public void QuotingAndPlaceholdersBehaveAsTheyDoInFrontMatter()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "<!-- persona:start -->",
            "```yaml",
            "name: '<your name here>'",
            "voice: 'af_bella'",
            "```",
            "<!-- persona:end -->",
        });

        Assert.Null(fields.Name);
        Assert.Equal("af_bella", fields.Voice);
    }
}
