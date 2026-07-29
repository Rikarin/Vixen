// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Replication;

/// <summary>Interest: what each player is told about, and what it costs to work that out.</summary>
public sealed class InterestTests : IDisposable {
    static readonly PlayerId Player = new(1);
    static readonly PlayerId Other = new(2);

    readonly World world = new("interest");
    readonly NetworkIdAllocator ids = new();
    readonly List<Entity> observed = [];

    public void Dispose() => world.Dispose();

    /// <summary>A chain with no rules is what a new project already had.</summary>
    /// <remarks>
    ///     The fallback is <c>Observed</c>, so adding a rule can only ever hide things — which is the
    ///     direction in which mistakes are visible. An object that should not be there gets noticed;
    ///     one that silently is not gets debugged.
    /// </remarks>
    [Fact]
    public void AChainWithNoRulesTellsEverybodyEverything() {
        var chain = new InterestChain();

        Spawn(0f);
        Spawn(1000f);

        chain.Resolve(world, Player, observed);

        Assert.Equal(2, observed.Count);
        Assert.Equal(0, chain.HiddenCount);
    }

    /// <summary>The first rule with an opinion wins, which is what "override" has to mean.</summary>
    /// <remarks>
    ///     An explicit answer placed before the grid is one the grid cannot argue with — a spectator
    ///     seeing a player across the map, a quest marker visible at any range. If the grid could
    ///     overrule it, it would not be an override.
    /// </remarks>
    [Fact]
    public void AnExplicitAnswerBeatsEverythingAfterIt() {
        var explicitly = new ExplicitInterestRule();
        var grid = new InterestGrid { Radius = 10f };
        var chain = new InterestChain { Source = grid, Rules = { explicitly } };

        var far = Spawn(500f);
        var near = Spawn(1f);

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);

        // Without an override, distance decides both.
        chain.Resolve(world, Player, observed);
        Assert.Equal([near], observed);

        // The grid is the source, so an override on something it never emits cannot show it — an
        // override is the last word among the rules, not a way around the candidate set.
        explicitly.Show(Player, world.Read<NetworkId>(far));
        explicitly.Hide(Player, world.Read<NetworkId>(near));

        observed.Clear();
        chain.Resolve(world, Player, observed);

        Assert.Empty(observed);

        explicitly.Clear(Player, world.Read<NetworkId>(near));
        observed.Clear();
        chain.Resolve(world, Player, observed);

