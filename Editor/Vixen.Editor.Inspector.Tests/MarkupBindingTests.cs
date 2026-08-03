// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>Doc 36 § P4: a tree that names members, joined to what it is editing after it is built.</summary>
/// <remarks>
///     ⚠ <b>Built by hand here rather than from a <c>.vxml</c>, and that is the right seam to test
///     at.</b> What the markup contributes is a <c>PropertyField</c> with a <c>Path</c> and an
///     attribute called <c>binding-path</c>; whether the emitter writes those correctly is
///     <c>Vixen.Ui.Markup</c>'s own suite. What this asserts is the half that has to be right for
///     either of them to mean anything — that the join finds the member, draws the row the generated
///     inspector would have drawn, and writes back through the one editing pipeline.
/// </remarks>
public class MarkupBindingTests : IDisposable {
    readonly UiDocument document = new(400f, 800f);
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-markup-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly ScratchDocument edited;

    public MarkupBindingTests() {
        InspectorTheme.Install(document);
        Directory.CreateDirectory(root);

        project = new(new ProjectPaths(root));
        edited = new(project);
    }

    public void Dispose() {
        document.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    InspectorTarget Target(params object[] objects) => new(objects, edited);

    [Fact]
    public void A_property_field_draws_the_row_the_generated_inspector_would() {
        var water = new WaterMaterial();
        var field = document.Root.Add<PropertyField>();

        field.Path = "Roughness";
        document.Update();

        Assert.Equal(1, MarkupBinding.Bind(document.Root, Target(water)));
        Assert.NotNull(field.Row);

        // The label the descriptor supplies, not the member name — which is the visible difference
        // between "a row" and "the row the panel already draws".
        Assert.Equal("Roughness", field.Row.Field.Member.DisplayName);
    }

    [Fact]
    public void Editing_a_property_field_writes_through_the_pipeline_and_can_be_undone() {
        var water = new WaterMaterial();
        var field = document.Root.Add<PropertyField>();

        field.Path = "Roughness";
        document.Update();

        MarkupBinding.Bind(document.Root, Target(water));

        Assert.True(field.Row!.Field.Write(0.75f));
        Assert.Equal(0.75f, water.Roughness);

        edited.Stack.Undo();
        Assert.Equal(0.2f, water.Roughness);
    }

    /// <summary>
    ///     ⚠ <b>A path is a string the compiler never sees.</b> A renamed member leaves markup that
    ///     resolves to nothing, and a row that quietly vanishes reads as the value being unset — so
    ///     the panel says which name it could not find.
    /// </summary>
    [Fact]
    public void A_path_that_names_nothing_says_so_in_the_panel() {
        var field = document.Root.Add<PropertyField>();

        field.Path = "Roughnes";
        document.Update();

        Assert.Equal(0, MarkupBinding.Bind(document.Root, Target(new WaterMaterial())));
        Assert.Null(field.Row);

        var text = Descendants(field).OfType<TextBlock>().Single();

        Assert.Contains("Roughnes", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_carrying_binding_path_reads_the_member_and_writes_it_back() {
        var water = new WaterMaterial { Roughness = 0.4f };
        var slider = document.Root.Add<Slider>();

        slider.Minimum = 0f;
        slider.Maximum = 1f;
        slider.SetAttribute("binding-path", "Roughness");
        document.Update();

        Assert.Equal(1, MarkupBinding.Bind(document.Root, Target(water)));
        Assert.Equal(0.4f, slider.Value, 3);

        slider.Value = 0.9f;
        document.Update();

        Assert.Equal(0.9f, water.Roughness, 3);
    }

    /// <summary>
    ///     ⚠ <b>A slider is a <c>float</c> and a numeric input is a <c>double</c>, and the member is
    ///     whichever it is.</b> Writing the control's own type straight through put a boxed
    ///     <c>double</c> on a <c>float</c> field — a row that appears to work and changes nothing.
    /// </summary>
    [Fact]
    public void A_numeric_input_writes_the_members_type_rather_than_its_own() {
        var water = new WaterMaterial();
        var number = document.Root.Add<NumericInput>();

        number.SetAttribute("binding-path", "Roughness");
        document.Update();

        MarkupBinding.Bind(document.Root, Target(water));

        number.Number = 0.6;
        document.Update();

        Assert.Equal(0.6f, water.Roughness, 3);
    }

    /// <summary>
    ///     ⚠ <b>The read is inside <c>Refreshing</c>.</b> Assigning to a control raises its own
    ///     changed event, so a mixed selection would have the neutral position the control parked at
    ///     written to every object the moment it was drawn.
    /// </summary>
    [Fact]
    public void Showing_a_mixed_selection_does_not_write_one_of_them_over_the_other() {
        var first = new WaterMaterial { Roughness = 0.1f };
        var second = new WaterMaterial { Roughness = 0.9f };

        var slider = document.Root.Add<Slider>();

        slider.Minimum = 0f;
        slider.Maximum = 1f;
        slider.SetAttribute("binding-path", "Roughness");
        document.Update();

        MarkupBinding.Bind(document.Root, Target(first, second));
        document.Update();

        Assert.Equal(0.1f, first.Roughness, 3);
        Assert.Equal(0.9f, second.Roughness, 3);
    }

    /// <summary>
    ///     ⚠ <b>The walk stops at a <c>PropertyField</c>.</b> A drawer builds controls inside the row
    ///     it was given, and some of them are the very controls this binds — a second pass would join
    ///     a colour well's channel to whatever member the enclosing field named.
    /// </summary>
    [Fact]
    public void The_walk_does_not_descend_into_a_row_a_drawer_built() {
        var field = document.Root.Add<PropertyField>();

        field.Path = "Tint";
        document.Update();

        Assert.Equal(1, MarkupBinding.Bind(document.Root, Target(new WaterMaterial())));
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
