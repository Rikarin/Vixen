// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>Which drawer edits what, and what the editors do with a mixed value.</summary>
public class DrawerTests {
    static InspectorDescriptor Water =>
        InspectorRegistry.Find(typeof(WaterMaterial))
        ?? throw new InvalidOperationException("The generator registered no descriptor for WaterMaterial.");

    static InspectorMember Member(string name) => Water.Members.Single(member => member.Name == name);

    [Theory]
    [InlineData("UseFoam", typeof(BooleanDrawer))]
    [InlineData("Roughness", typeof(NumberDrawer))]
    [InlineData("Notes", typeof(MultilineDrawer))]
    [InlineData("Kind", typeof(EnumDrawer))]
    [InlineData("Channels", typeof(EnumDrawer))]
    [InlineData("Flow", typeof(Vector3Drawer))]
    [InlineData("Facing", typeof(QuaternionDrawer))]
    [InlineData("Tint", typeof(ColorDrawer))]
    [InlineData("Amplitude", typeof(CurveDrawer))]
    [InlineData("NormalMap", typeof(AssetDrawer))]
    public void The_built_in_registry_resolves_a_member_to_the_drawer_its_type_and_attributes_ask_for(
        string name,
        Type expected
    ) => Assert.IsType(expected, DrawerRegistry.Default.Resolve(Member(name)));

    [Fact]
    public void An_attribute_beats_the_type_it_is_on() {
        // A string is a text box and a string under [Multiline] is a text area. Both registrations
        // match the member, and the attribute is the more specific statement about it.
        Assert.IsType<MultilineDrawer>(DrawerRegistry.Default.Resolve(Member("Notes")));

        // And with only the type registered, the same member falls back to it — so the line above is
        // the attribute winning rather than the type never having matched.
        var registry = new DrawerRegistry();
        registry.ForType<string>(new StringDrawer());
        Assert.IsType<StringDrawer>(registry.Resolve(Member("Notes")));
    }

    [Fact]
    public void A_registered_drawer_replaces_the_built_in_one_without_removing_it() {
        var registry = DrawerRegistry.CreateDefault();
        var mine = new ReadOnlyDrawer();

        registry.ForType<float>(mine);

        Assert.Same(mine, registry.Resolve(Member("Roughness")));
    }

    [Fact]
    public void A_member_nothing_can_edit_still_gets_a_drawer() {
        // Read-only rather than omitted: a member the inspector cannot edit is still a member
        // somebody needs to see the value of.
        Assert.NotNull(DrawerRegistry.Default.Resolve(Member("Sharpness")));
    }

    [Fact]
    public void A_checkbox_over_objects_that_disagree_is_indeterminate() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var field = new InspectorField(
            Water,
            Member("UseFoam"),
            [new WaterMaterial { UseFoam = true }, new WaterMaterial { UseFoam = false }]
        );

        var drawer = (IPropertyDrawer) new BooleanDrawer();
        var editor = drawer.Build(field, host);

        using (field.Refreshing()) {
            drawer.Show(field, editor);
        }

        var checkbox = Assert.IsType<CheckBox>(editor);

