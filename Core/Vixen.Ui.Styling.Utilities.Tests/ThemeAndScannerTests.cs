// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The token config, the scanner, and <c>@apply</c>.</summary>
public class ThemeAndScannerTests {
    [Fact]
    public void The_theme_from_doc_09_reads_exactly_as_written() {
        // Doc 09's worked example, used verbatim so that the test reads against the plan rather
        // than against a convenient simplification of it.
        var tokens = ThemeTokens.Parse(UtilityFixture.Theme);

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal(4f, tokens.SpacingBase);
        Assert.Equal(DarkModeStrategy.Media, tokens.DarkMode);

        Assert.Equal("#17171d", tokens.Colors["surface-2"]);
        Assert.Equal("#8a8a99", tokens.Colors["muted"]);
        Assert.Equal("8px", tokens.Radius["lg"]);
        Assert.Equal(600f, tokens.FontWeight["semibold"]);
        Assert.Equal(768f, tokens.Screens["md"]);
        Assert.Equal(new FontSizeToken(17f, 24f), tokens.FontSize["lg"]);
    }

    /// <summary>The engine's shipped tokens are v4's, in the oklch they were authored in.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted against the text and not against a colour object, on purpose.</b> Two of
    ///     every three v4 colours are outside sRGB; anything that compared them by converting first
    ///     would be comparing whatever the conversion clamped to, and would pass with the chroma
    ///     already thrown away. See docs/plan/43 § D4, which records that exact trap costing real
    ///     time here once already.
    /// </remarks>
    [Fact]
    public void The_shipped_default_is_Tailwind_v4s_own_theme() {
        var tokens = ThemeTokens.CreateDefault();

        Assert.Empty(tokens.Diagnostics);

        Assert.Equal("oklch(62.3% 0.214 259.815)", tokens.Colors["blue-500"]);
        Assert.Equal("oklch(69.6% 0.17 162.48)", tokens.Colors["emerald-500"]);
        Assert.Equal("#fff", tokens.Colors["white"]);

        // 26 ramps of 11, plus black and white.
        Assert.Equal((26 * 11) + 2, tokens.Colors.Count);

        // `rem` is resolved at 16px, because there is no root font size downstream to resolve it
        // against — the one documented divergence from v4's emission.
        Assert.Equal(4f, tokens.SpacingBase);
        Assert.Equal("8px", tokens.Radius["lg"]);
        Assert.Equal(768f, tokens.Screens["md"]);

        // `--text-sm: 0.875rem` with `--text-sm--line-height: calc(1.25 / 0.875)`: 14px, and a ratio
        // of 1.4286 multiplied out rather than carried.
        Assert.Equal(new FontSizeToken(14f, 20f), tokens.FontSize["sm"]);
    }

    /// <summary>A namespace can be emptied, which is how a project opts out of the shipped default.</summary>
    /// <remarks>
    ///     ⚠ <b>And the second half is the one that could regress silently.</b> Clearing has to leave
    ///     the <i>other</i> namespaces alone: a <c>--color-*: initial;</c> that also took the radii
    ///     with it would look correct in every colour test and quietly square every corner in the
    ///     application.
    /// </remarks>
    [Fact]
    public void Clearing_a_namespace_empties_that_one_and_no_other() {
        var tokens = ThemeTokens.Parse("@theme { --color-*: initial; --color-brand: #123456; }");

        Assert.Equal("#123456", Assert.Single(tokens.Colors).Value);
        Assert.False(tokens.Colors.ContainsKey("blue-500"));
        Assert.Equal("8px", tokens.Radius["lg"]);
        Assert.Equal(768f, tokens.Screens["md"]);
    }

    /// <summary>One token can be cleared on its own, and the rest of its namespace stays.</summary>
    [Fact]
    public void Clearing_one_token_leaves_its_neighbours() {
        var tokens = ThemeTokens.Parse("@theme { --color-blue-500: initial; }");

        Assert.False(tokens.Colors.ContainsKey("blue-500"));
        Assert.True(tokens.Colors.ContainsKey("blue-600"));
    }

    /// <summary><c>--*: initial;</c> takes everything, which is what the test fixtures want.</summary>
    [Fact]
    public void Clearing_everything_leaves_nothing_but_the_defaults_of_the_scalars() {
        var tokens = ThemeTokens.Parse("@theme { --*: initial; }");

        Assert.Empty(tokens.Colors);
        Assert.Empty(tokens.Radius);
        Assert.Empty(tokens.FontSize);
        Assert.Empty(tokens.Screens);
        Assert.Empty(tokens.Shadow);
        Assert.Empty(tokens.Variables);
    }

