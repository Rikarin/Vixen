// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1348">#1348</a>: the parity ledger scores a
///     slot of an assembled <c>transform</c>, <c>filter</c> or <c>backdrop-filter</c> by whether the
///     reader accepts it, not by whether the family emits it.
/// </summary>
/// <remarks>
///     Each assertion here is shown to be falsifiable on the tree as it is, not only under a sabotage:
///     an arbitrary value the reader refuses is reported, a spacing value it takes is not, and the
///     witness the probe leans on is shown to produce a transform on its own — a witness that did not
///     would make every class read as declined, which is the vacuous direction.
/// </remarks>
public class AssembledReaderProbeTests {
    static ThemeTokens Tokens => AssembledReaderProbe.Tokens;

    /// <summary>Both witnesses leave a transform alone, or "the list vanished" would prove nothing.</summary>
    [Fact]
    public void Each_witness_alone_leaves_a_transform() {
        Assert.NotNull(AssembledReaderProbe.TransformOf(Tokens, AssembledReaderProbe.Witness));
        Assert.NotNull(AssembledReaderProbe.TransformOf(Tokens, AssembledReaderProbe.OtherWitness));

        Assert.True(AssembledReaderProbe.FillsASlot(AssembledReaderProbe.Witness, Tokens, out _, out var slots));
        Assert.Contains(UtilityComposition.RotateZ, slots);
        Assert.True(AssembledReaderProbe.FillsASlot(AssembledReaderProbe.OtherWitness, Tokens, out _, out slots));
        Assert.DoesNotContain(UtilityComposition.RotateZ, slots);
    }

    /// <summary>
    ///     A value the reader refuses is reported, and it takes its neighbour down — which is the
    ///     defect's shape and the reason emission was never evidence.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>translateZ(50%)</c> has no box dimension to resolve against (Transforms 2 § 12), so the
    ///     reader declines it, and it declines the whole list with it. An author who writes the
    ///     arbitrary value has asked for exactly that, as a browser would give it; the point is that
    ///     the class <i>resolves</i> — the resolver's answer and the reader's disagree, and only the
    ///     reader's is what the engine does.
    /// </remarks>
    [Fact]
    public void A_slot_value_the_reader_refuses_is_declined_and_takes_the_witness_with_it() {
        const string refused = "translate-z-[50%]";

        Assert.True(AssembledReaderProbe.FillsASlot(refused, Tokens), $"{refused} should resolve into a transform slot");
        Assert.True(AssembledReaderProbe.Declines(refused));
        Assert.Null(AssembledReaderProbe.TransformOf(Tokens, refused, AssembledReaderProbe.Witness));
    }

    /// <summary>The other direction: a value the reader takes is not declined, or the probe refuses everything.</summary>
    [Theory]
    [InlineData("translate-z-4")]
    [InlineData("translate-z-px")]
    [InlineData("rotate-z-45")]
    [InlineData("-skew-y-6")]
    [InlineData("rotate-x-180")]
    public void A_slot_value_the_reader_takes_is_not_declined(string utility) {
        Assert.True(AssembledReaderProbe.FillsASlot(utility, Tokens), $"{utility} should resolve into a transform slot");
        Assert.False(AssembledReaderProbe.Declines(utility));
    }

    /// <summary><c>transform-none</c> and <c>filter-none</c> write the property and fill no slot.</summary>
    [Theory]
    [InlineData("transform-none")]
    [InlineData("filter-none")]
    public void A_keyword_that_writes_the_property_is_not_a_slot(string utility) {
        Assert.False(AssembledReaderProbe.FillsASlot(utility, Tokens));
    }

    /// <summary>
    ///     A filter slot value the executor cannot run is reported through the document's refusals,
    ///     which is the filter half's observation and needs no witness.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>blur(200ms)</c> is a time where a length belongs. Like the transform case it is an
    ///     arbitrary value and refused on purpose — what matters is that the class resolves and the
    ///     executor says no, so the probe has something to be false about on the tree as it is.
    /// </remarks>
    [Fact]
    public void A_filter_slot_value_the_executor_refuses_is_declined() {
        const string refused = "blur-[200ms]";

        Assert.True(AssembledReaderProbe.FillsASlot(refused, Tokens, out var property, out _), $"{refused} should resolve into a filter slot");
        Assert.Equal("filter", property);
        Assert.True(AssembledReaderProbe.Declines(refused));
        Assert.True(AssembledReaderProbe.Refuses(AssembledReaderProbe.RefusalsOf(Tokens, refused), "filter"));
        Assert.False(AssembledReaderProbe.Refuses(AssembledReaderProbe.RefusalsOf(Tokens, refused), "backdrop-filter"));
    }

    /// <summary>Named filter and backdrop values the executor runs are not declined.</summary>
    [Theory]
    [InlineData("blur-probe", "filter")]
    [InlineData("brightness-150", "filter")]
    [InlineData("hue-rotate-90", "filter")]
    [InlineData("backdrop-blur-probe", "backdrop-filter")]
    [InlineData("backdrop-opacity-50", "backdrop-filter")]
    public void A_filter_slot_value_the_executor_runs_is_not_declined(string utility, string expected) {
        Assert.True(AssembledReaderProbe.FillsASlot(utility, Tokens, out var property, out _), $"{utility} should resolve into a slot");
        Assert.Equal(expected, property);
        Assert.False(AssembledReaderProbe.Declines(utility));
    }

