// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>What the generator wrote, read back off the descriptors it registered.</summary>
/// <remarks>
///     These assertions are the snapshot doc 11's testing table asks for. They are written against
///     the descriptor rather than against generated <i>text</i> on purpose: the text is an
///     implementation of the descriptor, and a test over it fails when the generator's formatting
///     changes, which is a failure that teaches nobody anything.
/// </remarks>
public class DescriptorTests {
    static InspectorDescriptor Water =>
        InspectorRegistry.Find(typeof(WaterMaterial))
        ?? throw new InvalidOperationException("The generator registered no descriptor for WaterMaterial.");

    [Fact]
    public void Referencing_the_assembly_is_enough_for_a_type_to_be_described() {
        // No scan, no AppDomain walk: the module initializer the generator emitted has already run
        // by the time any of this assembly's code does.
        Assert.NotNull(InspectorRegistry.Find(typeof(WaterMaterial)));
        Assert.Null(InspectorRegistry.Find(typeof(string)));
    }

    [Fact]
    public void Members_are_in_declaration_order_except_where_one_asks_otherwise() {
        var names = Water.Members.Select(static member => member.Name).ToArray();

        // "Sea Level" declared Order = -1 and everything else left it at zero, so it comes first and
        // the rest keep the order they were written in.
        Assert.Equal("Level", names[0]);
        Assert.Equal(
            ["Roughness", "Tint", "NormalMap", "Amplitude", "FoamWidth", "DryWidth", "UseFoam"],
            names.Skip(1).Take(7)
        );
    }

    [Fact]
    public void A_member_name_becomes_a_label_a_person_can_read() {
        Assert.Equal("Foam Width", Water.Members.Single(static m => m.Name == "FoamWidth").DisplayName);

        // And an explicit name wins over the derived one.
        Assert.Equal("Sea Level", Water.Members.Single(static m => m.Name == "Level").DisplayName);
    }

    [Theory]
    [InlineData("FoamWidth", "Foam Width")]
    [InlineData("UVScale", "UV Scale")]
    [InlineData("m_hitPoints", "Hit Points")]
    [InlineData("_speed", "Speed")]
    [InlineData("Level2Boss", "Level 2 Boss")]
    [InlineData("X", "X")]
    public void Humanise_breaks_where_a_reader_would(string name, string expected) =>
        Assert.Equal(expected, InspectorMember.Humanise(name));

    [Fact]
    public void Attributes_reach_the_descriptor_as_the_values_they_carried() {
        var roughness = Water.Members.Single(static m => m.Name == "Roughness");
        Assert.Equal(new InspectorRange(0d, 1d, 0.05d, false), roughness.Range);

        var tint = Water.Members.Single(static m => m.Name == "Tint");
        Assert.Equal(new ColorUsage(true, false), tint.Color);

        var normal = Water.Members.Single(static m => m.Name == "NormalMap");
        Assert.Equal(typeof(TextureAsset), normal.AssetType);

        var amplitude = Water.Members.Single(static m => m.Name == "Amplitude");
        Assert.Equal(new CurveUsage(0f, 2f), amplitude.Curve);
        Assert.Equal("Waves", amplitude.Header);

        var notes = Water.Members.Single(static m => m.Name == "Notes");
        Assert.Equal(6, notes.Lines);

        var reflectivity = Water.Members.Single(static m => m.Name == "Reflectivity");
        Assert.Equal("How much of the sky the surface gives back.", reflectivity.Tooltip);
    }

    [Fact]
    public void A_condition_records_which_way_round_it_is() {
        var foam = Water.Members.Single(static m => m.Name == "FoamWidth");
        Assert.Equal("UseFoam", foam.Condition);
        Assert.False(foam.ConditionNegated);

        var dry = Water.Members.Single(static m => m.Name == "DryWidth");
        Assert.Equal("UseFoam", dry.Condition);
        Assert.True(dry.ConditionNegated);
    }

    [Fact]
    public void A_computed_member_is_described_and_is_not_writable() {
        var sharpness = Water.Members.Single(static m => m.Name == "Sharpness");

        Assert.False(sharpness.CanWrite);

        // And a member the inspector is *told* not to write is a different fact from one it cannot.
        var version = Water.Members.Single(static m => m.Name == "Version");
        Assert.True(version.CanWrite);
        Assert.True(version.IsReadOnly);
    }

    [Fact]
    public void A_field_is_reached_by_reference_so_a_struct_member_is_edited_in_place() {
        var flow = (InspectorMember<WaterMaterial, Vector3>) Water.Members.Single(static m => m.Name == "Flow");
        var material = new WaterMaterial();

        // The write goes through the accessor and lands on the object, which is the whole difference
        // from a boxed setter: nothing here re-assigns the whole Vector3 from outside.
        flow.Set(material, new Vector3(0f, 2f, 0f));

        Assert.Equal(new Vector3(0f, 2f, 0f), material.Flow);
        Assert.Equal(new Vector3(0f, 2f, 0f), flow.Get(material));
    }

    [Fact]
    public void A_property_is_reached_through_accessors() {
        var reflectivity =
            (InspectorMember<WaterMaterial, float>) Water.Members.Single(static m => m.Name == "Reflectivity");

        var material = new WaterMaterial();
        reflectivity.Set(material, 0.8f);

        Assert.Equal(0.8f, material.Reflectivity);
    }

    [Fact]
    public void A_derived_type_shows_what_it_inherits_first() {
        var descriptor = InspectorRegistry.Find(typeof(SparkEmitter));

        Assert.NotNull(descriptor);
        Assert.Equal(["Rate", "Spread"], descriptor.Members.Select(static member => member.Name));
    }

    [Fact]
    public void A_type_that_cannot_be_constructed_offers_no_defaults() {
        var descriptor = InspectorRegistry.Find(typeof(Uncreatable));

        Assert.NotNull(descriptor);
        Assert.False(descriptor.CanCreate);
        Assert.False(descriptor.TryGetDefault(descriptor.Members[0], out _));
    }

    [Fact]
    public void A_defaultable_type_answers_with_a_fresh_instance_s_value() {
        var roughness = Water.Members.Single(static m => m.Name == "Roughness");

        Assert.True(Water.TryGetDefault(roughness, out var value));
        Assert.Equal(0.2f, value);
    }

    [Fact]
    public void A_mixed_selection_has_no_common_type() {
        Assert.Equal(typeof(WaterMaterial), InspectorRegistry.CommonType([new WaterMaterial(), new WaterMaterial()]));
        Assert.Null(InspectorRegistry.CommonType([new WaterMaterial(), new Emitter()]));

        // Not "the most derived common base", which would produce a different set of editors
        // depending on what else happened to be selected.
        Assert.Null(InspectorRegistry.CommonType([new SparkEmitter(), new Emitter()]));
    }

    [Fact]
    public void A_member_of_the_wrong_object_is_a_throw_rather_than_a_silent_nothing() {
        var roughness = Water.Members.Single(static m => m.Name == "Roughness");

        Assert.Throws<ArgumentException>(() => roughness.GetBoxed(new Emitter()));
    }
}
