// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>What a type says about itself, turned into editors.</summary>
/// <remarks>
///     The descriptors are hand-built rather than generated, and that is the point of doing it this
///     way: <c>Vixen.Core.Reflection.Generator</c> emits exactly this shape, so a grid that works
///     against one written by hand works against one written by the generator — and the test does
///     not need a second assembly compiled at test time to have a type to inspect.
/// </remarks>
[Collection(SharedTypeRegistry.Name)]
public sealed class PropertyGridTests : IDisposable {
    public PropertyGridTests() {
        // ⚠ The registry is process-wide, so every test in this class starts by emptying it. Two
        // descriptors for one type would be a registration conflict, and a descriptor left behind
        // by one test would be found by another that never registered anything.
        TypeRegistry.Clear();
        TypeRegistry.Register(Describe());
    }

    public void Dispose() => TypeRegistry.Clear();

    enum Quality {
        Low,
        Medium,
        High
    }

    sealed class Light {
        public bool Enabled { get; set; } = true;

        public string Name { get; set; } = "Light";

        public float Intensity { get; set; } = 1f;

        public float Range { get; set; } = 0.5f;

        public int Samples { get; set; } = 4;

        public Quality Quality { get; set; } = Quality.Medium;

        public object? Payload { get; set; }
    }

    static TypeDescriptor Describe() =>
        new(
            typeof(Light),
            "Light",
            TypeTraits.DataContract | TypeTraits.EditorVisible,
            [
                Member("Enabled", typeof(bool), light => light.Enabled, (light, value) => light.Enabled = (bool) value!),
                Member("Name", typeof(string), light => light.Name, (light, value) => light.Name = (string) value!),
                Member("Intensity", typeof(float), light => light.Intensity, (light, value) => light.Intensity = (float) value!),
                Member(
                    "Range",
                    typeof(float),
                    light => light.Range,
                    (light, value) => light.Range = (float) value!,
                    new MemberPresentation(Minimum: 0, Maximum: 1, Step: 0.1, IsEditorVisible: true)
                ),
                Member("Samples", typeof(int), light => light.Samples, (light, value) => light.Samples = (int) value!),
                Member("Quality", typeof(Quality), light => light.Quality, (light, value) => light.Quality = (Quality) value!),
                Member("Payload", typeof(object), light => light.Payload, null),
                Member(
                    "Hidden",
                    typeof(int),
                    _ => 0,
                    null,
                    new MemberPresentation(IsEditorVisible: false)
                )
            ],
            () => new Light()
        );

    /// <remarks>
    ///     ⚠ The presentation is spelled out rather than defaulted. <c>MemberPresentation</c> is a
    ///     struct, so <c>default</c> zeroes <c>IsEditorVisible</c> however the parameter above is
    ///     declared — a member handed a defaulted presentation is hidden from the inspector, and it
    ///     is hidden silently. The generator writes every field for the same reason.
    /// </remarks>
    static MemberDescriptor Member(
        string name,
        Type type,
        Func<Light, object?> get,
        Action<Light, object?>? set,
        MemberPresentation? presentation = null
    ) =>
        new(
            name,
            type,
            0,
            instance => get((Light) instance),
            set is null ? null : (instance, value) => set((Light) instance, value),
            presentation ?? new MemberPresentation(IsEditorVisible: true)
        );

    static PropertyGrid Grid(AdvancedFixture fixture, params object[] targets) {
        var grid = fixture.Add<PropertyGrid>();
        grid.Inspect(targets);

        fixture.Update();
        return grid;
    }

