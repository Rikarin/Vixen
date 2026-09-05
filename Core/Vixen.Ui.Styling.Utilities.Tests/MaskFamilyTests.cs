// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>What each mask family's class actually masks, read off the assembled <c>mask-image</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Eighteen of the twenty-five mask roots were named by no test in the tree, and the
///         reason given for not needing one did not hold.</b> #629 declined the cluster rows on the
///         grounds that <c>MaskGradientTests</c> and <c>GradientPaintTests</c> pin it against
///         pixels. Both do read pixels and both write hand-authored declarations —
///         <c>mask-image: linear-gradient(to right, …)</c>, <c>mask-mode</c>, <c>mask-size</c> — and
///         not one utility class appears in either file. They prove the renderer honours
///         <c>mask-image</c>. Nothing proved that a <c>mask-b-from-*</c> reaches it, or reaches it on
///         the bottom edge.
///     </para>
///     <para>
///         ⚠ <b>And <c>UtilityConsumptionGateTests</c> is green for every one of them, which is the
///         whole point of writing these.</b> That gate's verdict is per <i>property</i> and unions
///         over values: <c>mask-image</c> is read, so a family that put its ramp on the wrong edge —
///         or on no edge at all — passes there with nothing to say. The same distinction one level
///         down from the one #629 exists to draw.
///     </para>
///     <para>
///         ⚠ <b>The assertion is the <i>computed</i> value and not the emitted text, and that is
///         what makes it about the answer rather than the mechanism.</b> Every one of these classes
///         emits a <c>--tw-*</c> fragment plus a <c>mask-image</c> made of <c>var()</c>s, so the
///         string a family emits says almost nothing on its own — the layer list only becomes a
///         gradient once the cascade substitutes. <c>UtilityFamilyTests</c> holds the emitted form
///         for four of these roots; this file holds what comes out the other end, which is where a
///         fragment named <c>--tw-mask-bottom</c> and written into the <c>to top</c> slot stops being
///         invisible.
///     </para>
///     <para>
///         ⚠ <b>Full equality, never a <c>Contains</c>.</b> An edge class writes all four ramps and
///         drives one of them, so an assertion that only looked for its own edge would pass against
///         a family that also stamped its position onto the other three — which is the shape
///         <c>mask-x-*</c> and <c>mask-y-*</c> genuinely have (two edges each) and every other edge
///         root genuinely does not.
///     </para>
///     <para>
///         ⚠ <b>The nineteenth root the issue named, <c>bg-radial</c>, was not uncovered:</b>
///         <c>CompositionTests.A_gradient_is_three_fragments_and_an_assembler</c> already resolves it
///         end to end. Its search looked for the root spelled as a class prefix, and <c>bg-radial</c>
///         is a static utility that never takes one.
///     </para>
/// </remarks>
public class MaskFamilyTests {
    /// <summary>An untouched layer: opaque, so the <c>intersect</c> leaves the others alone.</summary>
    const string Opaque = "linear-gradient(#fff, #fff)";

    /// <summary>An edge ramp nobody drove — solid at the edge, gone by the far side.</summary>
    const string Untouched = "black 0%, transparent 100%";