    /// <summary>A later block wins, which is what "layer your theme over the default" means.</summary>
    [Fact]
    public void A_later_block_beats_an_earlier_one() {
        var tokens = ThemeTokens.Parse("@theme { --color-brand: #111111; }\n@theme { --color-brand: #222222; }");

        Assert.Equal("#222222", tokens.Colors["brand"]);
    }

    /// <summary>A font size may omit its line height, and 1.4 is what "no opinion" is worth.</summary>
    [Fact]
    public void A_font_size_may_omit_its_line_height() {
        // ⚠ Cleared first, because the shipped default gives `base` a line-height ratio of 1.5 and
        // the point of this test is the *absence* of an opinion. Over the default the answer would
        // be 21 and the test would be measuring v4's type scale.
        var tokens = ThemeTokens.Parse("@theme { --*: initial; --text-base: 14px; }");

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal(14f, tokens.FontSize["base"].Size);
        Assert.Equal(20f, tokens.FontSize["base"].LineHeight);
    }

    /// <summary>
    ///     ⚠ <b>The line height may be written before the size, and the answer is the same.</b> Which
    ///     order two declarations appear in is the author's business, and a reader that folded the
    ///     pair as each arrived would have nowhere to put a ratio whose size has not landed — it
    ///     would sit in the line-height slot, and the size arriving second could not tell it from a
    ///     length.
    /// </summary>
    [Theory]
    [InlineData("--text-sm: 0.875rem; --text-sm--line-height: calc(1.25 / 0.875);")]
    [InlineData("--text-sm--line-height: calc(1.25 / 0.875); --text-sm: 0.875rem;")]
    public void A_ratio_and_its_size_may_be_written_in_either_order(string declarations) {
        var tokens = ThemeTokens.Parse($"@theme {{ --*: initial; {declarations} }}");

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal(new FontSizeToken(14f, 20f), tokens.FontSize["sm"]);
    }

    /// <summary>
    ///     ⚠ <b>A unitless line height is a ratio and one with a unit is a length</b>, which is CSS's
    ///     own distinction rather than a heuristic. v4 writes every one of its own as a ratio; a
    ///     theme carrying a designer's pixel pairs writes lengths, and both have to land.
    /// </summary>
    [Fact]
    public void A_line_height_with_a_unit_is_a_length_and_not_a_multiplier() {
        var tokens = ThemeTokens.Parse("@theme { --text-sm: 11px; --text-sm--line-height: 16px; }");

        Assert.Equal(new FontSizeToken(11f, 16f), tokens.FontSize["sm"]);
    }

    /// <summary>
    ///     ⚠ <b>A radius token can be a reference now, and that is the whole of a limitation the
    ///     editor's theme file carried in its own comments.</b> <c>ThemeTokens.Radius</c> was a
    ///     <c>Dictionary&lt;string, float&gt;</c>, so <c>var(--radius-row)</c> was rejected with a
    ///     diagnostic rather than stored — and the editor, whose three radii are custom properties on
    ///     the root, therefore declared no radius tokens at all.
    /// </summary>
    [Fact]
    public void A_radius_token_may_be_a_reference_rather_than_a_number() {
        var tokens = ThemeTokens.Parse("@theme { --radius-row: var(--radius-row); }");

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal("var(--radius-row)", tokens.Radius["row"]);
    }

    [Fact]
    public void A_token_that_is_not_what_it_should_be_is_reported_rather_than_guessed_at() {
        var tokens = ThemeTokens.Parse("@theme { --*: initial; --spacing: wide; --breakpoint-md: soon; }");

        Assert.Equal(2, tokens.Diagnostics.Count);
        Assert.Empty(tokens.Screens);
    }

    /// <summary>
    ///     ⚠ <b>Text that is not a theme is not an error, and that is the difference the format
    ///     makes.</b> A YAML reader had a cliff — a malformed tag threw out of it and stopped whoever
    ///     was building a stylesheet — and its own remarks called that the likeliest failure of all.
    ///     A stylesheet has no cliff: a block that does not close is a block that yields no
    ///     declarations, and a file with no <c>@theme</c> in it is most files.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(".card { color: red; }")]
    [InlineData("@theme { --color-brand: #fff")]
    [InlineData("@theme")]
    public void Text_that_declares_no_tokens_is_not_a_failure(string css) {
        var tokens = ThemeTokens.Parse(css);

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal("oklch(62.3% 0.214 259.815)", tokens.Colors["blue-500"]);
    }

