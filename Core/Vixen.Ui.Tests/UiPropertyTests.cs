// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The generated property system, tested by using it.</summary>
public class UiPropertyTests {
    [Fact]
    public void A_property_starts_at_the_default_the_attribute_gave_it() {
        var panel = new Panel();

        Assert.Equal(4f, panel.Radius);
        Assert.Equal("black", panel.Tint);
        Assert.Equal(2, new Card().Elevation);

        // ⚠ An enum default is the case a naive literal writer gets wrong: `Value.ToString()` on a
        // TypedConstant gives the underlying number, which compiles and means something else the
        // moment the enum is reordered.
        Assert.Equal(ElementState.Hover, panel.PreferredState);

        // And no default at all is null rather than a value nobody chose.
        Assert.Null(panel.Label);
    }

    [Fact]
    public void Setting_a_value_runs_coercion_before_anything_else() {
        var panel = new Panel { Radius = 500f };

        Assert.Equal(100f, panel.Radius);

        // Coercion runs before the change test, so clamping to what it already was raises nothing.
        var changes = panel.RadiusChanges;
        panel.Radius = 900f;
        Assert.Equal(changes, panel.RadiusChanges);
    }

    [Fact]
    public void A_change_callback_runs_once_per_actual_change() {
        var panel = new Panel();

        panel.Radius = 10f;
        panel.Radius = 10f;
        panel.Radius = 20f;

        Assert.Equal(2, panel.RadiusChanges);
    }

    [Fact]
    public void A_base_class_hears_about_a_change_without_knowing_the_property() {
        var panel = new Panel();

        panel.Radius = 11f;

        // The per-property callback belongs to the declaring type; this is how a base class reacts
        // to a property its derived types added, which is what invalidation will need.
        Assert.Same(Panel.RadiusProperty, panel.LastChanged);

        panel.Label = "hello";
        Assert.Same(Panel.LabelProperty, panel.LastChanged);
    }

    [Fact]
    public void An_inheriting_property_takes_the_nearest_ancestor_s_value() {
        using var document = new UiDocument(100f, 100f);

        var outer = document.Root.Add<Panel>("panel");
        var middle = outer.Add<Panel>("panel");
        var inner = middle.Add<Panel>("panel");

        outer.Tint = "red";
        Assert.Equal("red", inner.Tint);

        middle.Tint = "green";
        Assert.Equal("green", inner.Tint);

        // Its own value beats any ancestor's.
        inner.Tint = "blue";
        Assert.Equal("blue", inner.Tint);
    }

    [Fact]
    public void Clearing_a_value_goes_back_to_inheriting() {
        using var document = new UiDocument(100f, 100f);

        var outer = document.Root.Add<Panel>("panel");
        var inner = outer.Add<Panel>("panel");

        outer.Tint = "red";
        inner.Tint = "blue";
        Assert.Equal("blue", inner.Tint);

        inner.ClearTint();
        Assert.Equal("red", inner.Tint);

        outer.ClearTint();
        Assert.Equal("black", inner.Tint);
    }

    [Fact]
    public void Inheritance_walks_past_an_ancestor_that_does_not_declare_the_property() {
        using var document = new UiDocument(100f, 100f);

        var outer = document.Root.Add<Panel>("panel");
        var between = outer.Add("div");
        var inner = between.Add<Panel>("panel");

        outer.Tint = "red";

        // The `div` in the middle is a plain UiElement with no Tint at all, and the walk goes past
        // it rather than stopping.
        Assert.Equal("red", inner.Tint);
    }

    [Fact]
    public void A_same_named_property_on_another_type_is_not_the_same_property() {
        using var document = new UiDocument(100f, 100f);

        var overlay = document.Root.Add<Overlay>("overlay");
        var panel = overlay.Add<Panel>("panel");

        overlay.Tint = "red";

        // ⚠ Both types declare `Tint`, and the walk is generated per property with a typed test, so
        // Panel's asks for a Panel ancestor. A dictionary keyed on the name would have found the
        // Overlay's and been confidently wrong.
        Assert.Equal("black", panel.Tint);
        Assert.NotSame(Panel.TintProperty, Overlay.TintProperty);
    }

    [Fact]
    public void Setting_an_inheriting_property_to_what_it_already_shows_is_not_a_change() {
        using var document = new UiDocument(100f, 100f);

        var outer = document.Root.Add<Panel>("panel");
        var inner = outer.Add<Panel>("panel");

        outer.Radius = 9f;
        inner.Radius = 9f;

        // ⚠ The old value has to be read *through the property*, not out of the backing field. The
        // field is still empty on an element that has only ever inherited, so comparing against it
        // reports a change from zero to nine when nothing visibly changed — a spurious invalidation
        // on every element that agrees with its parent.
        Assert.Equal(0, inner.RadiusChanges);
        Assert.Null(inner.LastChanged);
    }

    [Fact]
    public void A_property_can_be_found_and_used_by_name() {
        var panel = new Panel();

        Assert.True(UiPropertyRegistry.TryFind(typeof(Panel), "Radius", out var key));
        Assert.Equal(typeof(float), key.ValueType);
        Assert.Equal(typeof(Panel), key.OwnerType);

        key.SetValue(panel, 12f);

        Assert.Equal(12f, panel.Radius);
        Assert.Equal(12f, key.GetValue(panel));
    }

    [Fact]
    public void A_derived_type_reports_its_own_properties_and_its_base_s() {
        var names = UiPropertyRegistry.Of(typeof(Card)).Select(key => key.Name).ToList();

        Assert.Contains("Elevation", names);
        Assert.Contains("Radius", names);
        Assert.Contains("Tint", names);
    }

    [Fact]
    public void A_property_is_findable_before_anything_has_touched_its_type() {
        // ⚠ The registry is filled by static initialisers, which run on first use of the declaring
        // type. Without forcing that, asking about a type nothing has instantiated would correctly
        // report no properties — a bug that appears only in the build where the order changed.
        Assert.True(UiPropertyRegistry.TryFind(typeof(Untouched), "Weight", out var key));
        Assert.Equal(7, key.GetValue(new Untouched()));
    }

    [Fact]
    public void An_element_that_is_not_in_a_document_says_so_rather_than_pretending() {
        var panel = new Panel();

        // Properties work on a detached element; anything needing the trees does not.
        panel.Radius = 3f;
        Assert.Equal(3f, panel.Radius);
        Assert.Throws<InvalidOperationException>(() => panel.Document);
    }
}

/// <summary>Declared here and mentioned nowhere else, so its class constructor has not run.</summary>
public partial class Untouched : UiElement {
    /// <summary>Its only property.</summary>
    [UiProperty(Default = 7)]
    public partial int Weight { get; set; }
}