        Assert.Equal([near], observed);
    }

    /// <summary>An override belongs to one player.</summary>
    [Fact]
    public void AnOverrideBelongsToOnePlayer() {
        var explicitly = new ExplicitInterestRule();
        var chain = new InterestChain { Rules = { explicitly } };

        var entity = Spawn(0f);
        explicitly.Hide(Player, world.Read<NetworkId>(entity));

        chain.Resolve(world, Player, observed);
        Assert.Empty(observed);

        observed.Clear();
        chain.Resolve(world, Other, observed);
        Assert.Equal([entity], observed);
    }

    /// <summary>The grid answers from the cells near a player, not from the whole world.</summary>
    /// <remarks>
    ///     <para>
    ///         The property the feature exists for, and the reason the grid is a source rather than a
    ///         rule. A rule is asked about everything, so a chain of rules over ten thousand objects
    ///         and two hundred players is two million questions a tick whatever the rules then say.
    ///     </para>
    ///     <para>
    ///         Asserting on <c>ConsideredCount</c> rather than on the result is deliberate: the
    ///         <i>answer</i> would be the same either way, and the answer is not what is under test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheGridConsidersOnlyWhatIsNear() {
        var grid = new InterestGrid { CellSize = 32f, Radius = 40f, Hysteresis = 0f };
        var chain = new InterestChain { Source = grid };

        for (var index = 0; index < 400; index++) {
            Spawn(index * 10f);
        }

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Equal(400, grid.PositionedCount);

        // Five objects within forty units, out of four hundred — and the chain was asked about five.
        Assert.Equal(5, observed.Count);
        Assert.Equal(5, chain.ConsideredCount);
    }

    /// <summary>An object at the boundary does not flicker, because flicker means despawn.</summary>
    /// <remarks>
    ///     Not a polish detail. Leaving the observed set means "drop this object" to a client, so an
    ///     object hovering at the edge is destroyed and recreated on every tick it wavers, together
    ///     with whatever the game hangs off a spawn. The hysteresis band is what a player walking a
    ///     boundary spends their time in.
    /// </remarks>
    [Fact]
    public void AnObjectAtTheBoundaryDoesNotFlicker() {
        var grid = new InterestGrid { CellSize = 32f, Radius = 50f, Hysteresis = 10f };
        var chain = new InterestChain { Source = grid };

        var entity = Spawn(49f);
        grid.SetViewpoint(Player, Vector3.Zero);

        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);
        Assert.Equal([entity], observed);

        // Past the radius but inside the band: it is already being watched, so it stays.
        Move(entity, 55f);
        grid.Rebuild(world);
        observed.Clear();
        chain.Resolve(world, Player, observed);
        Assert.Equal([entity], observed);

        // Past the band, so it goes.
        Move(entity, 65f);
        grid.Rebuild(world);
        observed.Clear();
        chain.Resolve(world, Player, observed);
        Assert.Empty(observed);

        // And coming back needs the inner radius rather than the outer one, or the band would be a
        // one-way door and the flicker would come back on the way in.
        Move(entity, 55f);
        grid.Rebuild(world);
        observed.Clear();
        chain.Resolve(world, Player, observed);
        Assert.Empty(observed);
    }

    /// <summary>Something that is not anywhere is told to everybody.</summary>
    /// <remarks>
    ///     A match timer, a scoreboard, a team's shared state. A distance rule has nothing to say
    ///     about a thing that has no position, and the other reading makes those vanish for reasons
    ///     nobody can see.
    /// </remarks>
    [Fact]
    public void SomethingWithNoPositionGoesToEverybody() {
        var grid = new InterestGrid { Radius = 1f };
        var chain = new InterestChain { Source = grid };

        var scoreboard = world.Create(ids.Next());
        Spawn(500f);

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Equal([scoreboard], observed);
        Assert.Equal(1, grid.UnpositionedCount);
    }

    /// <summary>A player nobody has placed sees everything, and it is counted.</summary>
    /// <remarks>
    ///     They are loading, or spectating, or the game has not wired the viewpoint up. Of the two
    ///     ways to be wrong, showing too much is the one that gets noticed.
    /// </remarks>
    [Fact]
    public void APlayerWithNoViewpointSeesEverythingAndIsCounted() {
        var grid = new InterestGrid { Radius = 1f };
        var chain = new InterestChain { Source = grid };

        Spawn(0f);
        Spawn(900f);

        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Equal(2, observed.Count);
        Assert.Equal(1, grid.ViewpointlessCount);
    }

    /// <summary>A rate sends distant objects less often, and never makes one disappear.</summary>
    /// <remarks>
    ///     <para>
    ///         Doc 16 lists LOD as the fourth resolver in the interest chain. It cannot be one:
    ///         leaving the observed set means "drop this object", so an LOD written as a rule would
    ///         despawn and respawn every distant object on every tick it skipped. This asserts the
    ///         thing that separation buys — the object stays observed throughout.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARateSkipsUpdatesWithoutDroppingTheObject() {
        var rate = new DistanceReplicationRate();
        rate.SetViewpoint(Player, Vector3.Zero);

        var near = Spawn(5f);
        var far = Spawn(150f);

        var sent = 0;
        var skipped = 0;

        for (var tick = 0u; tick < 16; tick++) {
            if (rate.ShouldSend(world, Player, near, world.Read<NetworkId>(near), new(tick))) {
                sent++;
            }

            if (rate.ShouldSend(world, Player, far, world.Read<NetworkId>(far), new(tick))) {
                skipped++;
            }
        }

        Assert.Equal(16, sent);
        Assert.Equal(4, skipped);
        Assert.True(rate.SkippedCount > 0);
    }

    /// <summary>Distant objects are spread across the ticks rather than arriving together.</summary>
    /// <remarks>
    ///     A divider phased by a shared counter would make a snapshot tiny three ticks out of four and
    ///     enormous on the fourth — the same total bandwidth in a shape that defeats the budget and
    ///     the path MTU at once.
    /// </remarks>
    [Fact]
    public void DistantObjectsAreSpreadAcrossTheTicks() {
        var rate = new DistanceReplicationRate();
        rate.SetViewpoint(Player, Vector3.Zero);

        var entities = new List<Entity>();

        for (var index = 0; index < 40; index++) {
            entities.Add(Spawn(150f + index));
        }

        var busiest = 0;

        for (var tick = 0u; tick < 4; tick++) {
            var due = 0;

            foreach (var entity in entities) {
                if (rate.ShouldSend(world, Player, entity, world.Read<NetworkId>(entity), new(tick))) {
                    due++;
                }
            }

            busiest = Math.Max(busiest, due);
        }

        // Forty objects at one tick in four is ten a tick if they are spread, and forty on one tick
        // if they are not.
        Assert.True(busiest <= 15, $"The busiest tick carried {busiest} of 40.");
    }

    Entity Spawn(float x) =>
        world.Create(
            ids.Next(),
            new NetworkTransform { Position = new(x, 0f, 0f), Rotation = Quaternion.Identity }
        );

    void Move(Entity entity, float x) => world.Get<NetworkTransform>(entity).Position = new(x, 0f, 0f);
}
