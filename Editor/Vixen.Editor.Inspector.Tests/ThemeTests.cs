// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>An entity as the editor puts one in front of the inspector: a name, then a transform.</summary>
/// <remarks>
///     The shape that matters here rather than a copy of <c>SceneEntity</c> — one ungrouped member
///     followed by a <c>[Header]</c> and three vectors — because it is the shape the alignment bugs
///     were about, and the application's own type lives in an assembly this one cannot reference.
/// </remarks>
public sealed class InspectedEntity {
    /// <summary>What it is called.</summary>
    [Inspector]
    public string Name { get; set; } = "Crate";

    /// <summary>Where it is, and the member that starts the transform section.</summary>
    [Inspector]
    [Header("Transform")]
    public Vector3 Position { get; set; }

    /// <summary>How it is turned.</summary>
    [Inspector]
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>How big it is.</summary>
    [Inspector]
    public Vector3 Scale { get; set; } = Vector3.One;
}

/// <summary>What <see cref="InspectorTheme" /> has to be loaded for, asserted on the laid-out tree.</summary>
/// <remarks>
///     ⚠ <b>Every one of these fails against an unstyled document rather than merely looking
///     different.</b> CSS's initial <c>flex-direction</c> is <c>row</c>, so an inspector nothing
///     styles puts the search box beside the fields and every member beside the one before it; a
///     field's background is the panel's own colour, so the box is a border around nothing. Those
///     are the three things this file is about, and none of them is visible to a test that only
///     asks which rows were built.
/// </remarks>
public class ThemeTests {
    [Fact]
    public void Members_are_a_column_and_their_labels_share_one_edge() {
        using var fixture = new InspectorFixture();

        var labels = fixture.Labels();
        Assert.Equal(4, labels.Count);

        // ⚠ The section's rows and the ungrouped one above it, on the same left edge. The control
        // set indents `expander-content` by twenty pixels for prose, which put "Name" and "Position"
        // in two different columns of one panel until the inspector's own sheet said otherwise.
        foreach (var label in labels) {
            Assert.Equal(labels[0].Bounds.X, label.Bounds.X, 0.5f);
        }

        // And they descend, which is the other half of it: nothing here is beside anything.
        for (var index = 1; index < labels.Count; index++) {
            Assert.True(
                labels[index].Bounds.Y > labels[index - 1].Bounds.Y,
                $"'{labels[index].Text}' is not below '{labels[index - 1].Text}'"
            );
        }
    }

    [Fact]
    public void An_editor_is_right_of_its_label_and_on_its_line() {
        using var fixture = new InspectorFixture();

        foreach (var row in fixture.Inspector.Rows) {
            var label = row.Label.Bounds;
            var slot = row.Slot.Bounds;

            Assert.True(slot.X >= label.X + label.Width, $"'{row.Label.Text}' overlaps its editor");
            Assert.True(slot.Width > 0f, $"'{row.Label.Text}' has no room for its editor");

            // Centres within a pixel of each other: the two are on one line rather than stacked,
            // which is what a row laid out as a column would look like.
            Assert.Equal(label.Y + (label.Height * 0.5f), slot.Y + (slot.Height * 0.5f), 1f);
        }
    }

    [Fact]
    public void The_three_axes_of_a_vector_are_equal_columns_in_order() {
        using var fixture = new InspectorFixture();

        var components = fixture.Components("Position");
        Assert.Equal(3, components.Count);

        // ⚠ Equal widths, which is what `flex-basis: 0px` buys. Sized by their content instead, a
        // box holding "-12.25" is wider than one holding "0" — so X moves as the number in it
        // changes, and the three boxes of Position do not line up with the three of Scale.
        //
        // Within a pixel rather than exactly: three columns out of a width that does not divide by
        // three are rounded to the pixel grid, and asking for less than that would be asking the
        // layout not to snap.
        foreach (var component in components) {
            Assert.Equal(components[0].Bounds.Width, component.Bounds.Width, 1.01f);
            Assert.True(component.Bounds.Width > 0f, "an axis has no width");
        }

        for (var index = 1; index < components.Count; index++) {
            Assert.True(
                components[index].Bounds.X >= components[index - 1].Bounds.X + components[index - 1].Bounds.Width,
                "the axes overlap"
            );

            Assert.Equal(components[0].Bounds.Y, components[index].Bounds.Y, 0.5f);
        }
    }

    [Fact]
    public void Position_and_scale_line_their_axes_up_with_each_other() {
        using var fixture = new InspectorFixture();

        var position = fixture.Components("Position");
        var scale = fixture.Components("Scale");

        for (var index = 0; index < position.Count; index++) {
            Assert.Equal(position[index].Bounds.X, scale[index].Bounds.X, 0.5f);
            Assert.Equal(position[index].Bounds.Width, scale[index].Bounds.Width, 0.5f);
        }
    }

    [Fact]
    public void A_field_is_not_the_panel_it_sits_in() {
        using var fixture = new InspectorFixture();

        var panel = fixture.Test.ColorOf(fixture.Panel, "background-color");
        Assert.NotNull(panel);

        // ⚠ The bug this is here for. The control set gives a text box `--surface`, and a docked
        // group is `--surface` too — so every box in the inspector was drawn in exactly the colour
        // behind it and the only thing on screen was its border.
        foreach (var field in fixture.Fields()) {
            var background = fixture.Test.ColorOf(field, "background-color");

            Assert.NotNull(background);
            Assert.NotEqual(panel.Value, background.Value);
        }
    }

    /// <summary>An inspector inside a docked panel, with the three sheets an editor loads.</summary>
    sealed class InspectorFixture : IDisposable {
        public InspectorFixture() {
            Test = UiTest.Create(420f, 600f);

            ControlTheme.Install(Test.Document);
            AdvancedTheme.Install(Test.Document);
            InspectorTheme.Install(Test.Document);

            // The panel is real rather than assumed: "a field is invisible" is a statement about
            // two colours, and one of them belongs to the thing the inspector is docked in.
            Panel = Test.Create("dock-group");

            Inspector = Panel.Add<InspectorView>();
            Inspector.Inspect(new InspectedEntity());

            Test.Frame();
        }

        public UiTest Test { get; }

        public UiElement Panel { get; }

        public InspectorView Inspector { get; }

        public IReadOnlyList<UiElement> Labels() => [.. Inspector.Rows.Select(row => row.Label)];

        /// <summary>The axis groups of one vector row.</summary>
        public IReadOnlyList<UiElement> Components(string member) => [.. Row(member).Editor.Children];

        /// <summary>Every control in the inspector that is typed into.</summary>
        public IReadOnlyList<UiElement> Fields() {
            List<UiElement> fields = [];
            Collect(Inspector, fields);

            return fields;
        }

        public void Dispose() => Test.Dispose();

        InspectorRow Row(string member) =>
            Inspector.Rows.Single(row => string.Equals(row.Field.Member.Name, member, StringComparison.Ordinal));

        static void Collect(UiElement element, List<UiElement> into) {
            foreach (var child in element.Children) {
                if (child is TextBox or NumericInput) {
                    into.Add(child);
                }

                Collect(child, into);
            }
        }
    }
}