        Assert.True(checkbox.IsIndeterminate);
        Assert.False(checkbox.IsChecked);
    }

    [Fact]
    public void Refreshing_a_mixed_checkbox_does_not_write_its_neutral_state_to_everything() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var first = new WaterMaterial { UseFoam = true };
        var second = new WaterMaterial { UseFoam = false };
        var field = new InspectorField(Water, Member("UseFoam"), [first, second]);

        var drawer = (IPropertyDrawer) new BooleanDrawer();
        var editor = drawer.Build(field, host);

        // Putting a value into a control raises the control's changed event, which calls Write. On a
        // mixed field there is no value to be re-written, so the guard is the only thing between the
        // objects and having the neutral position written to all of them.
        using (field.Refreshing()) {
            drawer.Show(field, editor);
        }

        Assert.True(first.UseFoam);
        Assert.False(second.UseFoam);
    }

    /// <summary>
    ///     ⚠ <b>Two objects whose curves are identical are not a mixed selection, and until this
    ///     landed every one of them was.</b> <c>EditProperty.Read</c> compares with
    ///     <c>Equals(object, object)</c>, which for a type with no equality is reference identity —
    ///     and a member initialised <c>= AnimationCurve.Linear()</c> gives each instance its own
    ///     object. So the row was mixed the moment a second thing was selected, whatever it held.
    /// </summary>
    [Fact]
    public void Two_objects_holding_the_same_curve_are_not_a_mixed_selection() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var first = new WaterMaterial();
        var second = new WaterMaterial();

        // Distinct objects with identical keys, which is what a field initializer produces.
        Assert.NotSame(first.Amplitude, second.Amplitude);
        Assert.True(new InspectorField(Water, Member("Amplitude"), [first, second]).Read().IsMixed);

        var field = new InspectorField(Water, Member("Amplitude"), [first, second]);
        var drawer = (IPropertyDrawer) new CurveDrawer();
        var editor = Assert.IsType<CurveEditor>(drawer.Build(field, host));

        drawer.Show(field, editor);

        Assert.False(editor.HasClass("mixed"));
        Assert.Equal(first.Amplitude.Keys.Count, editor.Curve.Keys.Count);
    }

    /// <summary>
    ///     ⚠ <b>And curves that differ show an <i>empty</i> graph, not one of them.</b> A drawer that
    ///     showed the first object's curve would have the user editing "the" curve and looking at one
    ///     arbitrary object's — the thing <c>EditValue</c>'s own remarks say must never happen.
    /// </summary>
    [Fact]
    public void Curves_that_disagree_show_an_empty_graph_rather_than_one_of_them() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var first = new WaterMaterial();
        var second = new WaterMaterial { Amplitude = AnimationCurve.EaseInOut() };

        var field = new InspectorField(Water, Member("Amplitude"), [first, second]);
        var drawer = (IPropertyDrawer) new CurveDrawer();
        var editor = Assert.IsType<CurveEditor>(drawer.Build(field, host));

        drawer.Show(field, editor);

        Assert.True(editor.HasClass("mixed"));
        Assert.Empty(editor.Curve.Keys);

        // And showing it wrote nothing: the empty graph is a picture of the disagreement, not a value
        // being pushed onto every object.
        Assert.Equal(2, first.Amplitude.Keys.Count);
        Assert.Equal(2, second.Amplitude.Keys.Count);
    }

    /// <summary>
    ///     ⚠ <b>Every object gets its own copy, because twenty objects sharing one curve is an alias
    ///     rather than an agreement.</b> A single <c>Write</c> puts the same instance on all of them,
    ///     and the next key drag on any one of them then moves the curve on all twenty, silently.
    /// </summary>
    [Fact]
    public void An_edit_over_several_objects_gives_each_of_them_its_own_curve() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var first = new WaterMaterial();
        var second = new WaterMaterial { Amplitude = AnimationCurve.EaseInOut() };

        var field = new InspectorField(Water, Member("Amplitude"), [first, second]);
        var drawer = (IPropertyDrawer) new CurveDrawer();
        var editor = Assert.IsType<CurveEditor>(drawer.Build(field, host));

        drawer.Show(field, editor);

        // What the user does with a mixed row: authors a curve in front of themselves.
        editor.Curve.Add(0.25f, 0.5f);

        Assert.NotSame(first.Amplitude, second.Amplitude);
        Assert.Single(first.Amplitude.Keys);
        Assert.Single(second.Amplitude.Keys);

        // The proof that they are not aliases: moving one leaves the other alone.
        first.Amplitude.Move(first.Amplitude.Keys[0], 0.75f, 0.1f);

        Assert.Equal(0.25f, second.Amplitude.Keys[0].Time);
    }

    /// <summary>
    ///     ⚠ <b>Re-showing an unchanged row leaves the curve object alone.</b> <c>Show</c> runs on
    ///     every change a gizmo drag makes, and assigning <c>Curve</c> no-ops only on reference
    ///     equality — so a fresh copy per call swaps the object out from under the control, clearing
    ///     its selection and re-subscribing forty times a second.
    /// </summary>
    [Fact]
    public void Showing_a_row_again_does_not_swap_the_curve_out_from_under_the_editor() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var field = new InspectorField(Water, Member("Amplitude"), [new WaterMaterial()]);
        var drawer = (IPropertyDrawer) new CurveDrawer();
        var editor = Assert.IsType<CurveEditor>(drawer.Build(field, host));

        drawer.Show(field, editor);
        var shown = editor.Curve;

        drawer.Show(field, editor);
        Assert.Same(shown, editor.Curve);
    }

    [Fact]
    public void A_number_with_a_range_is_a_slider_and_one_without_is_a_field() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");
        var drawer = (IPropertyDrawer) new NumberDrawer();

        var bounded = new InspectorField(Water, Member("Roughness"), [new WaterMaterial()]);
        Assert.IsType<Slider>(drawer.Build(bounded, host));

        var unbounded = new InspectorField(Water, Member("FoamWidth"), [new WaterMaterial()]);
        Assert.IsType<NumericInput>(drawer.Build(unbounded, host));
    }

    /// <summary>The gesture, on the control the drawer actually built, against the real object.</summary>
    /// <remarks>
    ///     ⚠ <b>A drag rather than an assertion about <c>Step</c>.</b> The drawer assigned exactly the
    ///     step it meant to; the bug was that a step of one is a thousandth of a percent of a
    ///     directional light's hundred thousand lux, so the scrub was inert on every large unbounded
    ///     member the inspector offers. A test that read the property back would have passed
    ///     throughout, which is why this one goes through <see cref="UiTest.Drag" /> and reads the
    ///     number off the object at the end.
    /// </remarks>
    [Fact]
    public void Dragging_the_field_the_drawer_built_moves_a_large_member_by_a_useful_amount() {
        using var test = UiTest.Create(600f, 400f);
        ControlTheme.Install(test.Document);

        // The row's width, stated rather than inherited from the inspector's own sheet. What this
        // test is about is the arithmetic behind a gesture, and `ThemeTests` is where "the box ends
        // up the right size" belongs — but a box of no width cannot be dragged across at all, so the
        // assertion below keeps the two from being confused for one another.
        test.Load("host { width: 300px; height: 40px; } numeric-input { width: 200px; height: 24px; }");

        var host = test.Create("host");

        // A hundred thousand of something. The member is unbounded and declares no scale, which is
        // the case the fix is about — a lux, a centimetre and a byte all arrive here identical.
        var material = new WaterMaterial { FoamWidth = 100_000f };
        var field = new InspectorField(Water, Member("FoamWidth"), [material]);
        var drawer = (IPropertyDrawer) new NumberDrawer();
        var box = Assert.IsType<NumericInput>(drawer.Build(field, host));

        using (field.Refreshing()) {
            drawer.Show(field, box);
        }

        test.Frame();

        var bounds = box.Bounds;
        Assert.True(bounds.Width > 20f, "the box has no room to be dragged across");

        var x = MathF.Round(bounds.X + (bounds.Width * 0.5f));
        var y = MathF.Round(bounds.Y + (bounds.Height * 0.5f));

        test.Drag(x, y, x + 10f, y, steps: 10);

        // Ten pixels, ten percent. The old arithmetic moved it by ten out of a hundred thousand, and
        // reaching daylight's upper end from its lower one took ninety thousand pixels of screen.
        Assert.Equal(110_000f, material.FoamWidth);
    }

    [Fact]
    public void Typing_into_one_component_of_a_vector_leaves_the_others_alone() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var material = new WaterMaterial { Flow = new Vector3(1f, 2f, 3f) };
        var field = new InspectorField(Water, Member("Flow"), [material]);
        var drawer = (IPropertyDrawer) new Vector3Drawer();
        var editor = drawer.Build(field, host);

        // The Y box, which is the second component group.
        var box = editor.Children[1].Children.OfType<NumericInput>().Single();
        box.Number = 9d;

        Assert.Equal(new Vector3(1f, 9f, 3f), material.Flow);
    }

    [Fact]
    public void A_vector_row_shows_a_number_for_the_components_that_agree_and_a_dash_for_the_rest() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var first = new WaterMaterial { Flow = new Vector3(1f, 2f, 3f) };
        var second = new WaterMaterial { Flow = new Vector3(1f, 7f, 3f) };
        var field = new InspectorField(Water, Member("Flow"), [first, second]);

        var drawer = (IPropertyDrawer) new Vector3Drawer();
        var editor = drawer.Build(field, host);

        using (field.Refreshing()) {
            drawer.Show(field, editor);
        }

        var boxes = editor.Children.Select(group => group.Children.OfType<NumericInput>().Single()).ToArray();

        // Which axis they disagree about is the thing the user wants to know, and blanking the whole
        // row throws it away.
        Assert.Equal("1", boxes[0].Value);
        Assert.Equal(string.Empty, boxes[1].Value);
        Assert.Equal("3", boxes[2].Value);
    }
}

