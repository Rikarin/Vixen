// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a document tells the cascade which surface it is on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>StyleEngine.Load</c> has taken a <see cref="MediaContext" /> for as long as there
///         has been a cascade, and <c>UiDocument</c> passed nothing.</b> So every sheet in every real
///         document was evaluated against <c>default</c> — a surface nought pixels wide — and every
///         <c>@media (min-width: …)</c> block in the engine was dropped at load, in every window, at
///         every size. That took the whole responsive-variant system with it: <c>md:p-4</c> compiles
///         to <c>@media (min-width: 768px)</c> and had never once matched.
///     </para>
///     <para>
///         ⚠ <b>Every test here asserts on a <i>box</i>, not on the presence of a rule.</b> "The
///         stylesheet parsed" and "the block applies" are the two questions this whole area keeps
///         conflating — a <c>@media</c> block that never matches is syntactically perfect CSS that
///         simply never reaches an element — so the only assertion worth making is one an element can
///         fail. That is more load-bearing now than it was: the rules of a block that does not apply
///         are in the rule set, so anything counting rules would see them and pass.
///     </para>
///     <para>
///         <see cref="PerSurfaceMediaTests" /> is the other half — the same questions asked of a
///         document showing itself in two windows at once.
///     </para>
/// </remarks>
public class MediaContextTests {
    /// <summary>A rule that only exists above a breakpoint, over a box that is otherwise narrow.</summary>
    const string Responsive = """
        root { width: 4000px; height: 200px; }
        #box { width: 10px; height: 20px; }
        @media (min-width: 640px) { #box { width: 300px; } }
        """;

    static UiElement Box(UiDocument document, string css) {
        document.Load(css);

        var box = document.Create("div", document.Root, "box");
        document.Update();

        return box;
    }

    /// <summary>
    ///     ⚠ <b>The headline: a breakpoint matches on a wide surface and not on a narrow one.</b>
    /// </summary>
    [Theory]
    [InlineData(800f, 300f)]
    [InlineData(400f, 10f)]
    public void A_breakpoint_is_decided_by_the_surface_the_document_is_on(float surface, float expected) {
        using var document = new UiDocument(surface, 200f);
        Assert.Equal(expected, Box(document, Responsive).Width);
    }

