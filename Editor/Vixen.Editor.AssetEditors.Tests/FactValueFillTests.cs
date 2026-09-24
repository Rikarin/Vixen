// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A field in a fact row's value cell is as wide as the cell (#1404).</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>fact-value { flex-grow: 1 }</c> grew the cell and not what was in it.</b> The cell
///         has no <c>flex-direction</c>, so it is a row, and a field in a row takes its content
///         width: an empty text box was its two paddings and two borders, 18 px in a 172 px cell. The
///         audio mixer's Parent and Sidechain boxes are empty for most buses, which is where the issue
///         measured it — and a filled one grew as it was typed into.
///     </para>
///     <para>
///         ⚠ <b>The cell holds more than fields</b> — a check box, a toggle, a button, a plain
///         <c>text</c> — so the rule is the same list <c>key-value-value</c> and <c>field-content</c>
///         use, and a check box in a cell is the control that must keep its own width.
///     </para>
/// </remarks>
public class FactValueFillTests {
    /// <summary>A field's padding and borders, which is all an empty one was.</summary>
    const float Chrome = 2 * 8f + 2 * 1f;

    [Fact]
    public void An_empty_text_box_in_a_fact_row_fills_the_value_cell() {
        using var harness = new ViewHarness();
        harness.Ui.Load("fact-column { width: 400px; flex-direction: column; align-items: stretch; }");

        var column = harness.Ui.Document.Root.Add("fact-column");

        var text = column.Add("fact-row");
        text.Add("fact-name").Text = "Parent";
        var box = text.Add("fact-value").Add<TextBox>();

        var toggle = column.Add("fact-row");
        toggle.Add("fact-name").Text = "Looping";
        var check = toggle.Add("fact-value").Add<CheckBox>();

        harness.Ui.Frame();

        var cell = box.Parent!;

        // The premise: the cell is wide, so a narrow field is the rule's doing and not a squeeze.
        Assert.True(cell.Width > 200f, $"the value cell is {cell.Width} px wide");

        Assert.True(box.Width > Chrome + 1f, $"an empty text box is {box.Width} px of a {cell.Width} px cell");
        Assert.Equal(cell.Width, box.Width, 0.5f);

        // The control: a check box is the size it is.
        Assert.True(check.Width < check.Parent!.Width / 2, $"a check box is {check.Width} px of a {check.Parent!.Width} px cell");
    }

    /// <summary>The case the issue measured: the mixer's bus fields, with Parent and Sidechain empty.</summary>
    [Fact]
    public void The_mixers_bus_fields_fill_their_cells() {
        using var harness = new ViewHarness();
        harness.Ui.Load("mixer-editor { width: 1000px; max-width: 1000px; height: 600px; flex-grow: 0; }");

        var document = new AudioMixerDocument(
            harness.Project.Project,
            AssetId.Empty,
            harness.Project.Write("Assets/Game.vxmixer", string.Empty)
        );

        document.AddBus("Music", "Master");

        var view = harness.Ui.Document.Root.Add<AudioMixerView>();
        view.Show(document);
        harness.Ui.Frame();

        var strip = view.Strips.Children.First(candidate => candidate.Children.Any(child => child.Text == "Music"));

        harness.Ui.Document.Dispatch(
            new PointerEvent {
                X = strip.AbsoluteLeft + (strip.Width / 2f),
                Y = strip.AbsoluteTop + 4f,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        harness.Ui.Frame();
        Assert.Equal("Music", view.Selected);

        var boxes = Descendants(view.Fields).OfType<TextBox>().ToList();

        // Name, Parent and Sidechain — and Sidechain is empty, which is the 18 px case.
        Assert.Equal(3, boxes.Count);
        Assert.Contains(boxes, box => string.IsNullOrEmpty(box.Value));

        foreach (var box in boxes) {
            Assert.True(box.Parent!.Width > 100f, $"the value cell is {box.Parent!.Width} px wide");
            Assert.Equal(box.Parent!.Width, box.Width, 0.5f);
        }
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