/// <summary>What ticking and unticking an optional member's checkbox writes.</summary>
public class OptionalTests {
    static InspectorDescriptor Graded =>
        InspectorRegistry.Find(typeof(GradedVolume))
        ?? throw new InvalidOperationException("The generator registered no descriptor for GradedVolume.");

    static InspectorMember Member(string name) => Graded.Members.Single(member => member.Name == name);

    [Fact]
    public void Ticking_supplies_the_types_declared_Neutral_rather_than_its_zero() {
        // `default(Grade)` is a saturation of zero — the greyscale trap the Neutral convention
        // exists for. Turning the opinion on must not be the same edit as authoring that trap.
        var volume = Tick("Grading");

        Assert.Equal(Grade.Neutral, volume.Grading);
    }

    [Fact]
    public void Ticking_supplies_a_declared_Default_when_there_is_no_Neutral() {
        var volume = Tick("Shape");

        Assert.Equal(Falloff.Default, volume.Shape);
    }

    [Fact]
    public void A_static_property_of_another_type_is_not_the_convention() {
        // `Mislabeled.Neutral` is a string. Only a property of the type itself counts, so ticking
        // falls back to the zero.
        var volume = Tick("Odd");

        Assert.Equal(default(Mislabeled), volume.Odd);
    }

    [Fact]
    public void Ticking_a_scalar_supplies_its_zero_as_before() {
        var volume = Tick("Exposure");

        Assert.Equal(0f, volume.Exposure);
    }

