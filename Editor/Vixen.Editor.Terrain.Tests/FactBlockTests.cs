// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The derived-numbers block, held to the loop it replaces.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion is the whole element tree with every rectangle in it, not a property
///         read.</b> A part that draws the same rows a pixel to the left is a defect and a count
///         assertion cannot see it, so this builds the hand-written form, writes down what the
///         document looks like, throws it away, builds the part in exactly the same place, and
///         compares the two dumps as strings. Same place, because <c>UiTest.Tree</c> prints absolute
///         positions. <c>FactRowTests</c> in <c>Vixen.Editor.Ui.Tests</c> is the same shape one level
///         down.
///     </para>
///     <para>
///         ⚠ <b>Three sheets and not four.</b> <c>fact-row</c>'s own rules are
///         <c>AssetEditorTheme</c>'s, which this assembly does not reference and should not start to
///         for a row — <c>EditorApplication</c> installs both sheets into the one document, which is
///         where the shipped rectangles come from. What is asserted here is that the two spellings
///         agree, which is true under any sheet set and is the claim the port has to make.
///     </para>
/// </remarks>
public sealed class FactBlockTests : IDisposable {
    readonly UiTest test = UiTest.Create(320f, 600f);

    public FactBlockTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
    }

    /// <summary>What the three panels each wrote out: a container, emptied and refilled per row.</summary>
    static void HandWritten(UiElement into, IEnumerable<(string Label, string Value)> facts) {
        foreach (var (label, value) in facts) {
            var row = into.Add<FactRow>();

            row.Name = label;
            row.Value = value;
        }
    }

    /// <summary>Builds something at the root, writes the document down, and takes it away again.</summary>
    /// <param name="build">Makes the element and hands it back so it can be removed.</param>
    string Dump(Func<UiElement> build) {
        var host = build();

        test.Frames(2);

        var tree = test.Tree();

        host.Remove();
        test.Frames(2);

        return tree;
    }

    static (string Label, string Value)[] Grass => [
        ("Ring size", "42.0 MB (derived)"),
        ("Cells resident", "1,024 (derived)"),
        ("Effective density", "75 % (derived)"),
        ("Refused", "the ring is larger than the budget")
    ];

    [Fact]
    public void The_part_builds_the_tree_the_hand_written_loop_did() {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("terrain-facts");

            HandWritten(host, Grass);

            return host;
        });

        // ⚠ The part *is* the `terrain-facts` element rather than something inside one, which is the
        // whole reason it can stand where `panel.Add("terrain-facts")` stood. A box around it is
        // precisely what `dock-panel.scrolls > *` would stop reaching the content.
        var part = Dump(() => {
            var block = test.Document.Root.Add<FactBlock>();

            block.Show(Grass);

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>A second <c>Show</c> replaces the rows rather than appending them</b>, which is what
    ///     <c>Clear</c> plus a refill did. The keys carry the values, so a changed reading is a
    ///     different key and its region is built fresh; the test that catches the opposite mistake is
    ///     the one that shows twice.
    /// </summary>
    [Fact]
    public void Showing_again_replaces_the_rows_rather_than_adding_to_them() {
        var block = test.Document.Root.Add<FactBlock>();

        block.Show(Grass);
        test.Frames(2);

        Assert.Equal(4, block.Children.Count);

        block.Show([("Ring size", "9.0 MB (derived)")]);
        test.Frames(2);

        Assert.Single(block.Children);
        Assert.Equal("9.0 MB (derived)", ((FactRow) block.Children[0]).ValueText.Text);
    }

    /// <summary>
    ///     ⚠ <b>Two rows that read the same are two rows.</b> <c>BuildContext.For</c> cannot reconcile
    ///     two equal keys in one loop, which is why the key carries the slot as well as the words —
    ///     and a growth report of four zeroes is not a hypothetical shape.
    /// </summary>
    [Fact]
    public void Two_identical_readings_are_two_rows() {
        var block = test.Document.Root.Add<FactBlock>();

        block.Show([("Refused", "0"), ("Refused", "0"), ("Refused", "0")]);
        test.Frames(2);

        Assert.Equal(3, block.Children.Count);
    }

    /// <summary>The block answers to the tag the panels already created, which is the port's premise.</summary>
    [Fact]
    public void It_is_the_terrain_facts_element_rather_than_a_box_around_one() {
        var block = test.Document.Root.Add<FactBlock>();

        test.Frames(2);

        Assert.Equal("terrain-facts", block.Tag);
        Assert.Same(test.Document.Root, block.Parent);

        // And it is not a `Control`, so it carries none of the two classes one gives itself.
        Assert.False(block.HasClass("variant-default"));
        Assert.False(block.HasClass("size-md"));
    }

    public void Dispose() => test.Dispose();
}