    /// <summary>Comments and wrapped values survive the scan, because v4's own file has both.</summary>
    [Fact]
    public void A_comment_and_a_wrapped_value_read_the_same_as_one_line() {
        var tokens = ThemeTokens.Parse(
            """
            @theme {
                /* a colour; with a brace { and a semicolon inside the comment */
                --color-brand:
                    #123456;
                --shadow-two: 0 1px 2px rgb(0 0 0 / 0.1),
                    0 2px 4px rgb(0 0 0 / 0.1);
            }
            """);

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal("#123456", tokens.Colors["brand"]);
        Assert.Equal("0 1px 2px rgb(0 0 0 / 0.1), 0 2px 4px rgb(0 0 0 / 0.1)", tokens.Shadow["two"]);
    }

    /// <summary>
    ///     ⚠ <b>The whole deliverable, end to end and through the real cascade: a class name nobody
    ///     configured reaches an element as a colour outside sRGB, and the mapper repairs it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every link is real — the shipped <c>@theme</c>, the generator, the style engine, the
    ///         parser and <c>GamutMap</c> — because each of them is a place the chroma could quietly
    ///         be thrown away and none of them would fail if it were.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The out-of-gamut assertion is the load-bearing one, and it is here because a
    ///         reference that clamps looks exactly like a reference that agrees.</b> doc 43 § D4
    ///         records that trap costing real time: a test asserting three v4 colours were fine
    ///         <i>looked</i> verified against an implementation that clamped before printing. Two of
    ///         these three are outside sRGB — <c>blue-500</c> past white in linear blue,
    ///         <c>emerald-500</c> past black in linear red — and a palette transcribed to hex would
    ///         pass "the utility resolves" while having deleted the thing worth shipping.
    ///     </para>
    ///     <para>
    ///         The repair is asserted as a <i>property</i> rather than against numbers: what the
    ///         specification promises is that the mapped colour is showable and that its hue is held
    ///         while chroma is reduced. Pinning the mapper's own output here would restate
    ///         <c>GamutMapTests</c> and would fail for a reason unrelated to the palette.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("blue-500", false)]
    [InlineData("emerald-500", false)]
    [InlineData("red-500", true)]
    public void A_shipped_colour_reaches_an_element_unclamped_and_is_mapped_at_presentation(string token, bool showable) {
        var fixture = new UtilityFixture(string.Empty);
        var computed = fixture.Computed([$"bg-{token}"], "background-color");

        Assert.NotNull(computed);

        var colour = new StyleValueParser(new NameTable(), new NameTable()).Parse(computed);
        var linear = new Vector3(colour.Color.R, colour.Color.G, colour.Color.B);

        Assert.Equal(StyleValueKind.Color, colour.Kind);
        Assert.Equal(showable, GamutMap.InGamut(linear, ColorGamut.Srgb));

        var mapped = GamutMap.Map(linear, ColorGamut.Srgb);

        Assert.True(GamutMap.InGamut(mapped, ColorGamut.Srgb), "the mapper returned a colour that still cannot be shown");

        // A colour already inside the gamut is returned untouched — the early-out — and one outside
        // it comes back different, which is the pair that says the mapping happened rather than that
        // the branch was never taken.
        Assert.Equal(showable, mapped == linear);
    }

    /// <summary>Stripping takes the block and leaves everything around it.</summary>
    [Fact]
    public void Stripping_removes_the_block_and_nothing_else() {
        const string css = ".a { color: red; }\n@theme { --color-brand: #fff; }\n.b { color: blue; }";

        Assert.Equal(".a { color: red; }\n\n.b { color: blue; }", ThemeTokens.Strip(css));
    }