    /// <summary>
    ///     A negative filter proportion is no class, and the one filter that is an angle keeps its
    ///     negative — the defect the three-list probe found on its first run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every negative of fourteen filter and backdrop families resolved, and the executor
    ///     refused every one.</b> <c>TryNegate</c> flips any value that starts with a digit, and a
    ///     filter fragment is a bare number, so <c>-brightness-50</c> became
    ///     <c>brightness(-0.5)</c> — a function the executor cannot run, which drops the whole
    ///     <c>filter</c> and every slot beside it. The ledger read <c>works</c> for all seven rows.
    /// </remarks>
    /// <param name="utility">A negative spelling.</param>
    /// <param name="resolves">Whether it should resolve at all.</param>
    [Theory]
    [InlineData("-brightness-50", false)]
    [InlineData("-blur-2", false)]
    [InlineData("-blur-probe", false)]
    [InlineData("-sepia-100", false)]
    [InlineData("-backdrop-blur-2", false)]
    [InlineData("-backdrop-saturate-150", false)]
    [InlineData("-backdrop-opacity-50", false)]
    [InlineData("-hue-rotate-90", true)]
    [InlineData("-backdrop-hue-rotate-90", true)]
    public void A_negative_filter_proportion_is_no_class_and_a_negative_angle_still_is(string utility, bool resolves) {
        Assert.Equal(resolves, AssembledReaderProbe.FillsASlot(utility, Tokens));

        if (resolves) {
            Assert.False(AssembledReaderProbe.Declines(utility));
        }
    }

    /// <summary>
    ///     A valid filter beside a negative spelling now reaches the draw list — the other half of the
    ///     fix, which "the negative no longer resolves" does not say on its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The fix changes a picture, in the direction of the author's intent.</b> While
    ///     <c>-brightness-50</c> resolved, <c>-brightness-50 invert</c> assembled
    ///     <c>brightness(-0.5) invert(1)</c>, the executor refused the whole list, and the element drew
    ///     unfiltered. Now the negative is not a class, so the list is <c>invert(1)</c> and the
    ///     element is inverted. Asserted on the draw list's filtered group rather than on the
    ///     refusals, because an empty refusal list is also what a filter that never reached the
    ///     executor leaves; the group's matrix is exactly <see cref="UiColorMatrix.Invert" />, a
    ///     closed form with nothing to eyeball.
    /// </remarks>
    [Fact]
    public void A_valid_filter_beside_a_negative_spelling_reaches_the_draw_list() {
        var inverted = UiColorMatrix.Invert(1f);

        // The instrument first: the bare class opens a group with the inversion on it.
        Assert.Equal(inverted, AssembledReaderProbe.FilterOf(Tokens, "invert"));
        Assert.Null(AssembledReaderProbe.FilterOf(Tokens, "filter-none"));

        Assert.Equal(inverted, AssembledReaderProbe.FilterOf(Tokens, "-brightness-50", "invert"));
        Assert.Equal(inverted, AssembledReaderProbe.FilterOf(Tokens, "-sepia-100", "invert"));
    }

    /// <summary>
    ///     Every slot value on the surface is one the reader takes — the gate, and the reason the
    ///     ledger's <c>works</c> for the transform roots now means the engine does it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>With a floor, because a candidate list that offered no slot class would pass this
    ///         over an empty loop.</b> Every transform, filter and backdrop slot family, over the scale
    ///         vocabulary and both signs: 659 classes when this was written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its minute and a half is not its own.</b> Run alone it takes about that long, and
    ///         the 659 documents are not why: asking the reader about every candidate one document at a
    ///         time was measured at 1.5 s, and the same answers batched into a handful of documents at
    ///         0.3 s. The rest is <see cref="UtilityConsumptionProbe.Take" />, the consumption ledger
    ///         <see cref="ParityLedger.Measure" /> starts from, which is cached for the process and
    ///         costs <c>UtilityConsumptionGateTests</c> the same 80 s when that runs alone. In a
    ///         whole-assembly run whichever test asks first pays it once. So batching was measured and
    ///         not kept: it saved a second and added a bisection that would have needed a test of its
    ///         own.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_slot_value_on_the_surface_is_one_the_reader_declines() {
        var measured = ParityLedger.Measure([]);
        var probed = AssembledReaderProbe.Candidates(UtilityFamilies.Surface(Tokens)).Count;

        Assert.True(
            measured.Declined.Count == 0,
            "Classes the resolver answers and the reader declines — each one drops every other slot of its "
            + "transform or filter list on its element (#1348, #1328):\n  "
            + string.Join("\n  ", measured.Declined.Select(p => $"{p.Key}: {string.Join(' ', p.Value)}"))
        );

        Assert.True(probed >= 500, $"only {probed} class(es) fill a slot of an assembled list");
    }
}
