// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>
    ///     ⚠ <b>And findable <i>on an instance</i>, which is the half the test above cannot see.</b>
    ///     <see cref="UiPropertyRegistry.TryFind(Type, string, out UiPropertyKey)" /> forces the
    ///     class constructor and so proves nothing about the path every binding actually uses:
    ///     <see cref="UiPropertyRegistry.TryFindFor" /> takes an element and, by design, forces
    ///     nothing. Its premise was that constructing an element had already run the initialisers —
    ///     and that was false, because a class whose only static members are field initialisers is
    ///     <c>beforefieldinit</c> and the CLR may defer them until a static field of that exact type
    ///     is read. Making an instance is not that. So <c>bind:Value</c> on a freshly built
    ///     <c>&lt;Slider /&gt;</c> threw "'slider' has no property called 'Value'", or did not,
    ///     depending on what else the application had run first.
    /// </summary>
    /// <remarks>
    ///     The generator now emits an empty static constructor, which is what makes the premise
    ///     true. The second assertion is the one that cannot pass by accident: whether some earlier
    ///     test warmed this type is a matter of ordering, and whether the type is
    ///     <c>beforefieldinit</c> is not.
    /// </remarks>
    [Fact]
    public void A_property_is_findable_on_an_instance_nothing_else_has_touched() {
        Assert.True(UiPropertyRegistry.TryFindFor(new Unvisited(), "Weight", out var key));
        Assert.Equal(7, key.GetValue(new Unvisited()));

        Assert.False(
            typeof(Unvisited).Attributes.HasFlag(TypeAttributes.BeforeFieldInit),
            "a generated property class must declare a static constructor, or its registrations may not have run"
        );
    }

    /// <summary>
    ///     An untouched base's properties reach a derived type's answer through the generated
    ///     constructor chain, not through the registry forcing each level.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1240">#1240</a>. The registry used to
    ///         call <c>RunClassConstructor</c> on every base type on the way down, which is the one
    ///         IL2072 that kept <c>Vixen.Ui</c> and six siblings off the AOT probe — and a true
    ///         positive: a NativeAOT publish answered <c>Of(typeof(Derived))</c> with the derived
    ///         property and nothing from any base, because ILC preserves a class constructor it can
    ///         name and not one reached through <c>Type.BaseType</c>. The registry now forces only
    ///         the type it was given, and each generated static constructor names its nearest
    ///         property-declaring ancestor's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The CoreCLR half is what this can see</b>, and it is the same guarantee: with the
    ///         walk no longer forcing bases, <see cref="ChainedBase" />'s registration is in the
    ///         answer only if <see cref="ChainedLeaf" />'s constructor ran it. Both types are named
    ///         nowhere else, so nothing has warmed either. A generator that stopped chaining leaves
    ///         <c>BaseWeight</c> out and this goes red; so does <c>UiElement</c>'s own
    ///         <c>AllowDrop</c>, which is two links up and in another assembly — the chain has to
    ///         cross metadata, not just source.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_untouched_base_s_properties_arrive_through_the_generated_chain() {
        var names = UiPropertyRegistry.Of(typeof(ChainedLeaf)).Select(key => key.Name).ToList();

        Assert.Contains("LeafWeight", names);
        Assert.Contains("BaseWeight", names);
        Assert.Contains("AllowDrop", names);

        // Bases first, which is the order an inheriting lookup relies on.
        Assert.True(names.IndexOf("AllowDrop") < names.IndexOf("BaseWeight"));
        Assert.True(names.IndexOf("BaseWeight") < names.IndexOf("LeafWeight"));
    }

    /// <summary>
    ///     ⚠ Where the chain does not reach, pinned so it cannot move in either direction without
    ///     somebody deciding to move it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The chain hangs off generated static constructors, and a type that declares no
    ///         <c>[UiProperty]</c> of its own gets no generated file and so no constructor to chain
    ///         from. Asked about by <c>typeof</c> before anything has put its base in play,
    ///         <see cref="UiPropertyRegistry.Of" /> therefore answers without the base's properties.
    ///         That is a <b>narrowing</b> of what the method promised before #1240, and it went in
    ///         with nothing in the tree able to contradict it — the chain test above only covers the
    ///         case where the leaf declares something.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first assertion is red if per-level forcing comes back</b>, which is the
    ///         point of keeping it. Restoring <c>RunClassConstructor</c> inside
    ///         <c>UiPropertyRegistry.Collect</c> re-broadens the answer here and re-introduces the
    ///         <c>IL2072</c> that kept <c>Vixen.Ui</c> and six siblings off the AOT probe — a true
    ///         positive, measured: under ILC that walk returned the derived property and nothing
    ///         else. Nothing but this line can see that regression from a test run; the alternative
    ///         is an ILC warning on a publish, and <c>CheckAot</c> has no CI leg.
    ///     </para>
    ///     <para>
    ///         The second assertion is the guarantee that survives, and it is the one every caller
    ///         in the tree relies on: the moment anything has run the declaring ancestor's
    ///         constructor — constructing an element does, which is what
    ///         <see cref="UiPropertyRegistry.TryFindFor" /> depends on — the same silent leaf answers
    ///         completely. Closing the gap for the cold case means deleting the first assertion and
    ///         widening <c>Of</c>'s summary in the same commit, not weakening this test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_type_that_declares_nothing_reaches_its_base_only_once_something_has_run_it() {
        Assert.DoesNotContain(
            "SilentWeight",
            UiPropertyRegistry.Of(typeof(SilentLeaf)).Select(key => key.Name)
        );

        RuntimeHelpers.RunClassConstructor(typeof(SilentBase).TypeHandle);

        Assert.Contains(
            "SilentWeight",
            UiPropertyRegistry.Of(typeof(SilentLeaf)).Select(key => key.Name)
        );
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

/// <summary>
///     The same, and named apart so that the test above cannot warm it. What is on trial is whether
///     an <i>instance</i> is enough, and a type some other test has already asked about by name
///     would answer yes either way.
/// </summary>
public partial class Unvisited : UiElement {
    /// <summary>Its only property.</summary>
    [UiProperty(Default = 7)]
    public partial int Weight { get; set; }
}

/// <summary>A base that declares a property and is named by nothing but its leaf's generated constructor.</summary>
public partial class ChainedBase : UiElement {
    /// <summary>The base's own.</summary>
    [UiProperty(Default = 3)]
    public partial int BaseWeight { get; set; }
}

/// <summary>The leaf the test asks about, whose generated constructor is the only thing that reaches its base.</summary>
public partial class ChainedLeaf : ChainedBase {
    /// <summary>The leaf's own.</summary>
    [UiProperty(Default = 4)]
    public partial int LeafWeight { get; set; }
}

/// <summary>
///     A declaring base whose only way into the registry is its own generated constructor, which
///     nothing names: its leaf below declares nothing, so no chained call points here.
/// </summary>
public partial class SilentBase : UiElement {
    /// <summary>Its only property, and the one the gap is measured with.</summary>
    [UiProperty(Default = 11)]
    public partial int SilentWeight { get; set; }
}

/// <summary>
///     ⚠ Declares nothing, so the generator emits nothing for it — not even an empty constructor.
///     This is the shape <see cref="UiPropertyRegistry.Of" /> cannot answer completely while cold.
/// </summary>
public class SilentLeaf : SilentBase;