    [Fact]
    public void The_scanner_finds_class_names_wherever_they_are_written() {
        var found = new HashSet<string>(StringComparer.Ordinal);

        CandidateScanner.Scan(
            """
            <div class="flex items-center gap-2 p-4">
                <Text class="text-lg font-semibold">@Title</Text>
            </div>
            """,
            found
        );

        CandidateScanner.Scan("""var classes = "bg-accent hover:bg-accent-hover";""", found);

        foreach (var expected in new[] {
            "flex", "items-center", "gap-2", "p-4", "text-lg", "font-semibold",
            "bg-accent", "hover:bg-accent-hover"
        }) {
            Assert.Contains(expected, found);
        }
    }

    [Fact]
    public void The_scanner_keeps_an_arbitrary_value_whole() {
        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.Scan("""<div class="w-[37px] grid-cols-[1fr_auto]">""", found);

        Assert.Contains("w-[37px]", found);
        Assert.Contains("grid-cols-[1fr_auto]", found);
    }

    [Fact]
    public void The_scanner_is_over_inclusive_on_purpose_and_the_generator_throws_the_rest_away() {
        // A false positive costs one unused rule that no element matches; a false negative is a
        // style silently missing at runtime, which someone debugs for an hour. The asymmetry is
        // enormous and the design follows it.
        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.Scan("// this comment mentions flex and p-4 and also the-word-container", found);

        Assert.Contains("flex", found);
        Assert.Contains("p-4", found);

        var fixture = new UtilityFixture();
        fixture.Generator.Generate(found);

        Assert.Equal(2, fixture.Generator.RuleCount);
    }

    /// <summary>
    ///     ⚠ <b>An unused rule is free by the scanner's own argument and a malformed one is not.</b>
    ///     <c>text[1..]</c> is a C# range expression, and every build of <c>Vixen.Editor.Ui</c> turned
    ///     it into <c>.text\[1\.\.\] { font-size: 1..; }</c> — a declaration ExCSS drops without a
    ///     word. That is not one more unused rule: it is a dropped declaration in the generated sheet,
    ///     indistinguishable from the real parse failure the next person is looking for. The refusal
    ///     has to produce <i>no rule</i>, not an empty one, which is why this asserts on the whole
    ///     sheet and not only on the absence of the value.
    /// </summary>
    [Fact]
    public void An_arbitrary_value_that_is_not_css_produces_no_rule_at_all() {
        var fixture = new UtilityFixture();
        var css = fixture.Generate("text[1..]", "w-[37px]");

        Assert.DoesNotContain("1..", css, StringComparison.Ordinal);
        Assert.DoesNotContain("text", css, StringComparison.Ordinal);
        Assert.Contains(".w-\\[37px\\] { width: 37px; }", css, StringComparison.Ordinal);

        Assert.Equal(1, fixture.Generator.RuleCount);
        Assert.Contains("text[1..]", fixture.Generator.Unrecognised);
    }

    /// <summary>
    ///     ⚠ <b>The guard is a token-shape test and must never become a CSS value parser.</b> These
    ///     are the forms Tailwind genuinely allows in brackets, and each one is a way the escape hatch
    ///     would stop being an escape hatch if the test grew opinions: a variable reference, a
    ///     function whose body it does not understand, a hex colour, a dimension, a percentage, a grid
    ///     unit, a string. <c>font-size: red</c> is nonsense CSS refuses and this accepts, deliberately
    ///     — deciding otherwise needs a table of every property's grammar.
    /// </summary>
    [Theory]
    [InlineData("w-[var(--x)]", "width: var(--x)")]
    [InlineData("w-[calc(100%-2rem)]", "width: calc(100%-2rem)")]
    [InlineData("border-[#f00]", "border-color: #f00")]
    [InlineData("border-[3px]", "border-width: 3px")]
    [InlineData("w-[50%]", "width: 50%")]
    [InlineData("w-[1fr]", "width: 1fr")]
    [InlineData("w-[.5em]", "width: .5em")]
    [InlineData("w-[1e3px]", "width: 1e3px")]
    [InlineData("bg-[url(a/b2.png)]", "background-color: url(a/b2.png)")]
    [InlineData("text-['a.b']", "font-size: 'a.b'")]
    public void A_bracketed_value_tailwind_allows_is_still_emitted(string candidate, string declaration) {
        Assert.Equal([declaration], new UtilityFixture().Emits(candidate));
    }

