// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That two windows of one document can answer <c>@media</c> differently.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of F11 that was owed after the query started matching at all.</b> A
///         document's surfaces share a rule set — that is what keeps one theme across a torn-off
///         window — and <c>@media</c> was decided at <i>load</i>, so the verdict lived in the rule
///         set and there could only be one of it. A torn-off inspector 400 px wide got the main
///         window's breakpoints, and a palette on a wide-gamut display got the main window's gamut,
///         for exactly the same reason.
///     </para>
///     <para>
///         ⚠ <b>Every test here asserts on a box or on a colour, never on a rule count.</b> That
///         matters more than it did: a block that does not apply now <i>is</i> in the rule set,
///         tagged with the group it came from, so anything that counted rules or looked for a
///         selector would see it and pass. The only question worth asking is whether the declaration
///         reached the element, in this window and not the other.
///     </para>
///     <para>
///         ⚠ <b>The sabotage they are written against is a query that stops matching.</b> That is
///         this subsystem's recurring defect — <c>MediaContext.Gamut</c> was once never constructed,
///         and every responsive variant was dead for a release because nothing built an
///         <c>Animator</c> — and it is silent by construction, because a dropped block is perfectly
///         good CSS. Point <c>StyleResolver</c> at the document's scope instead of the element's, or
///         have <c>UiDocument.CreateSurface</c> forget to allocate one, and the pairs below stop
///         disagreeing while the whole rest of the suite stays green.
///     </para>
/// </remarks>
public class PerSurfaceMediaTests {
    /// <summary>A box that is 10 px wide below 640 and 300 above it.</summary>
    const string Responsive = """
        root { flex-direction: column; }
        ui-surface { flex-direction: column; }
        .box { width: 10px; height: 20px; }
        @media (min-width: 640px) { .box { width: 300px; } }
        """;

    static UiDocument Document(float width = 1000f, string css = Responsive) {
        var document = new UiDocument(width, 600f);
        document.Load(css);

        return document;
    }

