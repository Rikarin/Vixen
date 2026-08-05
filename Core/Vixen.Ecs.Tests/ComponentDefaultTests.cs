// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>That declaring a default changes what asks for one, and changes nothing else.</summary>
/// <remarks>
///     ⚠ <b>Most of these assert that nothing happened.</b> The mechanism's whole claim is that the
///     storage layer keeps its contract — a chunk row is zeroed memory and an add hands one back —
///     and a claim like that is only worth anything if something fails when it stops being true.
/// </remarks>
public sealed class ComponentDefaultTests {
    [Fact]
    public void Adding_a_component_that_declares_a_default_still_hands_back_zeroes() {
        using var world = new World();
        var entity = world.Create();

        world.Add<Shielded>(entity);

        // The documented contract, and the reason `AddDefault` is a second method rather than a
        // branch inside this one: `Add` means "give me the storage", for every component alike.
        Assert.Equal(0f, world.Get<Shielded>(entity).Absorption);
    }

    [Fact]
    public void Asking_for_the_default_by_name_gets_it() {
        using var world = new World();
        var entity = world.Create();

        world.AddDefault<Shielded>(entity);

        Assert.Equal(50f, world.Get<Shielded>(entity).Absorption);
    }

    /// <remarks>
    ///     A component that declared a default and then had it read on a path that also serves the
    ///     components that did not would be the failure this design exists to avoid, so the two are
    ///     asserted side by side in one world.
    /// </remarks>
    [Fact]
    public void A_component_that_declares_nothing_is_untouched() {
        using var world = new World();
        var entity = world.Create();

        world.Add<Health>(entity);
        world.Add<Shielded>(entity);

        Assert.Equal(0, world.Get<Health>(entity).Value);
        Assert.Equal(0f, world.Get<Shielded>(entity).Absorption);
    }

    /// <remarks>
    ///     ⚠ <b>The size is what a saved scene is laid out from.</b> A static member cannot change it
    ///     and this is the assertion that nothing subtler did either — an interface that made a
    ///     component one byte wider would silently invalidate every compiled scene holding one.
    /// </remarks>
    [Fact]
    public void Declaring_a_default_does_not_change_the_layout() {
        Assert.Equal(sizeof(float), ComponentType<Shielded>.Info.Size);
        Assert.False(ComponentType<Shielded>.Info.IsManaged);
        Assert.False(ComponentType<Shielded>.Info.IsTag);
    }

    /// <remarks>
    ///     Recreating hands back an identity and not a state — <c>RecreateTests</c> says so for a
    ///     component with no default, and a declared one must not have quietly made that a special
    ///     case.
    /// </remarks>
    [Fact]
    public void Recreating_an_entity_does_not_consult_a_default() {
        using var world = new World();
        var entity = world.Create<Shielded>(new() { Absorption = 7f });

        world.Destroy(entity);

        Assert.True(world.TryRecreate(entity, world.ArchetypeOf([ComponentType<Shielded>.Id])));
        Assert.Equal(0f, world.Get<Shielded>(entity).Absorption);
    }
}
