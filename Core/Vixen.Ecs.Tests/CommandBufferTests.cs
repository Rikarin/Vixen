// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

public sealed class CommandBufferTests {
    [Fact]
    public void NothingHappensUntilPlayback() {
        using var world = new World();
        var buffer = new CommandBuffer(world);

        buffer.Create();
        buffer.Create();

        Assert.Equal(0, world.EntityCount);
        Assert.Equal(2, buffer.Count);

        buffer.Playback();

        Assert.Equal(2, world.EntityCount);
        Assert.Equal(0, buffer.Count);
    }

    /// <summary>
    ///     The case the buffer exists for: a structural change decided while a span over the very
    ///     chunk it would move is being walked.
    /// </summary>
    [Fact]
    public void AStructuralChangeCanBeDecidedDuringIteration() {
        using var world = new World();

        for (var index = 0; index < 200; index++) {
            world.Create(new Health(index));
        }

        var buffer = new CommandBuffer(world);

        world.QueryWithEntity(
            new QueryDescription().WithAll<Health>(),
            (Entity entity, ref Health health) => {
                if (health.Value % 2 == 0) {
                    buffer.Add(entity, new Position(health.Value, 0, 0));
                } else {
                    buffer.Destroy(entity);
                }
            }
        );

        Assert.Equal(200, world.EntityCount);
        buffer.Playback();
        Assert.Equal(100, world.EntityCount);

        var positioned = 0;

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position, Health>())) {
            foreach (var position in chunk.ReadValues<Position>()) {
                Assert.Equal(0, position.X % 2);
                positioned++;
            }
        }

        Assert.Equal(100, positioned);
    }

    // ---------------------------------------------------------------- placeholders

    [Fact]
    public void APlaceholderCanBeGivenComponentsBeforeItExists() {
        using var world = new World();
        var buffer = new CommandBuffer(world);

        var placeholder = buffer.Create();
        buffer.Add(placeholder, new Health(42));
        buffer.Add(placeholder, new Position(1, 2, 3));
        buffer.Add<Frozen>(placeholder);

        Assert.False(world.IsAlive(placeholder));
        buffer.Playback();

        var found = Assert.Single(All(world));
        Assert.Equal(42, world.Read<Health>(found).Value);
        Assert.Equal(2, world.Read<Position>(found).Y);
        Assert.True(world.Has<Frozen>(found));
    }

    /// <summary>
    ///     A placeholder is not a live handle and must never be mistaken for one, whatever it is
    ///     passed to.
    /// </summary>
    [Fact]
    public void APlaceholderIsRefusedByTheWorld() {
        using var world = new World();
        var buffer = new CommandBuffer(world);
        var placeholder = buffer.Create();

        Assert.False(world.IsAlive(placeholder));
        Assert.Throws<EntityNotFoundException>(() => world.Has<Health>(placeholder));
    }

    [Fact]
    public void APlaceholderCanBeDestroyedInTheSameBuffer() {
        using var world = new World();
        var buffer = new CommandBuffer(world);

        var placeholder = buffer.Create();
        buffer.Add(placeholder, new Health(1));
        buffer.Destroy(placeholder);
        buffer.Playback();

        Assert.Equal(0, world.EntityCount);
    }

    // ---------------------------------------------------------------- leniency

    [Fact]
    public void DestroyingTheSameEntityTwiceIsFine() {
        using var world = new World();
        var entity = world.Create(new Health(1));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(entity);
        buffer.Destroy(entity);
        buffer.Playback();

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AddingAComponentTheEntityAlreadyHasOverwritesIt() {
        using var world = new World();
        var entity = world.Create(new Health(1));
        var buffer = new CommandBuffer(world);

        buffer.Add(entity, new Health(2));
        buffer.Playback();

        Assert.Equal(2, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void RemovingAComponentTheEntityDoesNotHaveDoesNothing() {
        using var world = new World();
        var entity = world.Create(new Health(1));
        var buffer = new CommandBuffer(world);

        buffer.Remove<Position>(entity);
        buffer.Playback();

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void CommandsForAnEntityAnEarlierCommandDestroyedAreSkipped() {
        using var world = new World();
        var entity = world.Create(new Health(1));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(entity);
        buffer.Add(entity, new Position());
        buffer.Set(entity, new Health(9));
        buffer.Playback();

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void SettingAComponentTheEntityDoesNotHaveSaysWhereItCameFrom() {
        using var world = new World();
        var entity = world.Create(new Health(1));
        var buffer = new CommandBuffer(world);

        buffer.Set(entity, new Position());

        var failure = Assert.Throws<InvalidOperationException>(buffer.Playback);
        Assert.IsType<ComponentNotFoundException>(failure.InnerException);
        Assert.Contains("Position", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearThrowsAwayWhatWasRecorded() {
        using var world = new World();
        var buffer = new CommandBuffer(world);

        buffer.Create();
        buffer.Clear();
        buffer.Playback();

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AManagedComponentRidesInABuffer() {
        using var world = new World();
        var buffer = new CommandBuffer(world);
        var placeholder = buffer.Create();

        buffer.Add(placeholder, new Label { Text = "carried" });
        buffer.Playback();

        Assert.Equal("carried", world.Read<Label>(Assert.Single(All(world))).Text);
    }

    // ---------------------------------------------------------------- determinism

    /// <summary>
    ///     What [04](../../docs/plan/04-ecs-and-scripting.md) § Tests asks for: parallel playback is
    ///     reproducible across a hundred runs with the work distributed differently each time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the property a fixed-step simulation, a replay and a netcode rollback all
    ///         stand on, and it is not a property the buffer gets for free — the channels fill in
    ///         whatever order the scheduler picks. What makes it hold is that playback sorts by the
    ///         key the caller supplied, so the order is a function of the work rather than of the
    ///         machine.
    ///     </para>
    ///     <para>
    ///         The jitter is deliberate. Without it the thread pool tends to hand out contiguous
    ///         ranges the same way every run, and the test would pass against a buffer that simply
    ///         concatenated its channels.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ParallelPlaybackIsDeterministic() {
        var first = Run(0);

        for (var run = 1; run < 100; run++) {
            Assert.Equal(first, Run(run));
        }
    }

    [Fact]
    public void ASortKeyDecidesTheOrderEntitiesAreCreatedIn() {
        using var world = new World();
        var buffer = new CommandBuffer(world);
        var writer = buffer.AsParallelWriter();

        Parallel.For(0, 256, index => {
                var entity = writer.Create(index);
                writer.Add(index, entity, new Health(index));
            }
        );

        buffer.Playback();

        // Ids are dense and handed out in order, so the entity carrying Health(k) is the (k+1)-th.
        for (var index = 0; index < 256; index++) {
            var entity = All(world).Single(candidate => world.Read<Health>(candidate).Value == index);
            Assert.Equal(index + 1, entity.Id);
        }
    }

    static string Run(int seed) {
        using var world = new World();
        var buffer = new CommandBuffer(world);
        var writer = buffer.AsParallelWriter();

        Parallel.For(0, 300, index => {
                // Vary the interleaving without varying what any item does.
                Thread.SpinWait(((index * 31) + (seed * 17)) % 211);

                var entity = writer.Create(index);
                writer.Add(index, entity, new Health(index));

                if (index % 3 == 0) {
                    writer.Add(index, entity, new Position(index, 0, 0));
                }

                if (index % 5 == 0) {
                    writer.Add<Frozen>(index, entity);
                }
            }
        );

        buffer.Playback();
        return Describe(world);
    }

    static string Describe(World world) {
        var text = new StringBuilder();

        foreach (var entity in All(world).Order()) {
            // Slot and version, not the whole handle: each run builds a fresh world and so gets a
            // fresh world id, which is the one part of the state that is allowed to differ.
            text.Append(entity.Id)
                .Append(':')
                .Append(entity.Version)
                .Append(' ')
                .Append(world.ArchetypeOf(entity).Signature);

            if (world.TryGet<Health>(entity, out var health)) {
                text.Append(" health=").Append(health.Value);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    static List<Entity> All(World world) {
        var entities = new List<Entity>();

        foreach (var chunk in world.Chunks(new QueryDescription())) {
            entities.AddRange(chunk.Entities);
        }

        return entities;
    }
}
