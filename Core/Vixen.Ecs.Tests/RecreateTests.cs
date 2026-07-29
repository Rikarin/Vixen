// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>Giving a destroyed entity's handle back, which is what undoing a delete needs.</summary>
/// <remarks>
///     ⚠ <b>Half of these exist to prove it <i>refuses</i>.</b> The version counter's promise is that
///     a handle to a destroyed entity never looks live again, and handing one back is the one
///     operation that could break it. What makes it sound is that it is allowed only when nothing has
///     happened to the slot since — so the cases where something did are as much the specification as
///     the case where nothing did.
/// </remarks>
public sealed class RecreateTests {
    [Fact]
    public void The_handle_comes_back_exactly() {
        using var world = new World();
        var entity = world.Create(new Position { X = 1f });

        world.Destroy(entity);

        Assert.True(world.CanRecreate(entity));
        Assert.True(world.TryRecreate(entity, world.ArchetypeOf([ComponentType<Position>.Id])));

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.EntityCount);

        // Zeroed, because this hands back an identity and not a state. Whatever destroyed the entity
        // is what remembers its components — which is the shape an undo command already has.
        Assert.Equal(0f, world.Get<Position>(entity).X);
    }

    [Fact]
    public void A_live_entity_cannot_be_recreated() {
        using var world = new World();
        var entity = world.Create();

        Assert.False(world.CanRecreate(entity));
        Assert.False(world.TryRecreate(entity, world.EmptyArchetype));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void An_entity_that_never_existed_cannot_be_recreated() {
        using var world = new World();

        Assert.False(world.CanRecreate(new Entity(500, 0, world.Id)));
        Assert.False(world.CanRecreate(default));
    }

    [Fact]
    public void Another_worlds_handle_is_refused_rather_than_addressing_this_ones_slot() {
        using var world = new World("first");
        using var other = new World("second");

        var entity = other.Create();
        other.Destroy(entity);

        // Same slot number, different world. Recreating it here would put an entity at a handle that
        // names somebody else's world, which every read of it would then get wrong.
        Assert.False(world.CanRecreate(entity));
        Assert.False(world.TryRecreate(entity, world.EmptyArchetype));
    }

    [Fact]
    public void A_slot_that_was_taken_in_the_meantime_is_refused_for_ever() {
        using var world = new World();
        var entity = world.Create();

        world.Destroy(entity);

        // The free list is last-in-first-out, so one create is enough to take it.
        var thief = world.Create();

        Assert.Equal(entity.Id, thief.Id);
        Assert.False(world.CanRecreate(entity));

        world.Destroy(thief);

        // ⚠ Still refused, and this is the case the whole design turns on. The slot is free again and
        // it would be easy to rewind two versions to reach it — and then the *next* destroy and
        // create would issue `thief`'s handle a second time, to a third entity. A handle that names
        // two entities across its life is exactly what the version counter is for.
        Assert.False(world.CanRecreate(entity));
        Assert.False(world.TryRecreate(entity, world.EmptyArchetype));
    }

    [Fact]
    public void Recreating_does_not_let_a_later_create_reissue_a_handle() {
        using var world = new World();
        var first = world.Create();

        world.Destroy(first);
        Assert.True(world.TryRecreate(first, world.EmptyArchetype));

        world.Destroy(first);
        var next = world.Create();

        // The version moved on from where the recreate put it, so the slot's second occupant is not
        // the first one's handle wearing a different hat.
        Assert.Equal(first.Id, next.Id);
        Assert.NotEqual(first.Version, next.Version);
        Assert.False(world.IsAlive(first));
    }

    [Fact]
    public void Undo_and_redo_can_go_round_as_many_times_as_somebody_presses_the_key() {
        using var world = new World();
        var entity = world.Create(new Position());

        for (var round = 0; round < 10; round++) {
            world.Destroy(entity);
            Assert.True(world.TryRecreate(entity, world.ArchetypeOf([ComponentType<Position>.Id])));
            Assert.True(world.IsAlive(entity));
        }

        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void The_entity_comes_back_into_the_archetype_it_is_asked_for() {
        using var world = new World();
        var entity = world.Create(new Position(), new Velocity());

        world.Destroy(entity);
        Assert.True(world.TryRecreate(entity, world.ArchetypeOf([ComponentType<Position>.Id, ComponentType<Velocity>.Id])));

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.False(world.Has<Health>(entity));
    }

    [Fact]
    public void A_recreated_entity_is_found_by_the_queries_that_match_it() {
        using var world = new World();
        var entity = world.Create(new Position { X = 3f });

        world.Destroy(entity);
        world.TryRecreate(entity, world.ArchetypeOf([ComponentType<Position>.Id]));
        world.Get<Position>(entity).X = 7f;

        // The chunk it was put back into is a real one, and the row is indexed the way any other
        // allocation's is — a handle that reads back but does not iterate would be worse than none.
        List<float> seen = [];

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position>())) {
            foreach (var position in chunk.ReadValues<Position>()) {
                seen.Add(position.X);
            }
        }

        Assert.Equal([7f], seen);
    }

    [Fact]
    public void Recreating_one_of_several_destroyed_slots_leaves_the_others_alone() {
        using var world = new World();
        var first = world.Create();
        var second = world.Create();
        var third = world.Create();

        world.Destroy(first);
        world.Destroy(second);
        world.Destroy(third);

        // The middle one, so the free list has to be closed over rather than popped.
        Assert.True(world.TryRecreate(second, world.EmptyArchetype));

        Assert.True(world.IsAlive(second));
        Assert.True(world.CanRecreate(first));
        Assert.True(world.CanRecreate(third));

        // And the two still on it are still handed out, rather than one of them having been lost
        // when the list closed up.
        var next = world.Create();
        var after = world.Create();

        Assert.Equal([first.Id, third.Id], new[] { next.Id, after.Id }.Order());
        Assert.Equal(3, world.EntityCount);
    }

    [Fact]
    public void An_archetype_from_another_world_is_an_argument_error() {
        using var world = new World();
        using var other = new World();

        var entity = world.Create();
        world.Destroy(entity);

        Assert.Throws<ArgumentException>(() => world.TryRecreate(entity, other.EmptyArchetype));
    }

    [Fact]
    public void A_disposed_world_says_so_rather_than_recreating() {
        var world = new World();
        var entity = world.Create();

        world.Destroy(entity);
        var archetype = world.EmptyArchetype;
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.TryRecreate(entity, archetype));
    }
}
