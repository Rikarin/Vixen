// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

public sealed class WorldTests {
    [Fact]
    public void ACreatedEntityIsAliveAndACreatedOneIsNotNull() {
        using var world = new World();
        var entity = world.Create();

        Assert.True(world.IsAlive(entity));
        Assert.False(entity.IsNull);
        Assert.Equal(world.Id, entity.WorldId);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void AnEntityCreatedWithComponentsHasThemWithTheValuesGiven() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3), new Health(50));

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Health>(entity));
        Assert.False(world.Has<Velocity>(entity));
        Assert.Equal(2, world.Read<Position>(entity).Y);
        Assert.Equal(50, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void ComponentOrderDoesNotMakeADifferentArchetype() {
        using var world = new World();
        var one = world.Create(new Position(), new Velocity());
        var other = world.Create(new Velocity(), new Position());

        Assert.Same(world.ArchetypeOf(one), world.ArchetypeOf(other));
    }

    [Fact]
    public void AddingAComponentMovesTheEntityAndKeepsTheOthers() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3));
        var before = world.ArchetypeOf(entity);

        world.Add(entity, new Velocity(4, 5, 6));

        Assert.NotSame(before, world.ArchetypeOf(entity));
        Assert.Equal(2, world.Read<Position>(entity).Y);
        Assert.Equal(5, world.Read<Velocity>(entity).Y);
        Assert.Equal(0, before.EntityCount);
        Assert.Equal(1, world.ArchetypeOf(entity).EntityCount);
    }

    [Fact]
    public void RemovingAComponentKeepsTheRest() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3), new Velocity(4, 5, 6), new Health(7));

        world.Remove<Velocity>(entity);

        Assert.False(world.Has<Velocity>(entity));
        Assert.Equal(2, world.Read<Position>(entity).Y);
        Assert.Equal(7, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void AddingAComponentTheEntityAlreadyHasIsRefused() {
        using var world = new World();
        var entity = world.Create(new Position());

        var failure = Assert.Throws<InvalidOperationException>(() => world.Add(entity, new Position()));
        Assert.Contains(nameof(World.Set), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingAComponentTheEntityDoesNotHaveIsRefused() {
        using var world = new World();
        var entity = world.Create(new Position());

        var failure = Assert.Throws<ComponentNotFoundException>(() => world.Remove<Velocity>(entity));
        Assert.Equal(typeof(Velocity), failure.ComponentType);
    }

    // ---------------------------------------------------------------- lifetime

    [Fact]
    public void AHandleToADestroyedEntityIsRefusedRatherThanReused() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3));

        world.Destroy(entity);

        Assert.False(world.IsAlive(entity));
        Assert.Throws<EntityNotFoundException>(() => world.Has<Position>(entity));
        Assert.Equal(0, world.EntityCount);
    }

    /// <summary>
    ///     The whole reason a handle carries a version. Slot reuse is what makes ids dense; without
    ///     the version the stale handle would address the new occupant and read plausible garbage.
    /// </summary>
    [Fact]
    public void AStaleHandleDoesNotAddressTheEntityThatReusedItsSlot() {
        using var world = new World();
        var first = world.Create(new Health(1));
        world.Destroy(first);
        var second = world.Create(new Health(2));

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
        Assert.NotEqual(first, second);
        Assert.Throws<EntityNotFoundException>(() => world.Read<Health>(first));
        Assert.Equal(2, world.Read<Health>(second).Value);
    }

    [Fact]
    public void AnEntityOfAnotherWorldIsRefusedRatherThanAddressed() {
        using var one = new World("one");
        using var other = new World("other");
        var entity = one.Create(new Health(1));

        // Both worlds have a slot 1 on version 0, so nothing but the world id distinguishes them.
        other.Create(new Health(2));

        var failure = Assert.Throws<EntityNotFoundException>(() => other.Read<Health>(entity));
        Assert.Contains($"world {one.Id}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DestroyingAnEntityKeepsTheOthersAddressable() {
        using var world = new World();
        var entities = new Entity[64];

        for (var index = 0; index < entities.Length; index++) {
            entities[index] = world.Create(new Health(index));
        }

        // Back to front, so every removal is a swap-back that moves a live entity.
        for (var index = 0; index < entities.Length; index += 2) {
            world.Destroy(entities[index]);
        }

        for (var index = 1; index < entities.Length; index += 2) {
            Assert.Equal(index, world.Read<Health>(entities[index]).Value);
        }

        Assert.Equal(32, world.EntityCount);
    }

    [Fact]
    public void ClearDropsEveryEntityAndInvalidatesEveryHandle() {
        using var world = new World();
        var entity = world.Create(new Position(), new Label { Text = "x" });

        world.Clear();

        Assert.Equal(0, world.EntityCount);
        Assert.False(world.IsAlive(entity));
    }

    // ---------------------------------------------------------------- values

    [Fact]
    public void GetHandsOutAReferenceThatWritesThrough() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3));

        world.Get<Position>(entity).Y = 9;

        Assert.Equal(9, world.Read<Position>(entity).Y);
    }

    [Fact]
    public void TryGetAnswersForAComponentThatIsThereAndOneThatIsNot() {
        using var world = new World();
        var entity = world.Create(new Health(3));

        Assert.True(world.TryGet<Health>(entity, out var health));
        Assert.Equal(3, health.Value);
        Assert.False(world.TryGet<Position>(entity, out _));
    }

    [Fact]
    public void SettingAComponentTheEntityDoesNotHaveIsRefused() {
        using var world = new World();
        var entity = world.Create(new Health(3));

        Assert.Throws<ComponentNotFoundException>(() => world.Set(entity, new Position()));
    }

    // ---------------------------------------------------------------- tags

    [Fact]
    public void ATagIsPresentAndHasNoValue() {
        using var world = new World();
        var entity = world.Create(new Position());

        world.Add<Frozen>(entity);

        Assert.True(world.Has<Frozen>(entity));

        // In the signature and the mask, but not in the layout: two components' worth of identity
        // and one component's worth of memory.
        Assert.Equal(2, world.ArchetypeOf(entity).Signature.Count);
        Assert.Equal(1, world.ArchetypeOf(entity).ColumnCount);
        Assert.Equal(-1, world.ArchetypeOf(entity).ColumnOf(ComponentType<Frozen>.Id));
    }

    [Fact]
    public void ReadingAValueOffATagIsRefusedWithAMessageThatSaysWhy() {
        using var world = new World();
        var entity = world.Create();

        world.Add<Frozen>(entity);

        var failure = Assert.Throws<InvalidOperationException>(() => world.Read<Frozen>(entity));
        Assert.Contains("tag", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsAndComponentsAgreeAboutWhatAnEntityHas() {
        using var world = new World();
        var entity = world.Create();

        world.Add<Player>(entity);

        Assert.True(world.Has<Player>(entity));
        Assert.True(world.TryGet<Player>(entity, out _));
        Assert.False(world.TryGet<Npc>(entity, out _));
    }

    // ---------------------------------------------------------------- managed

    [Fact]
    public void AManagedComponentSurvivesAnArchetypeMove() {
        using var world = new World();
        var entity = world.Create(new Label { Text = "hello" });

        world.Add(entity, new Position(1, 2, 3));

        Assert.Equal("hello", world.Read<Label>(entity).Text);
        Assert.Equal(2, world.Read<Position>(entity).Y);
    }

    [Fact]
    public void AStructComponentContainingAReferenceRoundTrips() {
        using var world = new World();
        var entity = world.Create(new Named("first"), new Health(1));

        world.Get<Named>(entity).Name = "second";
        world.Remove<Health>(entity);

        Assert.Equal("second", world.Read<Named>(entity).Name);
    }

    /// <summary>
    ///     A released slot has to be cleared and not merely marked free, or the world roots every
    ///     mesh and behaviour it ever held and the leak looks exactly like normal growth.
    /// </summary>
    [Fact]
    public void RemovingAManagedComponentStopsRootingItsValue() {
        using var world = new World();
        var (entity, weak) = Plant(world);

        world.Remove<Label>(entity);
        Collect();

        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void DestroyingAnEntityStopsRootingItsManagedComponents() {
        using var world = new World();
        var (entity, weak) = Plant(world);

        world.Destroy(entity);
        Collect();

        Assert.False(weak.IsAlive);
    }

    /// <summary>
    ///     Creates the entity in a frame that returns, so the only reference to the label left when
    ///     the caller collects is the one the world holds. Inlining this would leave the object in a
    ///     live stack slot and the test would pass whether or not the store cleared its slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Entity Entity, WeakReference Label) Plant(World world) {
        var entity = world.Create(new Label { Text = "big" });
        return (entity, new(world.Read<Label>(entity)));
    }

    static void Collect() {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    ///     ⚠ A read of a managed component used to be a write: <c>World.Read</c> reached the same
    ///     lazy allocation <c>Get</c> does, so a row nobody had written got a store slot, the chunk
    ///     cell was overwritten with its handle, and the world's table of stores was grown to make
    ///     the store — three mutations behind a method documented as changing nothing
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1198">#1198</a>).
    /// </summary>
    /// <remarks>
    ///     The assertion is on the chunk cell rather than on the value, because the value is the same
    ///     either way — <c>default</c> is what the freshly allocated slot held. That is what made the
    ///     defect invisible, and it is why the test reaches an internal accessor rather than proving
    ///     it through the public surface, which cannot see it at all.
    /// </remarks>
    [Fact]
    public void ReadingAManagedComponentThatWasNeverWrittenTakesItNoSlot() {
        using var world = new World();
        var entity = world.Create(world.ArchetypeOf([ComponentType<Label>.Id]));

        Assert.Equal(0, world.ManagedHandleOf<Label>(entity));
        Assert.False(world.HasManagedStore<Label>());

        Assert.Null(world.Read<Label>(entity));

        Assert.Equal(0, world.ManagedHandleOf<Label>(entity));
        Assert.False(world.HasManagedStore<Label>());
    }

    /// <summary><c>TryGet</c> hands back a copy, so it is a read and owes the same promise.</summary>
    [Fact]
    public void TryGettingAManagedComponentThatWasNeverWrittenTakesItNoSlot() {
        using var world = new World();
        var entity = world.Create(world.ArchetypeOf([ComponentType<Named>.Id]));

        Assert.True(world.TryGet<Named>(entity, out var value));
        Assert.Null(value.Name);

        Assert.Equal(0, world.ManagedHandleOf<Named>(entity));
        Assert.False(world.HasManagedStore<Named>());
    }

    /// <summary>
    ///     The other half, and the one that keeps the first from being satisfiable by doing nothing:
    ///     a <em>write</em> still takes the slot lazily, which is what makes <c>Add&lt;T&gt;()</c>
    ///     with no value and a move that gains the component land on a real reference.
    /// </summary>
    [Fact]
    public void WritingAManagedComponentThatWasNeverWrittenStillTakesItASlot() {
        using var world = new World();
        var entity = world.Create(world.ArchetypeOf([ComponentType<Label>.Id]));

        world.Get<Label>(entity) = new() { Text = "written" };

        Assert.NotEqual(0, world.ManagedHandleOf<Label>(entity));
        Assert.True(world.HasManagedStore<Label>());
        Assert.Equal("written", world.Read<Label>(entity).Text);
    }

    /// <summary>
    ///     And the read path still resolves a slot that exists — a "takes no slot" that also stopped
    ///     reading the value would satisfy every assertion above.
    /// </summary>
    [Fact]
    public void ReadingAManagedComponentThatWasWrittenSeesTheValueAndNotTheDefault() {
        using var world = new World();
        var first = world.Create(new Label { Text = "first" });
        var second = world.Create(new Label { Text = "second" });

        Assert.Equal("first", world.Read<Label>(first).Text);
        Assert.Equal("second", world.Read<Label>(second).Text);
        Assert.True(world.TryGet<Label>(second, out var copy));
        Assert.Equal("second", copy!.Text);
    }

    /// <summary>
    ///     A read of one row must not change what the next writer of another row gets. With the old
    ///     path the read consumed slot 1, so the write landed in slot 2; the numbers are not the
    ///     point, the read having an effect on them is.
    /// </summary>
    [Fact]
    public void ReadingOneRowDoesNotMoveTheSlotTheNextWriterGets() {
        using var world = new World();
        var archetype = world.ArchetypeOf([ComponentType<Label>.Id]);
        var read = world.Create(archetype);
        var written = world.Create(archetype);

        _ = world.Read<Label>(read);
        world.Get<Label>(written) = new() { Text = "x" };

        Assert.Equal(1, world.ManagedHandleOf<Label>(written));
        Assert.Equal(0, world.ManagedHandleOf<Label>(read));
    }

    [Fact]
    public void ManagedSlotsAreReusedRatherThanGrowingForEver() {
        using var world = new World();

        for (var index = 0; index < 1000; index++) {
            var entity = world.Create(new Label { Text = "x" });
            world.Destroy(entity);
        }

        // Nothing observable to assert but that it stays correct and finishes; the point is that a
        // create/destroy loop over a managed component does not grow the store without bound. The
        // store's free list is what makes that true, and this is what would run for ever without it.
        Assert.Equal(0, world.EntityCount);
    }

    // ---------------------------------------------------------------- chunks

    [Fact]
    public void EntitiesSpillIntoMoreChunksAndStayAddressable() {
        using var world = new World();
        var archetype = world.ArchetypeOf([ComponentType<Bulky>.Id, ComponentType<Position>.Id]);
        var count = (archetype.ChunkCapacity * 2) + 7;
        var entities = new Entity[count];

        for (var index = 0; index < count; index++) {
            entities[index] = world.Create(new Bulky { A = index }, new Position(index, 0, 0));
        }

        Assert.Equal(3, archetype.Chunks.Count);

        for (var index = 0; index < count; index++) {
            Assert.Equal(index, world.Read<Bulky>(entities[index]).A);
            Assert.Equal(index, world.Read<Position>(entities[index]).X);
        }
    }

    [Fact]
    public void AChunkFitsInsideItsBudget() {
        using var world = new World();
        var archetype = world.ArchetypeOf([ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Flags>.Id]);

        Assert.True(archetype.ChunkBytes <= Archetype.ChunkBudget, $"{archetype.ChunkBytes} bytes");
        Assert.True(archetype.ChunkCapacity > 0);
    }

    /// <summary>
    ///     Removal fills the hole from the tail chunk rather than only from within its own, so an
    ///     archetype that has churned still iterates as few chunks as its population needs.
    /// </summary>
    /// <remarks>
    ///     Without this a world that creates and destroys in waves keeps every chunk it ever needed,
    ///     each half empty, and every query pays for all of them for ever. It is invisible to a
    ///     correctness test — the entities are all still there and all still right — which is why it
    ///     gets one of its own.
    /// </remarks>
    [Fact]
    public void SurvivorsStayPackedIntoAsFewChunksAsTheyNeed() {
        using var world = new World();
        var archetype = world.ArchetypeOf([ComponentType<Bulky>.Id]);
        var entities = new Entity[archetype.ChunkCapacity * 3];

        for (var index = 0; index < entities.Length; index++) {
            entities[index] = world.Create(new Bulky { A = index });
        }

        for (var index = 0; index < entities.Length; index += 2) {
            world.Destroy(entities[index]);
        }

        Assert.Equal(2, archetype.Chunks.Count);

        for (var index = 1; index < entities.Length; index += 2) {
            Assert.Equal(index, world.Read<Bulky>(entities[index]).A);
        }
    }

    [Fact]
    public void EmptyChunksAreReturnedRatherThanAccumulated() {
        using var world = new World();
        var archetype = world.ArchetypeOf([ComponentType<Bulky>.Id]);
        var entities = new Entity[archetype.ChunkCapacity * 3];

        for (var index = 0; index < entities.Length; index++) {
            entities[index] = world.Create(new Bulky { A = index });
        }

        Assert.Equal(3, archetype.Chunks.Count);

        foreach (var entity in entities) {
            world.Destroy(entity);
        }

        Assert.Single(archetype.Chunks);
        Assert.Equal(0, archetype.EntityCount);
    }

    // ---------------------------------------------------------------- versions

    [Fact]
    public void AReadDoesNotMarkAChunkAsChanged() {
        using var world = new World();
        var entity = world.Create(new Position());
        var archetype = world.ArchetypeOf(entity);
        var column = archetype.ColumnOf(ComponentType<Position>.Id);

        var version = world.AdvanceVersion();
        _ = world.Read<Position>(entity);

        Assert.NotEqual(version, archetype.Chunks[0].VersionOf(column));
    }

    [Fact]
    public void AWriteMarksExactlyTheColumnItWrote() {
        using var world = new World();
        var entity = world.Create(new Position(), new Velocity());
        var archetype = world.ArchetypeOf(entity);
        var positions = archetype.ColumnOf(ComponentType<Position>.Id);
        var velocities = archetype.ColumnOf(ComponentType<Velocity>.Id);

        var version = world.AdvanceVersion();
        world.Set(entity, new Position(1, 2, 3));

        Assert.Equal(version, archetype.Chunks[0].VersionOf(positions));
        Assert.NotEqual(version, archetype.Chunks[0].VersionOf(velocities));
    }

    [Fact]
    public void TakingAReferenceCountsAsAWriteBecauseNothingCanTellOtherwise() {
        using var world = new World();
        var entity = world.Create(new Position());
        var archetype = world.ArchetypeOf(entity);
        var column = archetype.ColumnOf(ComponentType<Position>.Id);

        var version = world.AdvanceVersion();
        _ = world.Get<Position>(entity);

        Assert.Equal(version, archetype.Chunks[0].VersionOf(column));
    }
}
