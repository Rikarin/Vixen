// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     What an inline leaf pays to find its neighbours: linear in the leaves of a block container, not
///     quadratic (#1403).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Counted as comparisons, not timed.</b> <c>UiElement.Before</c> and <c>After</c> once
///         found a leaf's own position with <c>List.IndexOf</c> at every level they climbed, and
///         <c>IndexOf</c> costs one <c>Equals</c> per sibling ahead of the leaf. The leaves here are a
///         subclass that counts its own <c>Equals</c> calls, so what the test reads is exactly the
///         work the search did — deterministic, and blind to how loaded the machine is.
///     </para>
///     <para>
///         ⚠ <b>The instrument is proved before it is trusted.</b> A counter that could not see
///         <c>IndexOf</c> would read zero whatever the lookup did, and zero is what the fixed code
///         reads too. So each test first asks the list for its last leaf directly and requires the
///         counter to move by the leaf count — the same search the defect ran, seen by the same
///         counter.
///     </para>
/// </remarks>
public class InlineNeighbourCostTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>The tally a counting leaf writes to — one per document, so parallel tests cannot share one.</summary>
    sealed class Tally {
        public long Comparisons;
    }

    /// <summary>A leaf that counts every equality comparison made against it.</summary>
    sealed class CountingLeaf : UiElement {
        public Tally? Tally;

        public override bool Equals(object? obj) {
            if (Tally is { } tally) {
                tally.Comparisons++;
            }

            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>A block container of <paramref name="count" /> inline leaves, not yet laid out.</summary>
    static (UiDocument Document, UiElement Container, CountingLeaf[] Leaves, Tally Tally) Paragraph(int count) {
        var document = new UiDocument(900f, 600f);
        document.Fonts.Register("Test", Font);

        document.Load(
            """
            root      { width: 800px; height: 600px; }
            container { display: block; width: 800px; }
            leaf      { font-family: Test; font-size: 16px; line-height: 20px; display: inline; white-space: pre-line; }
            """
        );

        var tally = new Tally();
        var container = document.Root.Add("container");
        var leaves = new CountingLeaf[count];

        for (var i = 0; i < count; i++) {
            var leaf = document.Create<CountingLeaf>("leaf", container);
            leaf.Text = "ab ";
            leaves[i] = leaf;
        }

        // Only now, so that building the tree is not counted — `Attach` compares nothing, but the
        // test is about the passes and should not depend on that.
        foreach (var leaf in leaves) {
            leaf.Tally = tally;
        }

        return (document, container, leaves, tally);
    }

    /// <summary>Proves the counter sees a positional search, and resets it.</summary>
    static void ProveInstrument(UiElement container, CountingLeaf[] leaves, Tally tally) {
        var before = tally.Comparisons;
        var at = container.ChildList.IndexOf(leaves[^1]);

        Assert.Equal(leaves.Length - 1, at);
        Assert.True(tally.Comparisons - before >= leaves.Length, $"the counter saw {tally.Comparisons - before} comparisons for a search of {leaves.Length}");

        tally.Comparisons = 0;
    }

    /// <summary>
    ///     The pass that lays out a thousand leaves makes a number of comparisons bounded by a small
    ///     multiple of the leaf count. The defect made about n² of them — measure and <c>Arrange</c>
    ///     each asked <c>Before</c> and <c>After</c> of every leaf — which for 1 000 leaves is about
    ///     two million against this bound of eight thousand.
    /// </summary>
    [Fact]
    public void Laying_out_a_paragraph_of_leaves_compares_each_leaf_a_bounded_number_of_times() {
        var (document, container, leaves, tally) = Paragraph(1000);
        ProveInstrument(container, leaves, tally);

        document.Update();

        // The lookups ran: every leaf but the last has a word after it, so its trailing space does
        // not hang — which only `After` can have answered.
        Assert.NotNull(leaves[0].Block());
        Assert.True(tally.Comparisons <= 8L * leaves.Length, $"{tally.Comparisons} comparisons for {leaves.Length} leaves");
    }

    /// <summary>
    ///     ⚠ <b>The stored position is right after every kind of structural edit</b> — append,
    ///     reorder, reparent in and out, and removal — which is what replacing the search with a store
    ///     has to keep. Each read is checked against the search it replaced, interleaved with the edits
    ///     rather than once at the end, because a list renumbered by an early read and then edited
    ///     without being marked stale is exactly the defect a single final read would miss.
    /// </summary>
    [Fact]
    public void The_position_follows_every_structural_edit() {
        var document = new UiDocument(400f, 400f);
        var a = document.Root.Add("box");
        var b = document.Root.Add("box");
        var rows = Enumerable.Range(0, 6).Select(_ => a.Add("row")).ToArray();

        void Agree() {
            foreach (var parent in new[] { document.Root, a, b }) {
                var list = parent.ChildList;

                for (var i = 0; i < list.Count; i++) {
                    Assert.Equal(i, list[i].IndexInParent);
                }
            }
        }

        Agree();

        document.Move(rows[5], 0);
        Agree();

        document.Move(rows[1], 4);
        Agree();

        document.Reparent(rows[2], b);
        Agree();

        document.Reparent(rows[3], b, 0);
        Agree();

        document.Reparent(rows[2], a, 1);
        Agree();

        rows[0].Remove();
        Assert.Equal(-1, rows[0].IndexInParent);
        Agree();

        a.Add("row");
        Agree();
    }

    /// <summary>
    ///     ⚠ <b>The steady state</b>: a pass that re-arranges a settled paragraph pays the neighbour
    ///     check again for every enrolled leaf (<c>UiDocument.Arrange</c> →
    ///     <c>InlineEdgesMoved</c>), so the cost must stay linear there too. Doubling the leaves may
    ///     at most roughly double the comparisons — the defect quadrupled them.
    /// </summary>
    [Fact]
    public void A_pass_over_a_settled_paragraph_is_linear_in_its_leaves() {
        long Cost(int count) {
            var (document, container, leaves, tally) = Paragraph(count);
            document.Update();
            ProveInstrument(container, leaves, tally);

            // A change that re-runs the style walk, and with it the enrolment and the Arrange check,
            // over every leaf, without changing any leaf's text.
            document.Root.AddClass("again");
            document.Update();

            return tally.Comparisons;
        }

        var small = Cost(1000);
        var large = Cost(2000);

        Assert.True(large <= 3L * Math.Max(small, 1000), $"1 000 leaves cost {small} comparisons and 2 000 cost {large}");
        Assert.True(large <= 8L * 2000, $"{large} comparisons for 2 000 leaves");
    }
}