    [Fact]
    public void Unticking_writes_null_rather_than_any_default() {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var volume = new GradedVolume { Grading = Grade.Neutral };
        var field = new InspectorField(Graded, Member("Grading"), [volume]);
        var drawer = (IPropertyDrawer) new OptionalDrawer(new DrawerRegistry());
        var editor = drawer.Build(field, host);

        using (field.Refreshing()) {
            drawer.Show(field, editor);
        }

        Assert.IsType<OptionalEditor>(editor).Toggle.IsChecked = false;

        Assert.Null(volume.Grading);
    }

    /// <summary>Builds the optional editor over a fresh object and ticks its box.</summary>
    static GradedVolume Tick(string member) {
        using var document = new UiDocument(600f, 400f);
        var host = document.Root.Add("host");

        var volume = new GradedVolume();
        var field = new InspectorField(Graded, Member(member), [volume]);

        // An empty registry: the inner editor is beside the point here, the checkbox is the drawer's.
        var editor = ((IPropertyDrawer) new OptionalDrawer(new DrawerRegistry())).Build(field, host);

        Assert.IsType<OptionalEditor>(editor).Toggle.IsChecked = true;

        return volume;
    }
}

/// <summary>The angles the inspector shows for a rotation it stores as a quaternion.</summary>
public class EulerTests {
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, 45f, 0f)]
    [InlineData(0f, 0f, 60f)]
    [InlineData(15f, -40f, 80f)]
    [InlineData(-70f, 170f, -25f)]
    public void The_angles_rebuild_the_rotation_they_were_read_from(float x, float y, float z) {
        var original = EulerAngles.ToRotation(new Vector3(x, y, z));
        var rebuilt = EulerAngles.ToRotation(EulerAngles.FromRotation(original));

        // The *rotation* round-trips, which is what matters; the numbers need not, because two sets
        // of angles can describe one turn.
        Assert.True(
            Quaternion.SameRotation(original, rebuilt, 1e-4f),
            $"({x}, {y}, {z}) came back as a different rotation"
        );
    }

    [Fact]
    public void At_the_pole_the_whole_turn_goes_into_yaw() {
        var locked = EulerAngles.ToRotation(new Vector3(90f, 30f, 20f));
        var angles = EulerAngles.FromRotation(locked);

        // Yaw and roll turn about the same axis there and only their sum survives. Reporting a roll
        // as well would be inventing a number.
        Assert.Equal(0f, angles.Z, 3);
        Assert.True(Quaternion.SameRotation(locked, EulerAngles.ToRotation(angles), 1e-3f));
    }
}
