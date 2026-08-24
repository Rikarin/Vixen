// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Water.Tests;

/// <summary>The two panels' readout blocks, held to the loops they replace.</summary>
/// <remarks>
///     ⚠ <b>The assertion is the whole element tree with every rectangle in it, not a property
///     read.</b> A part that draws the same rows a pixel to the left is a defect and a count
///     assertion cannot see it, so this builds the hand-written form, writes down what the document
///     looks like, throws it away, builds the part in exactly the same place, and compares the two
///     dumps as strings. Same place, because <c>UiTest.Tree</c> prints absolute positions.
/// </remarks>
public sealed class WaterFactsTests : IDisposable {
    readonly UiTest test = UiTest.Create(320f, 600f);

    public WaterFactsTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
    }

    /// <summary>What <c>RefreshZoneFacts</c> wrote out: rows, then a refusal if there is one.</summary>
    static void HandWritten(UiElement into, IEnumerable<(string Label, string Value)> facts, string? why) {
        foreach (var (label, value) in facts) {
            var row = into.Add<FactRow>();

            row.Name = label;
            row.Value = value;
        }

        if (why is not null) {
            into.Add("water-refusal").Add("text").Text = why;
        }
    }

    string Dump(Func<UiElement> build) {
        var host = build();

        test.Frames(2);

        var tree = test.Tree();

        host.Remove();
        test.Frames(2);

        return tree;
    }

    static (string Label, string Value)[] Zone => [
        ("Texels", "1024 × 1024"),
        ("Snap grid", "0.5 m (derived)"),
        ("Memory", "4 MB (derived)")
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("the snap grid is not a whole number of texels")]
    public void The_zone_part_builds_the_tree_the_hand_written_loop_did(string? why) {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("water-zone-facts");

            HandWritten(host, Zone, why);

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<WaterZoneFacts>();

            block.Show(Zone, why);

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>And it is now the <i>zone</i> part under a second tag, which is the claim.</b> There
    ///     were two near-identical types here because a component's host tag was a compile-time
    ///     header; the tag has always been an argument to <c>UiDocument.Adopt</c>, so one type serves
    ///     both panels and the dump is what says the tree did not move. The refusal arm is unbuilt
    ///     rather than hidden, because this caller passes no reason.
    /// </summary>
    [Fact]
    public void The_body_part_builds_the_tree_the_hand_written_loop_did() {
        (string Label, string Value)[] rows = [
            ("Bodies drawn", "3"), ("Points laid", "12"), ("Terrains to carve", "1")
        ];

        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("water-facts");

            HandWritten(host, rows, null);

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<WaterZoneFacts>("water-facts");

            block.Show(rows, null);

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ Both states of the notice, because the empty one is what <c>RefreshWaterFacts</c> leaves
    ///     behind on every redraw and the full one is what <c>Report</c> puts there.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Create zone is not available: there is no scene open to put one in.")]
    public void The_notice_builds_the_tree_the_hand_written_one_did(string message) {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("water-notice");

            if (message.Length > 0) {
                host.Add("water-refusal").Add("text").Text = message;
            }

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<WaterNotice>();

            block.Notice = message;

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>A second refusal replaces the first rather than being ignored.</b> The <c>@if</c>'s arm
    ///     does not change when one message succeeds another — the predicate is "is there a message" —
    ///     so a readout that had closed over the message instead of going back to the signal would
    ///     show the first one for ever. This is the test that catches it.
    /// </summary>
    [Fact]
    public void A_second_notice_replaces_the_first() {
        var block = test.Document.Root.Add<WaterNotice>();

        block.Notice = "the scene has not been saved";
        test.Frames(2);

        Assert.Equal("the scene has not been saved", block.Children[0].Children[0].Text);

        block.Notice = "the write failed";
        test.Frames(2);

        Assert.Single(block.Children);
        Assert.Equal("the write failed", block.Children[0].Children[0].Text);

        block.Clear();
        test.Frames(2);

        Assert.Empty(block.Children);
    }

    /// <summary>Each part answers to the tag the panel already created, which is the port's premise.</summary>
    /// <remarks>
    ///     ⚠ <b>And carries neither of a <c>Control</c>'s two classes.</b> These stand as direct
    ///     children of a <c>DockPanel</c>, where <c>dock-panel.scrolls &gt; * { flex-shrink: 0 }</c>
    ///     reaches them — a box around them, or a different tag, is the shape that makes a tall panel
    ///     compress instead of scroll.
    /// </remarks>
    [Fact]
    public void The_parts_are_the_elements_the_panels_created_rather_than_boxes_around_them() {
        var zone = test.Document.Root.Add<WaterZoneFacts>();
        var body = test.Document.Root.Add<WaterZoneFacts>("water-facts");
        var notice = test.Document.Root.Add<WaterNotice>();

        test.Frames(2);

        Assert.Equal("water-zone-facts", zone.Tag);
        Assert.Equal("water-facts", body.Tag);
        Assert.Equal("water-notice", notice.Tag);

        foreach (var part in new UiElement[] { zone, body, notice }) {
            Assert.Same(test.Document.Root, part.Parent);
            Assert.False(part.HasClass("variant-default"));
            Assert.False(part.HasClass("size-md"));
        }
    }

    public void Dispose() => test.Dispose();
}
