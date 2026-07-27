// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

public sealed class QueryTests {
    [Fact]
    public void WithAllMatchesOnlyEntitiesThatHaveEveryComponent() {
        using var world = new World();
        var both = world.Create(new Position(), new Velocity());
        world.Create(new Position());
        world.Create(new Velocity());

        Assert.Equal([both], Collect(world, new QueryDescription().WithAll<Position, Velocity>()));
    }

    [Fact]
    public void WithAnyMatchesEntitiesThatHaveAtLeastOne() {
        using var world = new World();
        var player = world.Create(new Position());
        world.Add<Player>(player);
        var npc = world.Create(new Position());
        world.Add<Npc>(npc);
        world.Create(new Position());

        var found = Collect(world, new QueryDescription().WithAll<Position>().WithAny<Player, Npc>());

        Assert.Equal(2, found.Count);
        Assert.Contains(player, found);
        Assert.Contains(npc, found);
    }

    [Fact]
    public void WithNoneExcludes() {
        using var world = new World();
        var moving = world.Create(new Position());
        var frozen = world.Create(new Position());
        world.Add<Frozen>(frozen);

        Assert.Equal([moving], Collect(world, new QueryDescription().WithAll<Position>().WithNone<Frozen>()));
    }

    [Fact]
    public void AQueryWithNoRequirementsMatchesEverything() {
        using var world = new World();
        world.Create();
        world.Create(new Position());
        world.Create(new Health(1));

        Assert.Equal(3, world.Query(new QueryDescription()).EntityCount);
    }

    [Fact]
    public void AnArchetypeThatAppearsAfterTheFirstIterationIsPickedUp() {
        using var world = new World();
        var description = new QueryDescription().WithAll<Position>();
        var query = world.Query(description);

        world.Create(new Position());
        Assert.Equal(1, query.EntityCount);

        // A component set nobody has used before creates an archetype, which is exactly the moment a
        // cached match list goes stale.
        world.Create(new Position(), new Health(1));
        Assert.Equal(2, query.EntityCount);
    }

    [Fact]
    public void TheSameDescriptionGivesTheSameQuery() {
        using var world = new World();
        var description = new QueryDescription().WithAll<Position>();

        Assert.Same(world.Query(description), world.Query(description));
    }

    [Fact]
    public void EmptyChunksAreNotHandedToTheCaller() {
        using var world = new World();
        var entity = world.Create(new Position());
        world.Destroy(entity);

        // The archetype keeps its chunk so it does not reallocate on the next entity; a system that
        // saw it would have to guard every span access against a zero length.
        Assert.Single(world.ArchetypeOf([ComponentType<Position>.Id]).Chunks);

        var chunks = 0;

        foreach (var _ in world.Chunks(new QueryDescription().WithAll<Position>())) {
            chunks++;
        }

        Assert.Equal(0, chunks);
    }

    // ---------------------------------------------------------------- iteration forms