    /// <summary>
    ///     The twelve edge ramps put their stop on the edge they name and nowhere else.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>mask-t-*</c> is <c>to top</c> and not <c>to bottom</c>: it fades the element out
    ///     <i>at the top</i>.</b> The four physical edges are the rows that would catch a family
    ///     wired to the opposite ramp, and swapping any pair of them is a defect no gate, no
    ///     emission test and no computed-value null check can see.
    ///     <para>
    ///         ⚠ <b><c>mask-x-*</c> and <c>mask-y-*</c> drive <i>two</i> ramps rather than widening
    ///         one</b> — that is why <c>Family.Positions</c> is several. A shorthand that set a single
    ///         fragment would fade one side and leave the other solid, and the two-edge rows below
    ///         are the only place that distinction is stated.
    ///     </para>
    /// </remarks>
    [Theory]
    // The near stop, per edge. `from` moves where the mask is still fully opaque.
    [InlineData("mask-t-from-50%", "black 50%, transparent 100%", Untouched, Untouched, Untouched)]
    [InlineData("mask-r-from-50%", Untouched, "black 50%, transparent 100%", Untouched, Untouched)]
    [InlineData("mask-b-from-50%", Untouched, Untouched, "black 50%, transparent 100%", Untouched)]
    [InlineData("mask-l-from-50%", Untouched, Untouched, Untouched, "black 50%, transparent 100%")]
    // The far stop. `to` moves where it has finished fading, and leaves the near stop alone.
    [InlineData("mask-t-to-25%", "black 0%, transparent 25%", Untouched, Untouched, Untouched)]
    [InlineData("mask-r-to-25%", Untouched, "black 0%, transparent 25%", Untouched, Untouched)]
    [InlineData("mask-b-to-25%", Untouched, Untouched, "black 0%, transparent 25%", Untouched)]
    [InlineData("mask-l-to-25%", Untouched, Untouched, Untouched, "black 0%, transparent 25%")]
    // And the two axis pairs, each of which is two ramps and not a wider one.
    [InlineData("mask-x-from-10%", Untouched, "black 10%, transparent 100%", Untouched, "black 10%, transparent 100%")]
    [InlineData("mask-x-to-80%", Untouched, "black 0%, transparent 80%", Untouched, "black 0%, transparent 80%")]
    [InlineData("mask-y-from-10%", "black 10%, transparent 100%", Untouched, "black 10%, transparent 100%", Untouched)]
    [InlineData("mask-y-to-80%", "black 0%, transparent 80%", Untouched, "black 0%, transparent 80%", Untouched)]
    public void An_edge_ramp_lands_on_the_edge_it_names(
        string utility,
        string top,
        string right,
        string bottom,
        string left
    ) {
        var fixture = new UtilityFixture();

        Assert.Equal(Edges(top, right, bottom, left), fixture.Computed([utility], "mask-image"));
    }

    /// <summary>
    ///     The linear, radial and conic families each fill their own layer and leave the other two
    ///     opaque.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Which of the three layers a class fills is the claim, and it is the one that decides
    ///     whether two mask classes compose or one of them silently wins.</b> The list is always
    ///     three long and always in the same order, so a radial family that wrote into the linear
    ///     slot would still produce a valid <c>mask-image</c>, still mask, and still be green
    ///     everywhere else — until an author wrote a linear class beside it.
    /// </remarks>
    [Theory]
    // The plain linear ramp, whose angle defaults to CSS's own 180deg.
    [InlineData("mask-linear-from-50%", "linear-gradient(180deg, black 50%, transparent 100%)", Opaque, Opaque)]
    [InlineData("mask-linear-to-90%", "linear-gradient(180deg, black 0%, transparent 90%)", Opaque, Opaque)]
    // ⚠ The angle root is a *fragment* family: it writes the angle and assembles the layer, so a
    // bare `mask-linear-45` masks on its own rather than waiting for a stop class.
    [InlineData("mask-linear-45", "linear-gradient(45deg, black 0%, transparent 100%)", Opaque, Opaque)]
    // The radial layer, with CSS's own ending and position under it.
    [InlineData(
        "mask-radial-from-40%",
        Opaque,
        "radial-gradient(ellipse farthest-corner at center, black 40%, transparent 100%)",
        Opaque
    )]
    [InlineData(
        "mask-radial-to-60%",
        Opaque,
        "radial-gradient(ellipse farthest-corner at center, black 0%, transparent 60%)",
        Opaque
    )]
    // The bare `mask-radial-*` root is the ending size, which is a different word in the same slot.
    [InlineData(
        "mask-radial-closest-side",
        Opaque,
        "radial-gradient(ellipse closest-side at center, black 0%, transparent 100%)",
        Opaque
    )]
    [InlineData(
        "mask-radial-farthest-side",
        Opaque,
        "radial-gradient(ellipse farthest-side at center, black 0%, transparent 100%)",
        Opaque
    )]
    // ⚠ `mask-radial-at-*` is a root of its own and not a value of the one above — `at` is part of
    // the class name in v4, and the two have to survive the longest-prefix split independently.
    [InlineData(
        "mask-radial-at-top-left",
        Opaque,
        "radial-gradient(ellipse farthest-corner at top left, black 0%, transparent 100%)",
        Opaque
    )]
    [InlineData(
        "mask-radial-at-bottom",
        Opaque,
        "radial-gradient(ellipse farthest-corner at bottom, black 0%, transparent 100%)",
        Opaque
    )]
    // And the conic layer, whose sweep starts at twelve o'clock unless a class says otherwise.
    [InlineData("mask-conic-from-20%", Opaque, Opaque, "conic-gradient(from 0deg, black 20%, transparent 100%)")]
    [InlineData("mask-conic-to-90%", Opaque, Opaque, "conic-gradient(from 0deg, black 0%, transparent 90%)")]
    [InlineData("mask-conic-45", Opaque, Opaque, "conic-gradient(from 45deg, black 0%, transparent 100%)")]
    public void A_gradient_mask_fills_its_own_layer_and_no_other(
        string utility,
        string linear,
        string radial,
        string conic
    ) {
        var fixture = new UtilityFixture();

        Assert.Equal($"{linear}, {radial}, {conic}", fixture.Computed([utility], "mask-image"));
    }

