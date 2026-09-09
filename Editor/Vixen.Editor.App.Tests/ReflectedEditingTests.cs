// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Reflection;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     A type the serialization generator described and <c>[Inspector]</c> did not, through the
///     editing pipeline rather than through the panel.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The pipeline was strictly narrower than the panel that draws it.</b>
///         <c>ReflectedDescriptor.For</c> answers for any <c>[DataContract]</c> type by building a
///         descriptor out of <c>TypeRegistry</c>; <c>InspectorEditProvider</c> read
///         <c>InspectorRegistry</c> alone. So a settings asset — <c>IEditProvider</c>'s own worked
///         example for why the seam exists — had rows on screen and <b>no members at all</b> through
///         an <c>EditTarget</c>: no <c>EditProperty</c>, no undo through the command stack, no mixed
///         state, no <c>Changed</c> and no markup binding.
///     </para>
///     <para>
///         ⚠ <b>And it was silent in the direction that looks fine.</b> An empty member list is a
///         target with no properties rather than an error — which <c>EmptyEditProvider</c>'s remarks
///         say is deliberate for the two cases it is for — so nothing anywhere reported it.
///     </para>
/// </remarks>
public class ReflectedEditingTests {
    /// <summary>The premise: nothing annotated this type, so the <c>[Inspector]</c> registry has no entry.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted rather than assumed.</b> If somebody annotates <see cref="SettingsFixture" />
    ///     later, every assertion below still passes and stops being about anything — the fallback
    ///     would never be reached and the test would be green about the path it does not exercise.
    /// </remarks>
    [Fact]
    public void The_fixture_is_described_by_the_serializer_and_not_by_the_inspector() {
        Assert.Null(InspectorRegistry.Find(typeof(SettingsFixture)));
        Assert.True(TypeRegistry.TryGet<SettingsFixture>(out _), "the serialization generator never saw it");
    }

    /// <summary>An <c>EditTarget</c> over it has the members the file holds.</summary>
    [Fact]
    public void The_pipeline_finds_the_members_of_a_type_only_the_serializer_described() {
        var target = new EditTarget([new SettingsFixture()], InspectorEditProvider.Default);

        // ⚠ The names, not just the count. A provider that answered with somebody else's descriptor
        // would satisfy "two members" and be wrong about both of them.
        Assert.Equal(
            ["Device", "Volume"],
            target.Members.Select(member => member.Name).Order(StringComparer.Ordinal)
        );
    }

    /// <summary>And a write through it lands on the object.</summary>
    /// <remarks>
    ///     The half that makes the member list worth having: a panel that only listed them would be
    ///     the descriptor over again, and what the pipeline adds is the write.
    /// </remarks>
    [Fact]
    public void A_write_through_the_pipeline_reaches_the_object() {
        var settings = new SettingsFixture();
        var target = new EditTarget([settings], InspectorEditProvider.Default);

        var property = target.Find("Volume")
            ?? throw new InvalidOperationException("the target has no Volume property");

        Assert.True(property.Write(0.75f));
        Assert.Equal(0.75f, settings.Volume);

        // And reading it back goes through the same member rather than through the object.
        Assert.Equal(new EditValue(0.75f, false), property.Read());
    }

    /// <summary>Resolution by name works for the same type, which is what a markup binding uses.</summary>
    [Fact]
    public void A_member_of_such_a_type_resolves_by_name() {
        Assert.True(InspectorEditProvider.Default.TryResolve(typeof(SettingsFixture), "Device", out var member));
        Assert.Equal("Device", member.Name);
    }
}

/// <summary>A settings-shaped object: written to a file, and annotated by nothing else.</summary>
/// <remarks>
///     ⚠ <b>Deliberately carries no <c>[Inspector]</c> anywhere.</b> That is the whole population
///     this test is about — <c>IEditProvider</c>'s "a settings asset is described by
///     <c>Vixen.Core.Reflection</c>" — and one annotated member would put it in
///     <c>InspectorRegistry</c> and take the fallback out of the path.
/// </remarks>
[DataContract]
public sealed class SettingsFixture {
    /// <summary>How loud.</summary>
    public float Volume { get; set; } = 0.5f;

    /// <summary>Which one.</summary>
    public string Device { get; set; } = "Default";
}
