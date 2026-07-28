// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>The child runs, and the growth policy that keeps appending amortised.</summary>
/// <remarks>
///     ⚠ <b>A cost test and a correctness test, and the second is the one that catches a wrong fix.</b>
///     Reserving space in a shared arena means a run has slots after it that hold stale entries, and
///     every reader has to be bounded by the count rather than by what is in the arena. A capacity
///     scheme that got that wrong would be fast and would hand back children nobody added.
/// </remarks>
public class ChildArenaTests {
    [Fact]
    public void Interleaved_appends_do_not_copy_quadratically() {
        // Two parents filled a child at a time, which is what a reconciler walking a keyed list does
        // and what the old implementation was worst at: neither run is ever the last thing in the
        // arena when it is asked to grow, so every append relocated the whole run.
        var tree = new StyleTree(new NameTable());
        var root = tree.CreateElement("root", null, null, []);
        var left = tree.CreateElement("div", root, null, []);
        var right = tree.CreateElement("div", root, null, []);

        const int each = 2_000;

        for (var i = 0; i < each; i++) {
            tree.CreateElement("div", left, null, []);
            tree.CreateElement("div", right, null, []);
        }

        Assert.Equal(each, tree.GetChildCount(left));
        Assert.Equal(each, tree.GetChildCount(right));

        // ⚠ The arena's own length is the measurement, because it counts the *slots* the scheme asked
        // for and is not a clock. Copying a run appends its whole length again, so the quadratic
        // version reaches roughly n²/2 entries for each parent — about four million here — while a
        // doubling one stays within a small multiple of the children that exist. Timing this instead
        // would be a test that fails on a loaded machine.
        Assert.True(
            tree.ChildArenaLength < 16 * each,
            $"the child arena holds {tree.ChildArenaLength} entries for {2 * each} children."
        );
    }

    [Fact]
    public void A_run_that_grows_where_it_stands_costs_nothing_extra() {
        var tree = new StyleTree(new NameTable());
        var root = tree.CreateElement("root", null, null, []);
        var parent = tree.CreateElement("div", root, null, []);

        for (var i = 0; i < 1_000; i++) {
            tree.CreateElement("div", parent, null, []);
        }

        Assert.Equal(1_000, tree.GetChildCount(parent));
        Assert.True(tree.ChildArenaLength < 4_096, $"{tree.ChildArenaLength} entries for 1 000 children.");
    }

    [Fact]
    public void Every_child_comes_back_in_the_order_it_went_in() {
        // The correctness half. Reserved slots hold whatever the arena had there before, so a reader
        // bounded by the arena rather than by the count would return them.
        var tree = new StyleTree(new NameTable());
        var root = tree.CreateElement("root", null, null, []);
        var left = tree.CreateElement("div", root, null, []);
        var right = tree.CreateElement("div", root, null, []);

        var expectedLeft = new List<StyleNodeId>();
        var expectedRight = new List<StyleNodeId>();

        for (var i = 0; i < 300; i++) {
            expectedLeft.Add(tree.CreateElement("div", left, null, []));
            expectedRight.Add(tree.CreateElement("div", right, null, []));
        }

        for (var i = 0; i < 300; i++) {
            Assert.Equal(expectedLeft[i], tree.GetChild(left, i));
            Assert.Equal(expectedRight[i], tree.GetChild(right, i));
        }
    }

    [Fact]
    public void Removing_and_appending_keep_the_run_consistent() {
        Gen.Select(Gen.Int[0, 100_000], Gen.Int[4, 40]).Sample(shape => {
                var (seed, operations) = shape;
                var random = new Random(seed);

                var tree = new StyleTree(new NameTable());
                var root = tree.CreateElement("root", null, null, []);
                var parents = new[] {
                    tree.CreateElement("div", root, null, []),
                    tree.CreateElement("div", root, null, [])
                };

                var expected = new List<StyleNodeId>[] { [], [] };

                for (var op = 0; op < operations; op++) {
                    var which = random.Next(2);

                    if (expected[which].Count > 0 && random.Next(4) == 0) {
                        var at = random.Next(expected[which].Count);
                        tree.Remove(expected[which][at]);
                        expected[which].RemoveAt(at);
                        continue;
                    }

                    expected[which].Add(tree.CreateElement("div", parents[which], null, []));
                }

                // A removal shifts the run down inside its own space and an append writes at the end
                // of the count — the two together are where an off-by-one in the capacity scheme
                // lands, and where a run can start reading its own reserved slots.
                for (var which = 0; which < 2; which++) {
                    Assert.Equal(expected[which].Count, tree.GetChildCount(parents[which]));

                    for (var i = 0; i < expected[which].Count; i++) {
                        Assert.Equal(expected[which][i], tree.GetChild(parents[which], i));
                        Assert.Equal(i, tree.IndexInParentOf(expected[which][i].Index));
                    }
                }
            }, iter: 200
        );
    }

    [Fact]
    public void Compaction_packs_the_runs_tight_again() {
        var tree = new StyleTree(new NameTable());
        var root = tree.CreateElement("root", null, null, []);
        var parent = tree.CreateElement("div", root, null, []);

        var children = new List<StyleNodeId>();

        for (var i = 0; i < 200; i++) {
            children.Add(tree.CreateElement("div", parent, null, []));
        }

        for (var i = 0; i < 150; i++) {
            tree.Remove(children[i]);
        }

        var before = tree.ChildArenaLength;
        var remap = new int[tree.Count];
        tree.Compact(remap);

        // Compaction is the moment reservation is given back — a compacted tree is one nobody is
        // part-way through building, so a tight run is right and the next append will reserve again.
        Assert.True(tree.ChildArenaLength < before);
        Assert.Equal(50, tree.GetChildCount(new StyleNodeId(remap[parent.Index])));
    }
}
