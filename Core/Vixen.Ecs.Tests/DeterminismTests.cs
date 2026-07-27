// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     Two worlds, the same input log, ten thousand steps, the same state.
/// </summary>
/// <remarks>
///     <para>
///         The exit criterion [14](../../../docs/plan/14-roadmap.md) sets for Phase 2, and the
///         property everything later stands on: a replay, a rollback, a desync check and a
///         reproducible bug report are all this one fact.
///     </para>
///     <para>
///         The second world runs its steps through a command buffer with a parallel writer while the
///         first runs them directly, so passing means the two paths agree — not merely that the same
///         code run twice does the same thing, which is a much weaker claim and the one it is easy to
///         accidentally test.
///     </para>
/// </remarks>
public sealed class DeterminismTests {
    const int Steps = 10_000;

    [Fact]
    public void TwoWorldsFedTheSameInputLogEndInTheSameState() {
        var log = InputLog(Steps);

        using var direct = new World("direct");
        using var buffered = new World("buffered");

        var directEntities = new List<Entity>();
        var bufferedEntities = new List<Entity>();
        var commands = new CommandBuffer(buffered);

        for (var step = 0; step < Steps; step++) {
            RunDirect(direct, directEntities, log[step]);
            RunBuffered(buffered, commands, bufferedEntities, log[step]);

            if (step % 1000 == 0) {
                Assert.True(
                    WorldDigest.Agree(direct, buffered),
                    $"The two worlds diverged at step {step}."
                );
            }
        }

        Assert.Equal(WorldDigest.Compute(direct), WorldDigest.Compute(buffered));
        Assert.Equal(direct.EntityCount, buffered.EntityCount);
        Assert.True(direct.EntityCount > 0);
    }

    /// <summary>
    ///     The digest has to be a function of the state and not of how the state was reached, or the
    ///     test above passes for the wrong reason.
    /// </summary>
    [Fact]
    public void TheDigestIgnoresTheOrderThingsHappenedIn() {
        using var forwards = new World();
        using var backwards = new World();

        var forwardEntities = new List<Entity>();
        var backwardEntities = new List<Entity>();

        for (var index = 0; index < 200; index++) {
            forwardEntities.Add(forwards.Create(new Position(index, 0, 0), new Health(index)));
            backwardEntities.Add(backwards.Create(new Position(index, 0, 0), new Health(index)));
        }

        // Same entities removed, opposite orders — so the survivors sit in different chunk rows.
        for (var index = 0; index < 200; index += 3) {
            forwards.Destroy(forwardEntities[index]);
        }

        for (var index = 198; index >= 0; index -= 3) {
            if (index % 3 == 0) {
                backwards.Destroy(backwardEntities[index]);
            }
        }

        Assert.True(WorldDigest.Agree(forwards, backwards));
    }

    [Fact]
    public void TheDigestNoticesADifference() {
        using var left = new World();
        using var right = new World();

        var entity = left.Create(new Position(1, 2, 3));
        right.Create(new Position(1, 2, 3));

        Assert.True(WorldDigest.Agree(left, right));

        left.Get<Position>(entity).Y = 4;

        Assert.False(WorldDigest.Agree(left, right));
    }

    /// <summary>
    ///     How many to destroy, decided from the state before the step rather than from the list as
    ///     it is being mutated — the two harnesses append their spawns at different moments, and a
    ///     count read mid-flight is the one thing that made them disagree.
    /// </summary>
    static int Destroys(int before, Input input) => Math.Clamp(input.Destroy, 0, Math.Max(0, before + input.Spawn - 8));

    /// <summary>One step's worth of decisions, made once and replayed into both worlds.</summary>
    readonly record struct Input(int Spawn, int Destroy, float Delta, bool Freeze);

    static Input[] InputLog(int steps) {
        var state = 0x9E3779B9u;

        uint Next() {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        var log = new Input[steps];

        for (var step = 0; step < steps; step++) {
            log[step] = new(
                (int)(Next() % 4),
                (int)(Next() % 3),
                // A power of two so the arithmetic is exact and a divergence is the ECS's fault
                // rather than the log's.
                0.25f,
                Next() % 8 == 0
            );
        }

        return log;
    }

    static void RunDirect(World world, List<Entity> entities, Input input) {
        // Chosen before anything changes, so both sides pick the same entity however their spawns
        // and destroys are ordered.
        var target = input.Freeze && entities.Count > 0 ? entities[^1] : Entity.Null;
        var spawned = entities.Count;
        var destroys = Destroys(entities.Count, input);

        for (var index = 0; index < input.Spawn; index++) {
            entities.Add(world.Create(new Position(spawned + index, 0, 0), new Velocity(1, 0, 0)));
        }

        for (var index = 0; index < destroys; index++) {
            var victim = entities[0];
            entities.RemoveAt(0);
            world.Destroy(victim);
        }

        if (!target.IsNull && world.IsAlive(target) && !world.Has<Frozen>(target)) {
            world.Add<Frozen>(target);
        }

        var delta = input.Delta;

        world.Query(
            new QueryDescription().WithAll<Position, Velocity>().WithNone<Frozen>(),
            (ref Position position, ref Velocity velocity) => position.X += velocity.X * delta
        );
    }

    static void RunBuffered(World world, CommandBuffer commands, List<Entity> entities, Input input) {
        var writer = commands.AsParallelWriter();
        var target = input.Freeze && entities.Count > 0 ? entities[^1] : Entity.Null;
        var spawned = entities.Count;
        var destroys = Destroys(entities.Count, input);
        var placeholders = new List<Entity>();

        for (var index = 0; index < input.Spawn; index++) {
            var placeholder = writer.Create(index);
            placeholders.Add(placeholder);
            writer.Add(index, placeholder, new Position(spawned + index, 0, 0));
            writer.Add(index, placeholder, new Velocity(1, 0, 0));
        }

        for (var index = 0; index < destroys; index++) {
            writer.Destroy(1000 + index, entities[0]);
            entities.RemoveAt(0);
        }

        if (!target.IsNull && world.IsAlive(target) && !world.Has<Frozen>(target)) {
            writer.Add<Frozen>(2000, target);
        }

        commands.Playback();

        foreach (var placeholder in placeholders) {
            entities.Add(commands.Resolve(placeholder));
        }

        var delta = input.Delta;

        world.Query(
            new QueryDescription().WithAll<Position, Velocity>().WithNone<Frozen>(),
            (ref Position position, ref Velocity velocity) => position.X += velocity.X * delta
        );
    }

}