    [Fact]
    public void TheChunkFormHandsOutContiguousSpans() {
        using var world = new World();

        for (var index = 0; index < 100; index++) {
            world.Create(new Position(index, 0, 0), new Velocity(1, 0, 0));
        }

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position, Velocity>())) {
            var positions = chunk.Values<Position>();
            var velocities = chunk.ReadValues<Velocity>();

            for (var index = 0; index < chunk.Count; index++) {
                positions[index].X += velocities[index].X;
            }
        }

        var total = 0f;

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position>())) {
            foreach (var position in chunk.ReadValues<Position>()) {
                total += position.X;
            }
        }

        // 0..99 plus one each.
        Assert.Equal((99 * 100 / 2) + 100, total);
    }

    [Fact]
    public void TheDelegateFormWritesThroughToTheChunk() {
        using var world = new World();
        var entity = world.Create(new Position(1, 2, 3), new Velocity(10, 20, 30));

        world.Query(
            new QueryDescription().WithAll<Position, Velocity>(),
            static (ref Position position, ref Velocity velocity) => position.X += velocity.X
        );

        Assert.Equal(11, world.Read<Position>(entity).X);
    }

    [Fact]
    public void TheDelegateFormCanSeeTheEntity() {
        using var world = new World();
        var expected = new List<Entity>();

        for (var index = 0; index < 5; index++) {
            expected.Add(world.Create(new Health(index)));
        }

        var seen = new List<Entity>();

        world.QueryWithEntity(
            new QueryDescription().WithAll<Health>(),
            (Entity entity, ref Health health) => {
                seen.Add(entity);
                Assert.Equal(health.Value, expected.IndexOf(entity));
            }
        );

        Assert.Equal(expected.Count, seen.Count);
        Assert.Equal([.. expected.Order()], [.. seen.Order()]);
    }

    [Fact]
    public void TheStructVisitorFormAccumulatesWithoutADelegate() {
        using var world = new World();

        for (var index = 1; index <= 10; index++) {
            world.Create(new Health(index));
        }

        var visitor = default(SumHealth);
        world.ForEach<SumHealth, Health>(new QueryDescription().WithAll<Health>(), ref visitor);

        Assert.Equal(55, visitor.Total);
    }

    [Fact]
    public void HigherAritiesAreThere() {
        using var world = new World();
        var entity = world.Create(new Position(1, 0, 0), new Velocity(2, 0, 0), new Health(3), new Flags(4));

        world.Query(
            new QueryDescription().WithAll<Position, Velocity, Health, Flags>(),
            static (ref Position position, ref Velocity velocity, ref Health health, ref Flags flags) =>
                position.X = velocity.X + health.Value + flags.Value
        );

        Assert.Equal(9, world.Read<Position>(entity).X);
    }

    [Fact]
    public void AskingAChunkForAComponentItsArchetypeLacksSaysHowToFixIt() {
        using var world = new World();
        world.Create(new Position());

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position>())) {
            var failure = Assert.Throws<InvalidOperationException>(() => chunk.Values<Velocity>());
            Assert.Contains("all", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AManagedComponentHasNoSpanAndSaysSo() {
        using var world = new World();
        world.Create(new Label { Text = "x" });

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Label>())) {
            Assert.Throws<InvalidOperationException>(() => chunk.Values<Label>());
        }
    }

    // ---------------------------------------------------------------- change filter

    [Fact]
    public void AChangeFilterSkipsChunksNothingWroteTo() {
        using var world = new World();
        world.Create(new Position(), new Velocity());

        var description = new QueryDescription().WithAll<Velocity>().WithChanged<Position>();

        // The contract a system follows: remember the version you last processed, then let the
        // scheduler move the version on before anything writes again.
        var lastSeen = world.Version;
        world.AdvanceVersion();

        Assert.Equal(0, Count(world, description, lastSeen));

        world.Query(
            new QueryDescription().WithAll<Position>(),
            static (ref Position position) => position.X = 1
        );

        Assert.Equal(1, Count(world, description, lastSeen));
    }

    [Fact]
    public void AReadOnlyPassOverAChunkDoesNotMakeItLookChanged() {
        using var world = new World();
        world.Create(new Position());

        var description = new QueryDescription().WithChanged<Position>();
        var lastSeen = world.Version;
        world.AdvanceVersion();

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position>())) {
            foreach (var position in chunk.ReadValues<Position>()) {
                _ = position.X;
            }
        }

        Assert.Equal(0, Count(world, description, lastSeen));
    }

    /// <summary>
    ///     Filtering on a change to something the entity does not have would match everything, and
    ///     leaving that to the caller to remember produces a query that silently over-matches.
    /// </summary>
    [Fact]
    public void AChangeFilterAlsoRequiresTheComponent() {
        using var world = new World();
        world.Create(new Health(1));

        var description = new QueryDescription().WithChanged<Position>();

        Assert.Equal(0, world.Query(description).EntityCount);
    }

    // ---------------------------------------------------------------- helpers

    static List<Entity> Collect(World world, QueryDescription description) {
        var found = new List<Entity>();

        foreach (var chunk in world.Chunks(description)) {
            found.AddRange(chunk.Entities);
        }

        return found;
    }

    static int Count(World world, QueryDescription description, uint since) {
        var total = 0;

        foreach (var chunk in world.Chunks(description, since)) {
            total += chunk.Count;
        }

        return total;
    }

    struct SumHealth : IForEach<Health> {
        public int Total;

        public void Execute(ref Health health) => Total += health.Value;
    }
}