    /// <summary>Two classes that name different edges of the same box compose.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the property the whole per-edge arrangement exists for, and the one an
    ///     implementation that wrote a whole <c>mask-image</c> per class could not have.</b> Each edge
    ///     class writes all four ramps and drives one; the two rules therefore write the identical
    ///     <c>--tw-mask-linear</c> and differ only in the fragment underneath it, so the cascade
    ///     picking either rule gives the same answer. A family that assembled its layer from its own
    ///     edge alone would blank whichever edge lost.
    /// </remarks>
    [Fact]
    public void Two_edge_classes_write_one_mask_between_them() {
        var fixture = new UtilityFixture();

        Assert.Equal(
            Edges("black 40%, transparent 100%", Untouched, "black 0%, transparent 60%", Untouched),
            fixture.Computed(["mask-t-from-40%", "mask-b-to-60%"], "mask-image")
        );
    }

    /// <summary>And so do two classes in different layers — sharing their stop positions.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Across layers the mechanism is the opposite one and the observable is the
    ///         same</b>: the layer a class does not fill resolves through its <c>var()</c> fallback to
    ///         an opaque gradient, which is the identity under <c>mask-composite: intersect</c>. That
    ///         fallback is what lets a single class mask at all, and it is also what stops a second
    ///         class in another layer from being erased by the first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But the stops are shared and the shapes are not, which is surprising enough to be
    ///         the reason this assertion is written out in full.</b> <c>mask-linear-from-*</c>,
    ///         <c>mask-radial-from-*</c> and <c>mask-conic-from-*</c> all write the one
    ///         <c>--tw-mask-from-position</c> — Tailwind's arrangement, faithfully — so the
    ///         <c>20%</c> asked for on the conic layer below appears on the radial layer too. Only
    ///         the twelve edge ramps carry a fragment per edge. Anyone reading
    ///         <c>mask-radial-to-80% mask-conic-from-20%</c> as two independent gradients is wrong
    ///         about it, and the string here is what says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radial_and_a_conic_class_survive_each_other() {
        var fixture = new UtilityFixture();

        Assert.Equal(
            Opaque
            + ", radial-gradient(ellipse farthest-corner at center, black 20%, transparent 80%)"
            + ", conic-gradient(from 0deg, black 20%, transparent 80%)",
            fixture.Computed(["mask-radial-to-80%", "mask-conic-from-20%"], "mask-image")
        );

        // And every one of them writes the operator that makes the arrangement work. A mask list
        // composited any other way would let the untouched layers cancel the one that was filled.
        Assert.Equal("intersect", fixture.Computed(["mask-radial-to-80%"], "mask-composite"));
    }

    /// <summary>The four edge ramps and the two untouched layers, in the order the family writes them.</summary>
    static string Edges(string top, string right, string bottom, string left) =>
        $"linear-gradient(to top, {top}), linear-gradient(to right, {right}), "
        + $"linear-gradient(to bottom, {bottom}), linear-gradient(to left, {left}), {Opaque}, {Opaque}";
}
