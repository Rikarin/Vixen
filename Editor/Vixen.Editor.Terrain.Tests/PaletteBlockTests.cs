// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>Wave 7's two terrain parts, each held to the loop it replaces.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion is the whole element tree with every rectangle in it, plus the flags
///         the tree cannot see.</b> <c>UiTest.Tree</c> prints tag, classes, rectangle and text and
///         says nothing about <c>ElementState</c> — which for a palette of check boxes is exactly
///         where the risk is, because a ticked box differs from an unticked one in nothing else. So
///         the dump carries a <c>state=</c> line per element as well. <c>FactBlockTests</c> beside
///         this file is the same shape for the part wave 5 built.
///     </para>
///     <para>
///         ⚠ <b>Three sheets and not four</b>, for <c>FactBlockTests</c>' reason: <c>fact-row</c>'s
///         own rules are <c>AssetEditorTheme</c>'s, which this assembly does not reference. What is
///         asserted is that the two spellings agree, which is true under any sheet set and is the
///         claim the port has to make.
///     </para>
/// </remarks>
public sealed class PaletteBlockTests : IDisposable {
    readonly UiTest test = UiTest.Create(320f, 600f);

    public PaletteBlockTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
    }

    /// <summary>The tree and the states, because a ticked box differs from an untitled one in nothing else.</summary>
    string Dump(Func<UiElement> build) {
        var host = build();

        test.Frames(2);

        var text = new System.Text.StringBuilder(test.Tree());

        text.AppendLine().AppendLine("--- state ---");
        States(test.Document.Root, 0, text);

        host.Remove();
        test.Frames(2);

        return text.ToString();
    }

    static void States(UiElement element, int depth, System.Text.StringBuilder text) {
        text.Append(' ', depth * 2).Append(element.Tag).Append(' ').Append(element.State).AppendLine();

        foreach (var child in element.Children) {
            States(child, depth + 1, text);
        }
    }

    static (string Label, string Value)[] Layers => [
        ("Roads", "reserved — the spline tool owns it, locked"),
        ("Detail", "hidden"),
        ("Base", "visible")
    ];

    /// <summary>What the terrain panel wrote out for its layer stack.</summary>
    static void HandWrittenLayers(UiElement into) {
        foreach (var (label, value) in Layers) {
            var row = into.Add<FactRow>();

            row.Name = label;
            row.Value = value;
        }
    }

    [Fact]
    public void The_layer_block_builds_the_tree_the_hand_written_loop_did() {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("terrain-layers");

            HandWrittenLayers(host);

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<LayerBlock>();

            block.Show(Layers);

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>A second type rather than a parameter on <c>FactBlock</c>, and the tag is why.</b>
    ///     <c>@tag</c> is a compile-time directive, so "the same part under another name" is not
    ///     sayable — and the tag is the whole of what tells the two lists apart, since neither is
    ///     styled by any sheet in the tree.
    /// </summary>
    [Fact]
    public void The_layer_block_is_the_terrain_layers_element_rather_than_a_box_around_one() {
        var block = test.Document.Root.Add<LayerBlock>();

        test.Frames(2);

        Assert.Equal("terrain-layers", block.Tag);
        Assert.Empty(block.Children);
    }

    /// <summary>What the foliage panel wrote out for a palette with types in it.</summary>
    static void HandWrittenPalette(UiElement into, params (string Name, bool Chosen, float Radius, bool Derived)[] types) {
        foreach (var (name, chosen, radius, derived) in types) {
            var row = into.Add("fact-row");
            var box = row.Add<CheckBox>();

            box.Label = name;
            box.IsChecked = chosen;

            row.Add("fact-value").Add("text").Text = derived
                ? "derived — nothing about it is in any file"
                : string.Create(CultureInfo.InvariantCulture, $"stored, spacing {radius:0.##} m");
        }
    }

    [Fact]
    public void The_palette_block_builds_the_tree_the_hand_written_loop_did() {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("foliage-palette");

            HandWrittenPalette(host, ("Oak", true, 4.5f, false), ("Fern", false, 0f, true), ("Pine", true, 6f, false));

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<PaletteBlock>();

            block.Show([
                new(0, "Oak", true, "stored, spacing 4.5 m"),
                new(1, "Fern", false, "derived — nothing about it is in any file"),
                new(2, "Pine", true, "stored, spacing 6 m")
            ]);

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>An empty palette says why it is empty, and that row is a <c>FactRow</c>.</b> Entering
    ///     a mode that does nothing and says nothing is the state every one of these toolsets puts a
    ///     new user in; the sentence is the cure, and it is the same row the hand-written panel built.
    /// </summary>
    [Fact]
    public void The_empty_palette_builds_the_tree_the_hand_written_refusal_did() {
        var handWritten = Dump(() => {
            var host = test.Document.Root.Add("foliage-palette");
            var row = host.Add<FactRow>();

            row.Name = "No types";
            row.Value = "add a .vxfoliage or .vxgrass to the palette";

            return host;
        });

        var part = Dump(() => {
            var block = test.Document.Root.Add<PaletteBlock>();

            block.ShowNothing("add a .vxfoliage or .vxgrass to the palette");

            return block;
        });

        Assert.Equal(handWritten, part);
    }

    /// <summary>
    ///     ⚠ <b>Ticking a box says which slot</b>, which is what makes this part need no
    ///     <c>refs</c>: the handler closes over the entry's own slot, and the slot is in the key.
    /// </summary>
    [Fact]
    public void Ticking_a_box_reports_the_slot_it_belongs_to() {
        var block = test.Document.Root.Add<PaletteBlock>();
        var said = new List<(int Slot, bool On)>();

        block.Chose = (slot, on) => said.Add((slot, on));

        block.Show([
            new(0, "Oak", false, "stored, spacing 4.5 m"),
            new(1, "Fern", false, "derived — nothing about it is in any file")
        ]);

        test.Frames(2);

        var second = (CheckBox) block.Children[1].Children[0];

        second.IsChecked = true;
        test.Frames(2);

        Assert.Equal([(1, true)], said);
    }

    /// <summary>Going from types to none replaces the list rather than adding the refusal under it.</summary>
    [Fact]
    public void Showing_nothing_after_something_replaces_the_rows() {
        var block = test.Document.Root.Add<PaletteBlock>();

        block.Show([new(0, "Oak", false, "stored, spacing 4.5 m")]);
        test.Frames(2);

        Assert.Single(block.Children);

        block.ShowNothing("add a .vxfoliage or .vxgrass to the palette");
        test.Frames(2);

        Assert.Single(block.Children);
        Assert.Equal("No types", ((FactRow) block.Children[0]).NameCell.Text);
    }

    /// <summary>The block answers to the tag the panel already created, which is the port's premise.</summary>
    [Fact]
    public void The_palette_block_is_the_foliage_palette_element_rather_than_a_box_around_one() {
        var block = test.Document.Root.Add<PaletteBlock>();

        test.Frames(2);

        Assert.Equal("foliage-palette", block.Tag);
    }

    public void Dispose() => test.Dispose();
}
