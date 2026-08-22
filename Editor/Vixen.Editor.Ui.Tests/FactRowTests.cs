// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The shared fact row, held to the four lines it replaces.</summary>
/// <remarks>
///     ⚠ <b>The assertion is the whole element tree with every rectangle in it, not a property
///     read.</b> A shared part that draws the same shape a pixel to the left is a defect and a
///     property assertion cannot see it, so this builds the hand-written form, writes down what the
///     document looks like, throws it away, builds the part in exactly the same place, and compares
///     the two dumps as strings. Same place, because <c>UiTest.Tree</c> prints absolute positions.
/// </remarks>
public sealed class FactRowTests : IDisposable {
    readonly UiTest test = UiTest.Create();

    public FactRowTests() {
        ControlTheme.Install(test.Document);
        EditorTheme.Install(test.Document);
    }

    /// <summary>The four lines every panel that shows derived numbers has a copy of.</summary>
    static void HandWritten(UiElement into, string label, string value) {
        var row = into.Add("fact-row");

        row.Add("fact-name").Text = label;
        row.Add("fact-value").Add("text").Text = value;
    }

    string Dump(Action<UiElement> build) {
        var host = test.Document.Root.Add("water-zone-facts");

        build(host);
        test.Frames(2);

        var tree = test.Tree();

        host.Remove();
        test.Frames(2);

        return tree;
    }

    [Fact]
    public void The_part_builds_the_tree_the_hand_written_helper_did() {
        var handWritten = Dump(host => {
            HandWritten(host, "Texels", "1024 × 1024");
            HandWritten(host, "Snap grid", "0.5 m (derived)");
            HandWritten(host, "Memory", "4 MB");
        });

        var part = Dump(host => {
            foreach (var (label, value) in new[] {
                ("Texels", "1024 × 1024"), ("Snap grid", "0.5 m (derived)"), ("Memory", "4 MB")
            }) {
                var row = host.Add<FactRow>();

                row.Name = label;
                row.Value = value;
            }
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ The name is the cell's own <c>Text</c> and the value is a <c>text</c> child, which is
    ///     the asymmetry the four callers already had. Written down because it is the one thing a
    ///     future tidy-up would "fix" and change the layout doing it.
    /// </summary>
    [Fact]
    public void The_name_is_the_cells_own_text_and_the_value_is_a_child() {
        var row = test.Document.Root.Add<FactRow>();

        row.Name = "Bodies drawn";
        row.Value = "3";

        test.Frames(2);

        Assert.Equal("fact-row", row.Tag);
        Assert.Equal("fact-name", row.Children[0].Tag);
        Assert.Equal("Bodies drawn", row.Children[0].Text);
        Assert.Empty(row.Children[0].Children);

        Assert.Equal("fact-value", row.Children[1].Tag);
        Assert.Null(row.Children[1].Text);
        Assert.Equal("text", row.Children[1].Children[0].Tag);
        Assert.Equal("3", row.Children[1].Children[0].Text);
    }

    /// <summary>
    ///     A row assigned before it is anywhere still says what it was told, because
    ///     <c>new FactRow()</c> is an order <c>Add&lt;T&gt;()</c> cannot produce but a caller can.
    /// </summary>
    [Fact]
    public void A_row_remembers_what_it_was_told_before_it_had_a_document() {
        var row = new FactRow { Name = "Points laid", Value = "12" };

        test.Document.Adopt(row, null, test.Document.Root);
        test.Frames(2);

        Assert.Equal("Points laid", row.NameCell.Text);
        Assert.Equal("12", row.ValueText.Text);
    }

    public void Dispose() => test.Dispose();
}
