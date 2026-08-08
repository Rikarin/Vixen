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
///         conflating — a dropped <c>@media</c> block is syntactically perfect CSS that simply never
///         reaches an element — so the only assertion worth making is one an element can fail.
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
    ///     <c>@media</c> is evaluated at load rather than at match — <c>StyleSheetLoader</c> says so
    ///     and gives the reason — which makes re-asking the question somebody's job. It was nobody's.
    ///     Growing and shrinking are asserted separately because a loader that recorded only the
    ///     conditions that <i>matched</i> would get the first right and the second wrong: there would
    ///     be nothing left to replay once the block had been dropped.
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
    ///     ⚠ <b>A resize that could not have changed an answer must not reload the stylesheets.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The naive fix is to reload on every resize, and it is not affordable: a reload is a full
    ///         ExCSS parse of every sheet, a window drag is sixty of them a second, and a generated
    ///         utility sheet is not small. So <c>StyleEngine.SetMedia</c> replays the conditions the
    ///         loader actually saw and reloads only where one of them changed its mind. Every
    ///         stylesheet this repository ships contains no <c>@media</c> at all, so for all of them
    ///         the answer is never.
    ///     </para>
    ///     <para>
    ///         Asserted through <c>Styles.Animations</c>, which is the cheapest thing a reload
    ///         destroys and the one whose destruction is visible: everything derived from the rules is
    ///         rebuilt, so a resize that reloaded would restart every fade in the window. That is the
    ///         user-facing consequence of getting this wrong, and it is what the identity check is
    ///         standing in for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_resize_with_nothing_to_re_decide_keeps_the_rules_it_had() {
        using var document = new UiDocument(400f, 200f);

        Box(
            document,
            """
            root { width: 4000px; height: 200px; }
            #box { width: 10px; height: 20px; }
            """
        );

        var animations = document.Styles.Animations;

        document.Resize(900f, 200f);
        document.Update();

        Assert.Same(animations, document.Styles.Animations);
    }

    /// <summary>
    ///     ⚠ <b>The counterpart, so the test above is not passing because nothing ever reloads.</b>
    /// </summary>
    [Fact]
    public void A_resize_that_crosses_a_breakpoint_does_reload() {
        using var document = new UiDocument(400f, 200f);

        Box(document, Responsive);

        var animations = document.Styles.Animations;

        document.Resize(900f, 200f);
        document.Update();

        Assert.NotSame(animations, document.Styles.Animations);
    }
}
