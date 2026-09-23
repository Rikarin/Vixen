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
    ///     A cold chain answers completely, bases first, for a leaf nothing has run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1240">#1240</a> and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1336">#1336</a>. Both types are named
    ///         nowhere else, so nothing has warmed either, and <c>AllowDrop</c> is two links up and
    ///         in another assembly — a walk that stopped at the assembly boundary would lose it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This no longer tells the generated chain from the registry's own forcing</b>, and
    ///         that is why the test below it exists: with <c>Of</c> forcing each level again, the
    ///         answer here is complete whether the chain is emitted or not. What this still pins is
    ///         the <em>order</em> — bases first, which every inheriting lookup relies on — and that a
    ///         cold leaf answers at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_untouched_base_s_properties_reach_a_cold_leaf_s_answer() {
        var names = UiPropertyRegistry.Of(typeof(ChainedLeaf)).Select(key => key.Name).ToList();

        Assert.Contains("LeafWeight", names);
        Assert.Contains("BaseWeight", names);
        Assert.Contains("AllowDrop", names);

        // Bases first, which is the order an inheriting lookup relies on.
        Assert.True(names.IndexOf("AllowDrop") < names.IndexOf("BaseWeight"));
        Assert.True(names.IndexOf("BaseWeight") < names.IndexOf("LeafWeight"));
    }

    /// <summary>
    ///     The generated constructor chain, watched through the one read that forces nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1240">#1240</a> put the forcing in the
    ///         generator — each static constructor runs its nearest property-declaring ancestor's by
    ///         <c>typeof</c>, which is the form ILC preserves — and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1336">#1336</a> gave the registry its
    ///         own walk back. The chain is therefore redundant on CoreCLR and is kept for the AOT
    ///         publish nothing here executes (#1255), which leaves it with no test at all unless one
    ///         asks the table directly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, and the first is the one that makes this a real predicate</b>:
    ///         <see cref="LinkedBase" /> is registered by nothing until <see cref="LinkedLeaf" />'s
    ///         constructor runs it, and neither type is named anywhere else in the tree, so an empty
    ///         first read is a fact about the chain rather than about test ordering. A generator that
    ///         stopped emitting the chain leaves the second read empty too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_untouched_base_is_registered_by_its_leaf_s_generated_constructor() {
        Assert.Empty(UiPropertyRegistry.DeclaredBy(typeof(LinkedBase)));

        RuntimeHelpers.RunClassConstructor(typeof(LinkedLeaf).TypeHandle);

        Assert.Contains(
            "LinkedWeight",
            UiPropertyRegistry.DeclaredBy(typeof(LinkedBase)).Select(key => key.Name)
        );
    }

    /// <summary>
    ///     ⚠ The gap #1240 opened and #1336 closed: a type that declares nothing of its own,
    ///     stone cold, answers with its base's properties.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The chain hangs off generated static constructors, and a type that declares no
    ///         <c>[UiProperty]</c> of its own gets no generated file and so no constructor to chain
    ///         from. While <see cref="UiPropertyRegistry.Of" /> forced only the type it was given,
    ///         such a leaf answered with whatever had already run — which for
    ///         <see cref="SilentLeaf" /> is nothing. The registry forces each level again, with an
    ///         annotation the trimmer propagates across <c>Type.BaseType</c>, so the answer is
    ///         complete on both runtimes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first assertion is the instrument, not the subject.</b> It says the base
    ///         really was cold at this instant — <see cref="UiPropertyRegistry.DeclaredBy" /> forces
    ///         nothing and walks nowhere — because a passing second assertion means nothing if some
    ///         earlier test had already put <see cref="SilentBase" /> in play. Nothing else in the
    ///         tree names either type.
    ///     </para>
    ///     <para>
    ///         Removing the <c>RunClassConstructor</c> from <c>UiPropertyRegistry.Collect</c> leaves
    ///         the second assertion red and the first green, which is the shape of the regression
    ///         this pins.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_type_that_declares_nothing_reaches_its_base_while_still_cold() {
        Assert.Empty(UiPropertyRegistry.DeclaredBy(typeof(SilentBase)));

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
///     This is the shape <see cref="UiPropertyRegistry.Of" /> could not answer completely while cold
///     until #1336 gave the walk its forcing back.
/// </summary>
public class SilentLeaf : SilentBase;

/// <summary>
///     A declaring base kept apart from every other pair here, so that the only thing which can ever
///     have registered it is the generated chain the test watches.
/// </summary>
public partial class LinkedBase : UiElement {
    /// <summary>Its only property.</summary>
    [UiProperty(Default = 13)]
    public partial int LinkedWeight { get; set; }
}

/// <summary>The leaf whose generated constructor names <see cref="LinkedBase" />, and nothing else does.</summary>
public partial class LinkedLeaf : LinkedBase {
    /// <summary>Its only property, and the reason it gets a generated constructor at all.</summary>
    [UiProperty(Default = 14)]
    public partial int LinkedLeafWeight { get; set; }
}