    /// <summary>
    ///     ⚠ <b>The headline: one document, two windows, one breakpoint, two answers.</b>
    /// </summary>
    /// <remarks>
    ///     A single assertion pair, and the pair is the test. Either box on its own is satisfied by
    ///     an engine that answers every surface the same way and happens to be right about that one.
    /// </remarks>
    [Fact]
    public void Two_surfaces_of_one_document_answer_a_breakpoint_differently() {
        using var document = Document();

        var wide = document.Root.Add("div", null, "box");
        var narrow = document.CreateSurface(400f, 300f).Root.Add("div", null, "box");

        document.Update();

        Assert.Equal(300f, wide.Width, 0.001f);
        Assert.Equal(10f, narrow.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>And resizing one window re-decides that window only.</b>
    /// </summary>
    /// <remarks>
    ///     The failure this is written against is the previous behaviour rather than a hypothetical
    ///     one: <c>UiDocument.Resize</c> re-asked the question for the primary surface and for no
    ///     other, because the answer was the rule set's. Under that code the second window here would
    ///     never change and the first would change when the second was dragged.
    /// </remarks>
    [Fact]
    public void Resizing_one_surface_leaves_the_other_alone() {
        using var document = Document();

        var wide = document.Root.Add("div", null, "box");
        var second = document.CreateSurface(400f, 300f);
        var narrow = second.Root.Add("div", null, "box");

        document.Update();
        Assert.Equal(300f, wide.Width, 0.001f);
        Assert.Equal(10f, narrow.Width, 0.001f);

        // The second window is dragged wider, across the breakpoint.
        document.Resize(second, 900f, 300f, 1f);
        document.Update();

        Assert.Equal(300f, narrow.Width, 0.001f);
        Assert.Equal(300f, wide.Width, 0.001f);

        // And the first is dragged narrower, across it the other way. The second must not follow.
        document.Resize(document.Primary, 500f, 600f, 1f);
        document.Update();

        Assert.Equal(10f, wide.Width, 0.001f);
        Assert.Equal(300f, narrow.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>A window on a wide-gamut display next to one on an sRGB display.</b>
    /// </summary>
    /// <remarks>
    ///     <c>EditorPane</c> published <c>ISwapChain.Gamut</c> from the main window's swapchain only,
    ///     and said so in its own remarks — not out of caution but because a document had one
    ///     cascade, and answering from whichever pane recreated its swapchain last would have
    ///     redecided the whole editor's palette every time a docked panel was dragged. It publishes
    ///     per pane now, which is only correct because of this.
    /// </remarks>
    [Fact]
    public void Two_surfaces_answer_the_colour_gamut_separately() {
        using var document = Document(
            css: """
                root { flex-direction: column; }
                ui-surface { flex-direction: column; }
                .box { width: 10px; height: 20px; }
                @media (color-gamut: p3) { .box { width: 300px; } }
                """
        );

        var srgb = document.Root.Add("div", null, "box");

        var second = document.CreateSurface(400f, 300f);
        var p3 = second.Root.Add("div", null, "box");

        second.Gamut = ColorGamut.DisplayP3;
        document.Update();

        Assert.Equal(10f, srgb.Width, 0.001f);
        Assert.Equal(300f, p3.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>A new window starts from the primary's colour scheme and its own sRGB gamut.</b>
    /// </summary>
    /// <remarks>
    ///     The two defaults differ because the two facts do, and the difference is worth pinning: an
    ///     appearance preference is a platform setting every window of an application shares, while a
    ///     gamut is negotiated per swapchain and a window that has not built one yet knows nothing.
    ///     Carrying the gamut over would make a torn-off panel claim a wide display it may not be on.
    /// </remarks>
    [Fact]
    public void A_new_surface_inherits_the_scheme_and_not_the_gamut() {
        using var document = Document();

        document.Gamut = ColorGamut.Rec2020;
        document.ColorScheme = ColorSchemePreference.Dark;

        var second = document.CreateSurface(400f, 300f);

        Assert.Equal(ColorSchemePreference.Dark, second.ColorScheme);
        Assert.Equal(ColorGamut.Srgb, second.Gamut);
    }

    /// <summary>
    ///     ⚠ <b>A panel dragged between windows picks up the window it lands in.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The case surfaces exist for, and the one that decides whether the scope needs
    ///         propagating at all. <c>UiDocument.Reparent</c> rebuilds a moved subtree's style slots
    ///         in pre-order under its new parent rather than moving them — because a slot's position
    ///         is read as depth order — so a scope inherited in <c>StyleTree.CreateElement</c> is
    ///         inherited by the whole subtree without anything walking it. This is the assertion that
    ///         says so.
    ///     </para>
    ///     <para>
    ///         Both directions, and a child rather than the element itself, because a propagation
    ///         that only reached the reparented element would pass the shallow half and leave every
    ///         panel's contents answering the window they came from.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_reparented_panel_answers_the_window_it_lands_in() {
        using var document = Document();

        // ⚠ `document.Root` itself, which this test used to route around. `CreateSurface` takes the
        // surface root out of the *layout* tree's child list and leaves it in the element tree, so a
        // parent that owns one has two different child counts — and `Reparent` used the element one
        // as a layout index, which meant that docking a panel back into the element that owns the
        // window threw. Fixed with `Move`, which had the same conversion; `SurfaceIndexTests` is
        // where that is argued, and this line is here so the workaround cannot quietly come back.
        var home = document.Root;

        var panel = home.Add("div");
        var inner = panel.Add("div", null, "box");

        var second = document.CreateSurface(400f, 300f);

        document.Update();
        Assert.Equal(300f, inner.Width, 0.001f);

        document.Reparent(panel, second.Root);
        document.Update();

        // `panel` and `inner` are the same instances; `inner` is a child, so this is the deep case.
        Assert.Equal(10f, inner.Width, 0.001f);

        document.Reparent(panel, home);
        document.Update();

        Assert.Equal(300f, inner.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>Two surface roots are the perfect sharing key, and must not share a style.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>StyleSharingKey</c> carries the element's parent, tag, id, classes, state and
    ///         inline block. Two elements with the same parent are in the same window by
    ///         construction, so for every ordinary element the scope adds nothing to the key —
    ///         except for surface roots, which <c>CreateSurface</c> hangs off one owner and which are
    ///         then the same tag with no id and no classes under the same parent. Two windows whose
    ///         only difference is which breakpoints they answer.
    ///     </para>
    ///     <para>
    ///         Written against removing <c>Scope</c> from the key. The rule below has to be one the
    ///         sharing cache is allowed to serve — nothing positional anywhere in the sheet, or
    ///         <c>SharingIsSound</c> turns the cache off and the test proves nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_surface_roots_do_not_share_one_computed_style() {
        using var document = Document(
            css: """
                root { flex-direction: column; }
                ui-surface { flex-direction: column; height: 20px; }
                @media (min-width: 640px) { ui-surface { height: 90px; } }
                """
        );

        var wide = document.CreateSurface(800f, 300f);
        var narrow = document.CreateSurface(400f, 300f);

        document.Update();

        Assert.Equal(90f, wide.Root.Height, 0.001f);
        Assert.Equal(20f, narrow.Root.Height, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>A nested group conjoins per surface, and the middle width is the whole test.</b>
    /// </summary>
    /// <remarks>
    ///     CSS Conditional 5 § 3 conjoins a conditional group rule with the one containing it, which
    ///     is what <c>sm:md:p-4</c> emits. 700 px satisfies the outer condition and not the inner, so
    ///     a scope that flattened the stack to its outermost — or to its innermost — gets two of these
    ///     three windows right and one wrong. Doc 43 § D3 recorded that Vixen's <c>@media</c> did not
    ///     nest and sized a cascade change against that belief; it always has, and moving the verdict
    ///     to the surface is the change that could plausibly have broken it.
    /// </remarks>
    [Theory]
    [InlineData(1000f, 300f)]
    [InlineData(700f, 10f)]
    [InlineData(400f, 10f)]
    public void A_nested_group_conjoins_in_each_window(float width, float expected) {
        using var document = Document(
            css: """
                root { flex-direction: column; }
                ui-surface { flex-direction: column; }
                .box { width: 10px; height: 20px; }
                @media (min-width: 640px) {
                    @media (min-width: 900px) { .box { width: 300px; } }
                }
                """
        );

        var box = document.CreateSurface(width, 300f).Root.Add("div", null, "box");
        document.Update();

        Assert.Equal(expected, box.Width, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>A condition nobody can read is refused once, at load, whatever size any window is.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The old loader dropped a block whose condition it could not evaluate, and dropped a
    ///         nested one <i>unread</i> when its enclosing condition was false — so a query with a
    ///         typo inside a breakpoint no window had reached was silent until somebody made a window
    ///         wide enough, and then arrived from a reload rather than from a load. Every group is
    ///         walked now, so there is no size at which a refusal first appears.
    ///     </para>
    ///     <para>
    ///         Asserted through the real <c>RingBufferSink</c> the editor's console reads, for the
    ///         reason <c>StyleDiagnosticDrainTests</c> gives: a refusal that reaches a list nothing
    ///         drains is the same silence in a different place.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_unreadable_condition_sealed_behind_a_false_one_still_reaches_the_log() {
        var sink = new RingBufferSink(64);
        using var document = new UiDocument(400f, 200f, logger: sink.CreateLogger("Vixen.Ui.Styling"));

        document.Load(
            """
            @media (min-width: 2000px) {
                @media (min-frobnicate: 3px) { .box { width: 300px; } }
            }
            """
        );

        var warning = Assert.Single(sink.Snapshot(), record => record.Level >= LogLevel.Warning);

        Assert.Contains("min-frobnicate", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A positional rule sealed behind a breakpoint no window is at must not cost sharing.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The regression this design could most easily have introduced, and the one no assertion
    ///         about styles would catch. A block that does not apply is now loaded, so its rules are
    ///         in the set — and <c>StyleRuleSet.SharingIsSound</c> is a property of the set. One
    ///         <c>:nth-child</c> inside a <c>@media</c> nobody has reached would have turned the
    ///         sharing cache off for the whole document, for ever, and every style would still have
    ///         been correct: the only symptom is a restyle pass doing several times the work it
    ///         needs to.
    ///     </para>
    ///     <para>
    ///         Both halves. Sound while the block is unreached, and unsound once a window is wide
    ///         enough to reach it — the second is what stops the fix from being "ignore conditional
    ///         rules", which would be unsound in the direction that produces wrong styles.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Sharing_stays_sound_until_a_positional_rule_is_actually_reachable() {
        using var document = Document(
            css: """
                .box { width: 10px; height: 20px; }
                @media (min-width: 640px) { .box:nth-child(2n) { width: 300px; } }
                """
        );

        var narrow = document.CreateSurface(400f, 300f);
        var wide = document.CreateSurface(900f, 300f);

        document.Update();

        var rules = document.Styles.Rules;
        var scopes = document.Styles.Scopes;

        Assert.True(rules.SharingIsSound(scopes.VerdictsOf(narrow.Scope)));
        Assert.False(rules.SharingIsSound(scopes.VerdictsOf(wide.Scope)));
    }
}
