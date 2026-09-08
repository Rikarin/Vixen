// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The panel's layout comes out of a stylesheet, and the panel still has one.</summary>
/// <remarks>
///     <para>
///         <b>The first half of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>.</b>
///         Nine call sites in <c>LayerStackView</c>'s constructor wrote <c>display</c>,
///         <c>flex-direction</c>, <c>flex-grow</c> and a width through <c>SetStyle</c>; they are
///         <c>TexturingTheme.vcss</c> now, installed by the view into the document its host is in.
///     </para>
///     <para>
///         ⚠ <b>The assertions are the ones a missing sheet makes false, which took choosing.</b>
///         <c>NodeGraphThemeTests</c> records the trap in the same shape: an element with no
///         stylesheet takes CSS's initial <c>flex-direction: row</c>, so
///         <c>layer-stack { flex-direction: row }</c> is a rule whose absence changes nothing and a
///         test asserting it passes with the install deleted. What the sheet decides here is the
///         <em>column</em> — the left column stacks its binding row above its list — and the preview
///         column's fixed width. Both are read as geometry after a layout pass rather than as
///         declarations, which is the difference between "the rule is there" and "the rule reached
///         the panel".
///     </para>
/// </remarks>
public class LayerStackThemeTests {
    /// <summary>⚠ The left column stacks and the preview column is 280 wide, after a real layout.</summary>
    [Fact]
    public void The_sheet_reaches_the_panel_the_module_builds() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        var preview = Only(panel, "layer-stack-preview");
        var binding = Only(panel, "layer-stack-binding");
        var list = Only(panel, "layer-stack-list");

        // ⚠ The declaration with no initial value behind it: a column of a fixed width, beside a
        // column that grows. Without the sheet this element is as wide as its widest child.
        Assert.Equal(280f, preview.Width);

        // ⚠ And `flex-direction: column` on the left column, read as the thing it decides. The
        // binding row is *above* the list and starts at the same left edge; the initial `row` would
        // put them side by side at the same top, so this is false in exactly the way a missing sheet
        // makes it false rather than being true of any laid-out panel.
        Assert.True(
            binding.Top < list.Top,
            $"the binding row is at y={binding.Top} and the layer list at y={list.Top}: the left "
            + "column is laying its children out in a row, which is CSS's initial direction and what "
            + "`layer-stack-rows { flex-direction: column }` in TexturingTheme.vcss is for. The sheet "
            + "did not reach this panel."
        );

        Assert.Equal(binding.Left, list.Left);

        // The whole view is still a row — asserted last and as a consequence, because it is the one
        // declaration the initial value would have given for nothing.
        Assert.True(preview.Left > list.Left, "the preview column is not beside the rows.");
    }

    /// <summary>⚠ And a second panel build does not load a second copy of the sheet.</summary>
    /// <remarks>
    ///     <b>A panel's factory really does re-run</b>: opening any other panel relays the workspace
    ///     out and rebuilds this one, which is why <c>LayerStackView</c> is disposed and replaced
    ///     there. <c>UiDocument.Load</c> appends and has no notion of a sheet it already holds, so an
    ///     unguarded install would grow the editor's stylesheet for as long as the session lasted.
    /// </remarks>
    [Fact]
    public void The_sheet_is_loaded_once_per_document_however_often_the_panel_is_rebuilt() {
        using var fixture = new TexturingFixture();

        Assert.True(TexturingTheme.Install(fixture.Shell.Document));
        Assert.False(TexturingTheme.Install(fixture.Shell.Document));

        using var second = new TexturingFixture();

        // A different document is a different answer, which is what makes the guard a guard rather
        // than a one-shot flag: two editor windows are two documents.
        Assert.True(TexturingTheme.Install(second.Shell.Document));
    }

    /// <summary>⚠ No typed control in the panel is renamed out of the theme that styles it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1071">#1071</a>.</b>
    ///         <c>UiElement.Add&lt;T&gt;(string)</c>'s first parameter is the <em>tag</em>, so
    ///         <c>row.Add&lt;Slider&gt;("layer-stack-opacity")</c> — the convention twenty-eight
    ///         controls in this panel were built with — produced an element that
    ///         <c>ControlTheme.vcss</c>'s <c>slider</c>, <c>button</c>, <c>checkbox</c>,
    ///         <c>select</c> and <c>textbox</c> rules no longer matched.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Derived rather than listed, which is what makes it hold for the twenty-ninth
    ///         control.</b> A plain <c>Add(string)</c> container is exactly a
    ///         <see cref="UiElement" />; anything else is a control and carries a tag of its own that
    ///         a stylesheet is written against. So the rule is "a subclass may not answer to a
    ///         <c>layer-stack-</c> name", and no roll call of the twenty-eight has to be maintained
    ///         beside the view.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a measurement, because the sentence above is satisfied by a panel that draws
    ///         no controls at all.</b> <c>slider { height: 20px; min-width: 80px }</c> in
    ///         <c>ControlTheme.vcss</c>, narrowed to 18 by <c>EditorTheme.vcss</c>, are both type
    ///         selectors with no other source — so a renamed slider stretches to its row instead,
    ///         which measures 30 here. That height, read after a layout pass, is the theme arriving
    ///         rather than the theme being declared.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_typed_control_in_the_panel_answers_to_a_layer_stack_tag() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        List<string> renamed = [];
        var controls = 0;

        Walk(panel);

        // The instrument first: a panel that built nothing would satisfy the emptiness below.
        Assert.True(controls > 20, $"the panel drew {controls} controls, so the check below is vacuous.");

        Assert.True(
            renamed.Count == 0,
            $"these controls answer to a tag of their own naming — {string.Join(", ", renamed)} — so every "
            + "type selector in ControlTheme.vcss stops matching them. Pass the name as a class instead: "
            + "Add<T>(null, null, \"layer-stack-…\"). #1071."
        );

        // ⚠ The half that is geometry rather than a name. `slider { height: 20px; min-width: 80px }`
        // is ControlTheme.vcss and `slider { height: 18px }` is EditorTheme.vcss narrowing it — both
        // type selectors, and neither has any other source, so a renamed slider takes its height and
        // its width from nothing at all. 18 rather than 20 because the editor's sheet wins, which is
        // itself worth asserting: this reads the document the panel is really in.
        var opacity = Assert.IsType<Slider>(Only(panel, "layer-stack-opacity"));

        Assert.Equal(18f, opacity.Height);
        Assert.True(opacity.Width >= 80f, $"the opacity slider is {opacity.Width} wide, under the 80px minimum.");

        void Walk(UiElement element) {
            if (element.GetType() != typeof(UiElement)) {
                controls++;

                if (element.Tag.StartsWith("layer-stack-", StringComparison.Ordinal)) {
                    renamed.Add($"{element.GetType().Name} as '{element.Tag}'");
                }
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>The only element under that name — its tag, or its class.</summary>
    static UiElement Only(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return Assert.Single(found);

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal) || element.HasClass(tag)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