    /// <summary>
    ///     ⚠ <b>And it is re-decided when the window changes size, in both directions.</b>
    /// </summary>
    /// <remarks>
    ///     Re-asking the question on a resize was nobody's job for two phases. Growing and shrinking
    ///     are asserted separately because the first fix — replaying the conditions the loader had
    ///     recorded — got the first right and the second wrong if it recorded only the ones that
    ///     <i>matched</i>: there was nothing left to replay once a block had been dropped. Nothing is
    ///     dropped now, so both directions are the same code, and both are still worth asserting.
    /// </remarks>
    [Fact]
    public void A_resize_re_decides_every_breakpoint() {
        using var document = new UiDocument(400f, 200f);
        var box = Box(document, Responsive);

        Assert.Equal(10f, box.Width);

        document.Resize(900f, 200f);
        document.Update();
        Assert.Equal(300f, box.Width);

        document.Resize(500f, 200f);
        document.Update();
        Assert.Equal(10f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b><c>@media (color-gamut: p3)</c> matches on a wide surface and not on an sRGB one.</b>
    /// </summary>
    /// <remarks>
    ///     The swapchain has always known what it was granted, and <c>UiGeometryBuilder</c> has always
    ///     been told — it maps every colour it emits against exactly this value. The same fact simply
    ///     never reached the cascade, so the query could not match on any hardware.
    /// </remarks>
    [Theory]
    [InlineData(ColorGamut.Srgb, 10f)]
    [InlineData(ColorGamut.DisplayP3, 300f)]
    [InlineData(ColorGamut.Rec2020, 300f)]
    public void A_colour_gamut_query_is_decided_by_the_surface_that_was_granted(ColorGamut granted, float expected) {
        using var document = new UiDocument(800f, 200f) { Gamut = granted };

        var box = Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            @media (color-gamut: p3) { #box { width: 300px; } }
            """
        );

        Assert.Equal(expected, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>A window dragged onto a wide display picks the query up with the surface.</b>
    /// </summary>
    /// <remarks>
    ///     Which is not hypothetical bookkeeping: <c>EditorPane</c> re-reads <c>ISwapChain.Gamut</c> on
    ///     every recreate for precisely this reason, because a resize renegotiates the surface format
    ///     and the granted gamut can move with it.
    /// </remarks>
    [Fact]
    public void Changing_the_granted_gamut_re_decides_the_query() {
        using var document = new UiDocument(800f, 200f);

        var box = Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            @media (color-gamut: p3) { #box { width: 300px; } }
            """
        );

        Assert.Equal(10f, box.Width);

        document.Gamut = ColorGamut.DisplayP3;
        document.Update();

        Assert.Equal(300f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b><c>prefers-color-scheme</c> was dead in the same way, and <c>dark:</c> rides on it.</b>
    /// </summary>
    /// <remarks>
    ///     Under a theme whose <c>--dark-mode</c> is <c>media</c> — the default — the <c>dark:</c>
    ///     variant compiles to <c>@media (prefers-color-scheme: dark)</c> and so had never matched
    ///     either. The editor uses the <c>class</c> strategy, which compiles to a <c>.dark</c> ancestor
    ///     and was unaffected, which is the whole reason nobody noticed.
    /// </remarks>
    [Theory]
    [InlineData(ColorSchemePreference.NoPreference, 10f)]
    [InlineData(ColorSchemePreference.Light, 10f)]
    [InlineData(ColorSchemePreference.Dark, 300f)]
    public void A_colour_scheme_preference_is_asked_of_the_host(ColorSchemePreference preference, float expected) {
        using var document = new UiDocument(800f, 200f) { ColorScheme = preference };

        var box = Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            @media (prefers-color-scheme: dark) { #box { width: 300px; } }
            """
        );

        Assert.Equal(expected, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>A resize that could not have changed an answer must not disturb anything.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The naive fix is to forget every computed style on every resize, and it is not
    ///         affordable: a window drag is sixty of them a second over a whole document's worth of
    ///         elements. So <c>StyleEngine.SetMedia</c> evaluates the groups the loader registered and
    ///         says whether one of them changed its mind, and only then does the document forget.
    ///         Every stylesheet this repository ships contains no <c>@media</c> at all, so for all of
    ///         them the answer is never.
    ///     </para>
    ///     <para>
    ///         Asserted on the engine's own answer rather than on a proxy for it, because the proxy
    ///         this used to have — <c>Styles.Animations</c> being replaced by the reload — is a thing
    ///         a resize no longer does at all, and an assertion about a mechanism that has been
    ///         removed passes for ever whatever the code does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_resize_with_nothing_to_re_decide_changes_no_verdict() {
        using var document = new UiDocument(400f, 200f);

        Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            """
        );

        document.Resize(900f, 200f);

        Assert.False(document.Styles.SetMedia(document.Media));
    }

    /// <summary>
    ///     ⚠ <b>Crossing a breakpoint no longer re-parses the stylesheets, and a fade survives it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This asserted the opposite until <c>@media</c> stopped being decided at load. It had
    ///         to: the verdict lived in the rule set, so the only way to re-ask the question was
    ///         <c>StyleEngine.Reload</c> — a full ExCSS parse of every sheet, measured at 42 ms for
    ///         the editor's twelve, on a frame of a window drag — and everything derived from the
    ///         rules went with it, <c>Animations</c> included. Dragging a window across 640 px
    ///         restarted every transition in it.
    ///     </para>
    ///     <para>
    ///         Both halves, and the second is what stops the first being vacuous. The animator being
    ///         the same object would also be true of an engine that had simply stopped answering
    ///         <c>@media</c> at all, which is this subsystem's recurring failure — so the width is
    ///         asserted in the same test, and it is the width that says the query still matches.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_resize_that_crosses_a_breakpoint_keeps_what_was_in_flight() {
        using var document = new UiDocument(400f, 200f);
        var box = Box(document, Responsive);

        var animations = document.Styles.Animations;
        var rules = document.Styles.Rules;

        document.Resize(900f, 200f);
        document.Update();

        Assert.Equal(300f, box.Width);
        Assert.Same(animations, document.Styles.Animations);
        Assert.Same(rules, document.Styles.Rules);
    }

    /// <summary>A conditional group rule inside another one, which is what <c>sm:md:</c> emits.</summary>
    const string Nested = """
        root { width: 4000px; height: 200px; }
        #box { width: 10px; height: 20px; }
        @media (min-width: 640px) {
            @media (min-width: 900px) { #box { width: 300px; } }
        }
        """;

    /// <summary>
    ///     ⚠ <b>A nested conditional group applies only where every condition in the stack holds.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         CSS Conditional 5 § 3 lets a conditional group rule contain another, and the two
    ///         conditions conjoin. <c>StyleSheetLoader.LoadMedia</c> has always recursed into the rule
    ///         it just matched, so this has always worked — which is worth an assertion precisely
    ///         because doc 43 § D3 recorded the opposite ("Vixen's <c>@media</c> does not nest") and
    ///         sized a whole cascade change against that belief. The generator was the one that could
    ///         not nest, and it was dropping <c>sm:md:p-4</c> on the strength of this file's silence.
    ///     </para>
    ///     <para>
    ///         The three widths are the three cases, and the middle one is the whole test: 700 px
    ///         satisfies the outer condition and not the inner, so a loader that flattened the stack to
    ///         its outermost condition — or to its innermost — passes two of these rows and fails one.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1000f, 300f)]
    [InlineData(700f, 10f)]
    [InlineData(400f, 10f)]
    public void A_nested_conditional_group_needs_every_condition_in_its_stack(float surface, float expected) {
        using var document = new UiDocument(surface, 200f);
        Assert.Equal(expected, Box(document, Nested).Width);
    }

    /// <summary>
    ///     ⚠ <b>The inner condition of a nested group is re-decided too.</b>
    /// </summary>
    /// <remarks>
    ///     The guard in <c>StyleEngine.SetMedia</c> replays the conditions the loader recorded, and
    ///     nesting changes what "a condition" is. Recording only the outer one would leave a window
    ///     dragged from 700 px to 1000 px showing the narrow box for ever: the outer condition holds at
    ///     both widths, so nothing would look changed and no reload would happen.
    /// </remarks>
    [Fact]
    public void A_resize_that_crosses_only_the_inner_condition_re_decides() {
        using var document = new UiDocument(700f, 200f);
        var box = Box(document, Nested);

        Assert.Equal(10f, box.Width);

        document.Resize(1000f, 200f);
        document.Update();
        Assert.Equal(300f, box.Width);

        document.Resize(700f, 200f);
        document.Update();
        Assert.Equal(10f, box.Width);
    }

    /// <summary>
    ///     ⚠ <b>And the guard stays tight: a condition sealed behind a false outer one costs nothing.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The inner condition here is <c>(min-width: 640px)</c> and the drag crosses it — but the
    ///         outer condition is false at both ends, so the block could not have applied either way
    ///         and there is nothing to re-decide. The loader never records a condition it never reached,
    ///         which is what makes this free rather than merely correct.
    ///     </para>
    ///     <para>
    ///         Worth its own test because the safe-looking fix for the one above — record every
    ///         condition in the text, reached or not — would pass that test and fail this one, and the
    ///         cost it would add is a full ExCSS re-parse on a frame of a window drag.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_resize_inside_a_dropped_outer_condition_reloads_nothing() {
        using var document = new UiDocument(400f, 200f);

        Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            @media (min-width: 2000px) {
                @media (min-width: 640px) { #box { width: 300px; } }
            }
            """
        );

        var animations = document.Styles.Animations;

        document.Resize(900f, 200f);
        document.Update();

        Assert.Same(animations, document.Styles.Animations);
        Assert.Equal(10f, document.Root.Children[0].Width);
    }
}