    /// <summary>The shapes that are not CSS at all, and so name no rule.</summary>
    /// <remarks>
    ///     Each is what the over-inclusive scan hands the generator out of ordinary source text: a
    ///     range, a slice, a stray delimiter. <c>1..</c> is the one that reached a shipped sheet.
    /// </remarks>
    [Theory]
    [InlineData("text[1..]")]
    [InlineData("w[2..3]")]
    [InlineData("w-[.]")]
    [InlineData("w-[calc(100%-2rem]")]
    [InlineData("w-[')]")]
    [InlineData("w-[ ]")]
    public void A_bracketed_value_that_is_not_css_names_no_utility(string candidate) {
        Assert.Null(new UtilityFixture().Declarations(candidate));
    }

    /// <summary>
    ///     ⚠ <b>Seven of the editor's twenty-five rules were CSS keywords, not class names.</b>
    ///     <c>.absolute</c>, <c>.block</c>, <c>.grid</c>, <c>.hidden</c>, <c>.inline</c>,
    ///     <c>.relative</c> and <c>.static</c> came out of <c>position: absolute</c> and friends in the
    ///     editor's own sheets, because the scanner globs <c>**/*.vcss</c> and parses nothing. A class
    ///     name cannot be <i>used</i> from the right of a colon, so skipping a declaration's value
    ///     costs nothing — and the narrowing is scoped to stylesheet input, which
    ///     <see cref="Source_text_is_not_narrowed_the_way_a_stylesheet_is" /> is the other half of.
    /// </summary>
    [Fact]
    public void A_declaration_value_in_a_stylesheet_is_not_a_class_name() {
        var found = new HashSet<string>(StringComparer.Ordinal);

        CandidateScanner.ScanStyleSheet(
            """
            @layer components {
                tree-row { position: absolute; display: block; }
                tree-row:hover data-grid { border-radius: 2px; }
            }
            """,
            found
        );

        Assert.DoesNotContain("absolute", found);
        Assert.DoesNotContain("block", found);
        Assert.DoesNotContain("2px", found);

        // The selector is not a value, and a `{` is what says so: the run that looked like a property
        // name ran into an opening brace, so nothing was dropped.
        Assert.Contains("tree-row:hover", found);
        Assert.Contains("data-grid", found);
    }

