// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1348">#1348</a>: the parity ledger scores a
///     slot of the assembled <c>transform</c> by whether the reader accepts it, not by whether the
///     family emits it.
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

        Assert.True(AssembledReaderProbe.FillsASlot(AssembledReaderProbe.Witness, Tokens, out var slots));
        Assert.Contains(UtilityComposition.RotateZ, slots);
        Assert.True(AssembledReaderProbe.FillsASlot(AssembledReaderProbe.OtherWitness, Tokens, out slots));
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

        Assert.True(AssembledReaderProbe.FillsASlot(refused, Tokens, out _), $"{refused} should resolve into a transform slot");
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
        Assert.True(AssembledReaderProbe.FillsASlot(utility, Tokens, out _), $"{utility} should resolve into a transform slot");
        Assert.False(AssembledReaderProbe.Declines(utility));
    }

    /// <summary><c>transform-none</c> writes the property and fills no slot, and a witness beside it is meant to vanish.</summary>
    [Fact]
    public void A_keyword_that_writes_the_property_is_not_a_slot() {
        Assert.False(AssembledReaderProbe.FillsASlot("transform-none", Tokens, out _));
    }

    /// <summary>
    ///     Every slot value on the surface is one the reader takes — the gate, and the reason the
    ///     ledger's <c>works</c> for the transform roots now means the engine does it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>With a floor, because a candidate list that offered no slot class would pass this over
    ///     an empty loop.</b> Seven slot families, each over the scale vocabulary, both signs: 242 classes
    ///     when this was written.
    /// </remarks>
    [Fact]
    public void No_slot_value_on_the_surface_is_one_the_reader_declines() {
        var measured = ParityLedger.Measure([]);
        var probed = AssembledReaderProbe.Candidates(UtilityFamilies.Surface(Tokens)).Count;

        Assert.True(
            measured.Declined.Count == 0,
            "Classes the resolver answers and TransformReader declines — each one drops every other transform "
            + "slot on its element (#1348, #1328):\n  "
            + string.Join("\n  ", measured.Declined.Select(p => $"{p.Key}: {string.Join(' ', p.Value)}"))
        );

        Assert.True(probed >= 200, $"only {probed} class(es) fill a transform slot");
    }
}
