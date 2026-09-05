// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The one control in the tree whose <i>rules</i> differ by palette — doc 09 § Testing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The five <c>root.dark .tok-*</c> rules in <c>AdvancedTheme.vcss</c> are the only
///         per-control dark rules anywhere in the tree</b>, and nothing rendered them. Every other
///         control is dark by token substitution alone: <c>ControlTheme.vcss</c> has exactly one
///         <c>root.dark</c> block and it declares tokens and nothing else. That is the evidence
///         behind refusing #325's literal remainder — thirty-nine per-control dark baselines would be
///         thirty-nine pictures of one substitution, which <c>ControlThemeVisualTests</c> already
///         asserts reaches most of the frame — and it is also what makes <i>this</i> control the one
///         that genuinely needed the theme dimension.
///     </para>
///     <para>
///         ⚠ <b>A contrast oracle rather than a baseline, because the failure has a shape.</b> If a
///         <c>root.dark</c> rule stopped matching, the light colour would stay: <c>#8250df</c> on
///         <c>#1b1d21</c> reads 3.3:1, <c>#a3364b</c> reads 2.6:1 — unreadable code in a dark editor,
///         which is doc 43's "43 baselines stayed byte-identical" failure with a consequence attached.
///         So the assertion is the WCAG 4.5:1 that text owes its background, measured in both
///         palettes, and every one of the five light colours fails it on the dark ground. A committed
///         picture would fail too, and would say only "these bytes differ".
///     </para>
///     <para>
///         ⚠ <b>Both halves, because either alone is satisfiable by a broken theme.</b> "The two
///         palettes disagree" is met by a stylesheet that swapped one colour for another unreadable
///         one; "every colour is legible" is met by a document where the dark rules never applied and
///         the ground is white. Together they say the palette swapped and stayed readable.
///     </para>
/// </remarks>
public class SyntaxThemeTests {
    /// <summary>What WCAG asks of body text against its background.</summary>
    const double Required = 4.5d;

    const string Source = "#define X\nvar int a = 1 + \"two\"; // three\n";

    /// <summary>The token classes the theme gives a literal colour to, per palette.</summary>
    /// <remarks>
    ///     ⚠ <b><c>tok-comment</c> and <c>tok-operator</c> are deliberately absent.</b> Those two
    ///     resolve through <c>var(--text-muted)</c>, which the token block already swaps, so they are
    ///     covered by the substitution every other control is covered by and have no dark rule of
    ///     their own. Listing them here would be asserting the same fact a sixth and seventh time.
    /// </remarks>
    static readonly CodeTokenKind[] Literal = [
        CodeTokenKind.Keyword,
        CodeTokenKind.Type,
        CodeTokenKind.Number,
        CodeTokenKind.String,
        CodeTokenKind.Directive
    ];

    /// <summary>Relative luminance of a colour the cascade has already decoded from sRGB.</summary>
    static double Luminance(Color4 color) => (0.2126d * color.R) + (0.7152d * color.G) + (0.0722d * color.B);

    static double Contrast(Color4 left, Color4 right) {
        var a = Luminance(left);
        var b = Luminance(right);

        return (Math.Max(a, b) + 0.05d) / (Math.Min(a, b) + 0.05d);
    }

    /// <summary>Every literal token colour a rendered editor resolved, in one palette or the other.</summary>
    static Dictionary<CodeTokenKind, Color4> Colours(AdvancedFixture fixture, bool dark) {
        if (dark) {
            fixture.Document.Root.AddClass("dark");
        }

        var editor = fixture.Add<CodeEditor>();
        editor.Tokenizer = CStyleTokenizer.CSharp;
        editor.Source = Source;

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        var found = new Dictionary<CodeTokenKind, Color4>();

        foreach (var row in editor.Pool) {
            foreach (var span in row.Spans) {
                if (span.HasClass("parked") || !Literal.Contains(span.Kind) || found.ContainsKey(span.Kind)) {
                    continue;
                }

                found[span.Kind] = fixture.Document.ForegroundOf(span);
            }
        }

        // ⚠ Reported rather than skipped. A tokenizer that stopped producing one of these kinds would
        // otherwise silently reduce this test to whichever kinds survived, which is the state in
        // which every assertion below passes.
        Assert.Equal(Literal.Length, found.Count);

        return found;
    }

    /// <summary>The ground each palette's code is read against.</summary>
    static Color4 Surface(AdvancedFixture fixture) =>
        fixture.Document.ColorOf(fixture.Document.Root.Style, fixture.Document.PropertyId("--surface"))
        ?? throw new InvalidOperationException("the theme declares no --surface");

    [Fact]
    public void Every_literal_token_colour_is_legible_against_its_own_palette() {
        foreach (var dark in new[] { false, true }) {
            using var fixture = new AdvancedFixture();

            var colours = Colours(fixture, dark);
            var surface = Surface(fixture);

            foreach (var (kind, colour) in colours) {
                Assert.True(
                    Contrast(colour, surface) >= Required,
                    $"{(dark ? "dark" : "light")} {kind} is {Contrast(colour, surface):0.00}:1 against its surface"
                );
            }
        }
    }

    /// <summary>And the five rules that make that true in the dark actually ran.</summary>
    /// <remarks>
    ///     The test above would also pass on a document where the <c>dark</c> class reached nothing
    ///     at all — white ground, light token colours, every ratio fine. This is the half that says
    ///     the palette swapped.
    /// </remarks>
    [Fact]
    public void The_dark_palette_gives_every_literal_token_a_colour_of_its_own() {
        using var light = new AdvancedFixture();
        using var dark = new AdvancedFixture();

        var lit = Colours(light, dark: false);
        var dim = Colours(dark, dark: true);

        foreach (var kind in Literal) {
            Assert.NotEqual(lit[kind], dim[kind]);

            // ⚠ And it is lighter, not merely different: a dark editor's syntax colours are the light
            // ones brightened, and a rule that swapped one for another dark colour would satisfy
            // "not equal" while being exactly the defect.
            Assert.True(
                Luminance(dim[kind]) > Luminance(lit[kind]),
                $"the dark {kind} is {Luminance(dim[kind]):0.000} against the light one's {Luminance(lit[kind]):0.000}"
            );
        }
    }

    /// <summary>The instrument: the light colours are what fails in the dark, so the oracle can say no.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the first test cannot be shown to be falsifiable.</b> It asserts a
    ///     threshold, and a threshold every colour in the tree happens to clear is a threshold that
    ///     proves nothing. This measures the actual failure state — a <c>root.dark</c> rule that
    ///     stopped matching, so the light colour is read on the dark ground — and requires it to be
    ///     under the bar for all five.
    /// </remarks>
    [Fact]
    public void A_light_token_colour_on_the_dark_surface_is_what_the_threshold_rejects() {
        using var light = new AdvancedFixture();
        using var dark = new AdvancedFixture();

        var lit = Colours(light, dark: false);
        _ = Colours(dark, dark: true);

        var ground = Surface(dark);

        foreach (var kind in Literal) {
            Assert.True(
                Contrast(lit[kind], ground) < Required,
                $"the light {kind} reads {Contrast(lit[kind], ground):0.00}:1 on the dark surface, so this "
                + "test would not notice its dark rule going missing"
            );
        }
    }
}