    /// <summary>
    ///     ⚠ <b><c>@apply p-4 flex;</c> is a statement inside a block whose value <i>is</i> a list of
    ///     class names, and it is the exception the exclusion has to get right.</b> The test is
    ///     written against a variant, because that is the form with a colon in it — sabotage the rule
    ///     to "skip from any <c>:</c> inside a block" and <c>flex</c> disappears, which is exactly the
    ///     silent false negative the over-inclusive design exists to prevent. Verified by doing it:
    ///     with <c>IsDeclaration</c> made to return true, this test fails on <c>flex</c> and
    ///     <see cref="A_declaration_value_in_a_stylesheet_is_not_a_class_name" /> still passes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Exercised against the scanner and the expander directly, because the path is not live
    ///     in this tree.</b> <c>ApplyExpander</c> runs only over files named by
    ///     <c>@(VixenStyleBase)</c> and no project sets that item — every hand-written sheet reaches
    ///     the runtime as a raw <c>EmbeddedResource</c> — so no real build would demonstrate it. A
    ///     reader should not conclude from a green test here that <c>@apply</c> works end to end;
    ///     <c>Vixen.Ui.Styling.Utilities/README.md</c> records why it does not.
    /// </remarks>
    [Fact]
    public void Apply_is_not_a_declaration_and_its_utilities_survive_the_scan() {
        const string sheet = ".card { @apply p-4 hover:bg-accent flex; color: red; }";

        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.ScanStyleSheet(sheet, found);

        Assert.Contains("p-4", found);
        Assert.Contains("hover:bg-accent", found);
        Assert.Contains("flex", found);

        // And `red` is gone, which is the rule the exception is an exception to.
        Assert.DoesNotContain("red", found);

        // The expander reads the same text and agrees about which of the three it can place.
        var expander = new ApplyExpander(ThemeTokens.Parse(UtilityFixture.Theme));
        var expanded = expander.Expand(sheet);

        Assert.Contains("padding: 16px;", expanded, StringComparison.Ordinal);
        Assert.Contains("display: flex;", expanded, StringComparison.Ordinal);
        Assert.Contains("variant", Assert.Single(expander.Diagnostics), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A comment is scanned, and a comment in this tree can end where nobody meant it to.</b>
    ///     CSS closes a comment at its first <c>*/</c>, and prose about a glob — <c>**/*.vcss</c> —
    ///     contains one, so everything after it is loose text with sentence colons in it. A rule that
    ///     skipped from any colon swallowed the rest of the paragraph and lost <c>rounded-md</c> out of
    ///     the editor's own theme file. Requiring an identifier immediately before the colon is what
    ///     tells a property name from a sentence.
    /// </summary>
    /// <remarks>
    ///     ⚠ The declaration after such a comment is <i>also</i> left alone, because the statement the
    ///     tracker is following started inside the wreckage and does not end until the next <c>;</c>.
    ///     That is over-inclusion, which is the side this design errs on: one unused rule for a value
    ///     that happened to look like a class name, and never a missing style.
    /// </remarks>
    [Fact]
    public void A_sentence_after_an_early_closed_comment_is_still_scanned() {
        var found = new HashSet<string>(StringComparer.Ordinal);

        CandidateScanner.ScanStyleSheet(
            """
            @theme {
                --radius-xs: 2px;

                /* The scanner globs **/*.vcss and parses nothing: a panel wanting `rounded-md`
                   should have it. */
                --radius-md: 6px;
            }
            """,
            found
        );

        Assert.Contains("rounded-md", found);

        // The declaration before the comment is still read as one.
        Assert.DoesNotContain("2px", found);
    }

    /// <summary>
    ///     The other half of the narrowing: it is scoped to stylesheets and nothing else. A colon in
    ///     C# is a ternary, a label, a named argument or a string, and none of them says a class name
    ///     is not on the other side of it.
    /// </summary>
    [Fact]
    public void Source_text_is_not_narrowed_the_way_a_stylesheet_is() {
        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.Scan("""{ var css = "position: absolute"; element.AddClass(on ? "flex" : "hidden"); }""", found);

        Assert.Contains("absolute", found);
        Assert.Contains("flex", found);
        Assert.Contains("hidden", found);
    }

    [Fact]
    public void Apply_writes_the_utilities_out_where_they_were_written() {
        var expander = new ApplyExpander(ThemeTokens.Parse(UtilityFixture.Theme));
        var expanded = expander.Expand(".card { @apply flex items-center gap-2; border-radius: 2px }");

        Assert.Empty(expander.Diagnostics);
        Assert.Contains("display: flex;", expanded, StringComparison.Ordinal);
        Assert.Contains("align-items: center;", expanded, StringComparison.Ordinal);
        Assert.Contains("gap: 8px;", expanded, StringComparison.Ordinal);
        Assert.Contains("border-radius: 2px", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expanded_apply_is_a_stylesheet_the_engine_can_load() {
        var tokens = ThemeTokens.Parse(UtilityFixture.Theme);
        var expander = new ApplyExpander(tokens);

        var engine = new StyleEngine();
        engine.Load(expander.Expand(".card { @apply p-4 bg-surface-2 }"));

        var element = engine.Tree.CreateElement("div", classNames: ["card"]);
        var style = engine.Resolver.Resolve(engine.Tree, element);

        Assert.Equal("16px", Read(engine, style, "padding-left"));
        Assert.Equal("rgb(23, 23, 29)", Read(engine, style, "background-color"));
    }

    [Fact]
    public void Apply_without_a_trailing_semicolon_still_ends_at_the_block() {
        var expander = new ApplyExpander(ThemeTokens.Parse(UtilityFixture.Theme));
        var expanded = expander.Expand(".card { @apply p-4 }");

        Assert.Empty(expander.Diagnostics);
        Assert.Contains("padding: 16px;", expanded, StringComparison.Ordinal);
        Assert.EndsWith("}", expanded.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_refuses_a_variant_rather_than_inventing_a_rule_for_it() {
        // `@apply hover:bg-accent` would have to emit a rule with a different selector from the
        // block it sits in, which is not what "apply this here" means.
        var expander = new ApplyExpander(ThemeTokens.Parse(UtilityFixture.Theme));
        expander.Expand(".card { @apply hover:bg-accent }");

        var diagnostic = Assert.Single(expander.Diagnostics);
        Assert.Contains("variant", diagnostic, StringComparison.Ordinal);
    }

    static string? Read(StyleEngine engine, ComputedStyle style, string property) {
        var id = engine.Properties.Lookup(property);
        return id != NameTable.None && style.TryGet(id, out var value) ? engine.Values.NameOf(value) : null;
    }
}