    [Fact]
    public void It_builds_an_editor_per_visible_member() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light());

        // Seven declared, one of them not editor-visible.
        Assert.Equal(7, grid.Rows.Count);
        Assert.DoesNotContain(grid.Rows, static row => row.Member?.Name == "Hidden");

        Assert.IsType<CheckBox>(grid.Rows[0].Editor.Children[0]);
        Assert.IsType<TextBox>(grid.Rows[1].Editor.Children[0]);
        Assert.IsType<NumericInput>(grid.Rows[2].Editor.Children[0]);

        // ⚠ A bounded number is a different thing to edit from an unbounded one, so a declared range
        // makes it a slider rather than a field with limits.
        Assert.IsType<Slider>(grid.Rows[3].Editor.Children[0]);
        Assert.IsType<Select>(grid.Rows[5].Editor.Children[0]);

        // Nothing knows how to edit an `object`, so it is shown rather than omitted.
        Assert.IsType<TextBlock>(grid.Rows[6].Editor.Children[0]);
    }

    /// <summary>An inspector's editors are named by their rows, and its reset buttons say what they reset.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole point of a per-row relation, and it is the case that makes an accessible
    ///     inspector different from an inaccessible one.</b> A <c>TextBox</c>, a
    ///     <c>NumericInput</c>, a <c>Slider</c> and a <c>Select</c> all deliberately answer
    ///     <c>null</c> to <c>NativeAccessibleName</c> — a placeholder is a hint and a number is not
    ///     a name — so an inspector of seven members was seven unnamed fields beside a column of
    ///     text that nothing connected them to. And every reset button says the same word, so
    ///     walking them announced "Reset" seven times: the name stays the verb and
    ///     <c>DescribedBy</c> says which member it acts on.
    /// </remarks>
    [Fact]
    public void Every_editor_is_named_by_its_row_and_every_reset_button_says_what_it_resets() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light());

        Assert.Equal("Enabled", grid.Rows[0].Editor.Children[0].AccessibleName);
        Assert.Equal("Name", grid.Rows[1].Editor.Children[0].AccessibleName);
        Assert.Equal("Intensity", grid.Rows[2].Editor.Children[0].AccessibleName);
        Assert.Equal("Range", grid.Rows[3].Editor.Children[0].AccessibleName);
        Assert.Equal("Quality", grid.Rows[5].Editor.Children[0].AccessibleName);

        Assert.Equal(ControlStrings.PropertyGridReset.Source, grid.Rows[0].Reset.AccessibleName);
        Assert.Equal("Enabled", grid.Rows[0].Reset.AccessibleDescription);
        Assert.Equal("Range", grid.Rows[3].Reset.AccessibleDescription);

        // ⚠ First, and it is the assertion that cannot pass vacuously: an inspector with no editors
        // in it satisfies every line above.
        Assert.Empty(AccessibilitySnapshot.Unnamed(grid));
    }

    [Fact]
    public void The_editors_start_at_the_objects_values() {
        using var fixture = new AdvancedFixture();

        var light = new Light { Enabled = false, Name = "Key", Intensity = 2.5f, Quality = Quality.High };
        var grid = Grid(fixture, light);

        Assert.False(((CheckBox) grid.Rows[0].Editor.Children[0]).IsChecked);
        Assert.Equal("Key", ((TextBox) grid.Rows[1].Editor.Children[0]).Value);
        Assert.Equal("2.5", ((NumericInput) grid.Rows[2].Editor.Children[0]).Value);
        Assert.Equal("High", ((Select) grid.Rows[5].Editor.Children[0]).Value);
    }

    [Fact]
    public void An_edit_writes_the_object() {
        using var fixture = new AdvancedFixture();

        var light = new Light();
        var grid = Grid(fixture, light);

        var changes = 0;
        grid.ValueChanged += (_, _) => changes++;

        var checkbox = (CheckBox) grid.Rows[0].Editor.Children[0];
        fixture.Click(checkbox);

        Assert.False(light.Enabled);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void An_edit_writes_every_object_that_is_selected() {
        using var fixture = new AdvancedFixture();

        var first = new Light();
        var second = new Light();
        var grid = Grid(fixture, first, second);

        var numeric = (NumericInput) grid.Rows[2].Editor.Children[0];
        numeric.Number = 7d;

        // Selecting twenty objects and setting one field on all of them is the operation an
        // inspector exists for; showing the first one's values and editing only that is the bug.
        Assert.Equal(7f, first.Intensity, 0.001f);
        Assert.Equal(7f, second.Intensity, 0.001f);
    }

    [Fact]
    public void Where_the_objects_disagree_the_editor_says_so() {
        using var fixture = new AdvancedFixture();

        var first = new Light { Enabled = true, Name = "A", Intensity = 1f, Quality = Quality.Low };
        var second = new Light { Enabled = false, Name = "B", Intensity = 2f, Quality = Quality.High };

        var grid = Grid(fixture, first, second);

        // ⚠ Not an average and not the first one's value. Twenty objects with three different
        // values must not show one of them as though it were the answer.
        Assert.True(((CheckBox) grid.Rows[0].Editor.Children[0]).IsIndeterminate);
        Assert.Equal(string.Empty, ((TextBox) grid.Rows[1].Editor.Children[0]).Value);
        Assert.Equal("—", ((TextBox) grid.Rows[1].Editor.Children[0]).Placeholder);
        Assert.Null(((Select) grid.Rows[5].Editor.Children[0]).Value);
    }

    [Fact]
    public void Objects_of_different_types_show_nothing() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light(), "a string");

        Assert.Null(grid.Descriptor);
        Assert.Empty(grid.Rows);
    }

    [Fact]
    public void A_member_that_cannot_be_written_is_not_editable() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light());

        var row = grid.Rows.Single(static row => row.Member?.Name == "Payload");

        // No setter, so no editor and no reset button — but the value is still shown.
        Assert.IsType<TextBlock>(row.Editor.Children[0]);
        Assert.True(row.Reset.HasClass("hidden"));
    }

    [Fact]
    public void The_reset_button_appears_only_when_the_value_is_not_the_default() {
        using var fixture = new AdvancedFixture();

        var light = new Light();
        var grid = Grid(fixture, light);

        var row = grid.Rows[2];
        Assert.True(row.Reset.HasClass("hidden"));

        ((NumericInput) row.Editor.Children[0]).Number = 5d;
        Assert.False(row.Reset.HasClass("hidden"));

        // ⚠ A pass before the click. The button was hidden a moment ago, so it has no box yet — and
        // a click at the centre of a zero-sized rectangle lands on whatever is behind it.
        fixture.Update();
        fixture.Click(row.Reset);

        Assert.Equal(1f, light.Intensity, 0.001f);
        Assert.True(row.Reset.HasClass("hidden"));
    }

    [Fact]
    public void A_number_reaches_the_member_as_its_own_type() {
        using var fixture = new AdvancedFixture();

        var light = new Light();
        var grid = Grid(fixture, light);

        var samples = (NumericInput) grid.Rows[4].Editor.Children[0];
        samples.Number = 9d;

        // ⚠ Every numeric editor works in double. A member declared `int` that was handed one would
        // throw inside the generated setter's cast, which is a crash rather than a wrong number.
        Assert.Equal(9, light.Samples);
    }

    [Fact]
    public void An_enum_becomes_a_list_of_its_members() {
        using var fixture = new AdvancedFixture();

        var light = new Light();
        var grid = Grid(fixture, light);

        var select = (Select) grid.Rows[5].Editor.Children[0];
        Assert.Equal(["Low", "Medium", "High"], select.Options.Select(static option => option.Value));

        select.Value = "Low";
        Assert.Equal(Quality.Low, light.Quality);
    }

    [Fact]
    public void The_search_box_hides_the_rows_that_do_not_match() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light());

        grid.Search.Value = "int";

        Assert.Contains(grid.Rows, static row => !row.HasClass("filtered") && row.Member?.Name == "Intensity");
        Assert.All(
            grid.Rows.Where(static row => row.Member?.Name != "Intensity"),
            static row => Assert.True(row.HasClass("filtered"))
        );

        grid.Search.Value = string.Empty;
        Assert.All(grid.Rows, static row => Assert.False(row.HasClass("filtered")));
    }

    [Fact]
    public void Reloading_reads_the_objects_without_rebuilding_the_editors() {
        using var fixture = new AdvancedFixture();

        var light = new Light();
        var grid = Grid(fixture, light);

        var editor = grid.Rows[1].Editor.Children[0];
        light.Name = "moved by a gizmo";

        grid.Reload();

        // Same element — rebuilding would take the focus out of whatever the user was typing into,
        // which for a gizmo dragging an object is every frame.
        Assert.Same(editor, grid.Rows[1].Editor.Children[0]);
        Assert.Equal("moved by a gizmo", ((TextBox) editor).Value);
    }

    [Fact]
    public void Inspecting_nothing_clears_it() {
        using var fixture = new AdvancedFixture();
        var grid = Grid(fixture, new Light());

        Assert.NotEmpty(grid.Rows);

        grid.Clear();

        Assert.Empty(grid.Rows);
        Assert.Empty(grid.Body.Children);
    }
}
