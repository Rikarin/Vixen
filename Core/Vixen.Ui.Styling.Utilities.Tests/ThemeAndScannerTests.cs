// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
        Assert.Equal(["Assets/**/*.vxml", "Assets/**/*.cs"], tokens.Content);

        Assert.Equal("#17171d", tokens.Colors["surface-2"]);
        Assert.Equal("#8a8a99", tokens.Colors["muted"]);
        Assert.Equal(8f, tokens.Radius["lg"]);
        Assert.Equal(600f, tokens.FontWeight["semibold"]);
        Assert.Equal(768f, tokens.Screens["md"]);
        Assert.Equal(new FontSizeToken(17f, 24f), tokens.FontSize["lg"]);
    }

    [Fact]
    public void A_colour_family_can_be_a_value_or_a_set_of_shades() {
        // `muted: "#8a8a99"` and `accent: { DEFAULT: …, hover: … }` are both legal and mean
        // different things, which is why the config is read from the YAML DOM rather than
        // deserialised into a record: a typed schema would have to pick one shape.
        var tokens = ThemeTokens.Parse(UtilityFixture.Theme);

        Assert.Equal("#4f7cff", tokens.Colors["accent"]);
        Assert.Equal("#6a91ff", tokens.Colors["accent-hover"]);
        Assert.False(tokens.Colors.ContainsKey("accent-DEFAULT"));
    }

    [Fact]
    public void A_font_size_may_omit_its_line_height() {
        var tokens = ThemeTokens.Parse("theme:\n  fontSize: { base: 14 }\n");

        Assert.Empty(tokens.Diagnostics);
        Assert.Equal(14f, tokens.FontSize["base"].Size);
        Assert.Equal(20f, tokens.FontSize["base"].LineHeight);
    }

    [Fact]
    public void A_token_that_is_not_what_it_should_be_is_reported_rather_than_guessed_at() {
        var tokens = ThemeTokens.Parse("theme:\n  radius: { sm: notanumber }\n  colors: { bad: [1, 2] }\n");

        Assert.Equal(2, tokens.Diagnostics.Count);
        Assert.Empty(tokens.Radius);
    }

    /// <summary>
    ///     And a file that is not YAML at all goes down the same channel, rather than out of the
    ///     reader and into whoever was building a stylesheet. A theme file is hand-written, so this is
    ///     the likeliest of the failures here — and the malformed tag is the shape the <c>meta</c>
    ///     fuzz found reaching a library constructor before <c>YamlReader</c> refused it.
    /// </summary>
    [Theory]
    [InlineData("theme: !!Te]V\n")]
    [InlineData("theme:\n  colors: { bad: [1, 2\n")]
    [InlineData(":")]
    public void A_theme_file_that_is_not_YAML_is_reported_rather_than_thrown(string yaml) {
        var tokens = ThemeTokens.Parse(yaml);

        Assert.Contains("not YAML", Assert.Single(tokens.Diagnostics), StringComparison.Ordinal);
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
