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

    /// <summary>An explicit answer is the last word, including about something out of range.</summary>
    /// <remarks>
    ///     ⚠ The half that matters is the far one, and it did not work until <c>ExplicitInterestRule</c>
    ///     became an <c>IInterestSource</c> as well (#1042). A rule is only asked about the candidates
    ///     the source produced, so <c>Show</c> on a distant object used to be a call with no effect of
    ///     any kind — which is every example the type's own remarks give: a spectator seeing a player
    ///     across the map, a quest marker visible at any range.
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

        // ⚠ The grid never emits the far one, so this is the case the rule could not do at all
        // until it became a source as well: Show nominates it, and the nomination is what makes the
        // override the last word rather than a veto the source has already exercised.
        explicitly.Show(Player, far, world.Read<NetworkId>(far));
        explicitly.Hide(Player, world.Read<NetworkId>(near));

        observed.Clear();
        chain.Resolve(world, Player, observed);

        Assert.Equal([far], observed);
        Assert.Equal(1, chain.NominatedCount);

        explicitly.Clear(Player, world.Read<NetworkId>(near));
        explicitly.Clear(Player, world.Read<NetworkId>(far));
        observed.Clear();
        chain.Resolve(world, Player, observed);

        Assert.Equal([near], observed);
        Assert.Equal(0, chain.NominatedCount);
    }

    /// <summary>A nomination is not a way to see a slot somebody else is now using.</summary>
    /// <remarks>
    ///     The entity handed to <c>Show</c> is a hint and the id is the key, so the pair is checked
    ///     before anything is nominated. Without that check a destroyed object's override would show
    ///     the player whatever entity had since been given its slot — the one failure a handle-keyed
    ///     override has that an id-keyed one does not, arriving as a player seeing an unrelated object
    ///     across the map.
    /// </remarks>
    [Fact]
    public void ANominationIsRefusedWhenTheHandleNoLongerCarriesTheId() {
        var explicitly = new ExplicitInterestRule();
        var grid = new InterestGrid { Radius = 10f };
        var chain = new InterestChain { Source = grid, Rules = { explicitly } };

        var far = Spawn(500f);
        var id = world.Read<NetworkId>(far);

        explicitly.Show(Player, far, id);
        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);

        chain.Resolve(world, Player, observed);
        Assert.Equal([far], observed);

        // The object goes, and something else takes the slot. The override outlives both, because an
        // id is not reused within a session and nothing told the rule.
        world.Destroy(far);
        var stranger = Spawn(500f);

        grid.Rebuild(world);
        observed.Clear();
        chain.Resolve(world, Player, observed);

        Assert.Empty(observed);
        Assert.DoesNotContain(stranger, observed);
        Assert.Equal(0, chain.NominatedCount);
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

    /// <summary>A flat world does not pay for the empty layers above and below it.</summary>
    /// <remarks>
    ///     <para>
    ///         The window a query walks is a cube and this engine's worlds are a plane. At the cell
    ///         size <c>InterestGrid</c>'s own remarks recommend the span is four, so an unclamped walk
    ///         is 729 probes per connection per tick and — with everything standing on the ground —
    ///         648 of them are in layers nothing is in. That was measured as three to four times the
    ///         slice's cost for thirteen per cent more observed (#1043).
    ///     </para>
    ///     <para>
    ///         Asserted as probes rather than as milliseconds on purpose: the count is the work, and
    ///         it is the same number on an idle machine and a loaded one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFlatWorldIsNotWalkedAsACube() {
        var grid = new InterestGrid { CellSize = 32f, Radius = 96f, Hysteresis = 12f };
        var chain = new InterestChain { Source = grid };

        // Nine cells of occupancy in x and in z, so those two axes clamp to the whole window and the
        // only axis this can be measuring is the empty one.
        for (var x = -128f; x <= 128f; x += 32f) {
            for (var z = -128f; z <= 128f; z += 32f) {
                SpawnAt(new(x, 0f, z));
            }
        }

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Equal(81, grid.PositionedCount);

        // Nine by nine by *one*, because one layer is all that has anything in it.
        Assert.Equal(81, grid.ProbedCellCount);
    }

    /// <summary>A world with floors above it still pays for those floors, and finds them.</summary>
    /// <remarks>
    ///     The clamp is an intersection with what the rebuild filled, not a decision that the third
    ///     dimension does not exist. Both halves are asserted: the object two cells up is observed,
    ///     and the walk grew to the three layers that hold something rather than to the nine the
    ///     window spans.
    /// </remarks>
    [Fact]
    public void AStackedWorldStillReachesTheFloorAboveIt() {
        var grid = new InterestGrid { CellSize = 32f, Radius = 96f, Hysteresis = 12f };
        var chain = new InterestChain { Source = grid };

        var ground = SpawnAt(Vector3.Zero);
        var upstairs = SpawnAt(new(0f, 64f, 0f));

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Equal(2, observed.Count);
        Assert.Contains(ground, observed);
        Assert.Contains(upstairs, observed);

        // One column, three layers: cells y = 0, 1 and 2, of which the middle one is empty and still
        // walked because it is inside the occupied band.
        Assert.Equal(3, grid.ProbedCellCount);
    }

    /// <summary>A world nobody has put anything in is walked not at all.</summary>
    /// <remarks>
    ///     The degenerate end of the same clamp, and the one that would go wrong quietly: with no
    ///     occupancy the bounds are inverted, and a loop whose bounds cross has to run zero times
    ///     rather than wrap around the whole of <c>int</c>.
    /// </remarks>
    [Fact]
    public void AnEmptyWorldIsWalkedNotAtAll() {
        var grid = new InterestGrid { CellSize = 32f, Radius = 96f, Hysteresis = 12f };
        var chain = new InterestChain { Source = grid };

        grid.SetViewpoint(Player, Vector3.Zero);
        grid.Rebuild(world);
        chain.Resolve(world, Player, observed);

        Assert.Empty(observed);
        Assert.Equal(0, grid.ProbedCellCount);
    }

    Entity Spawn(float x) => SpawnAt(new(x, 0f, 0f));

    Entity SpawnAt(Vector3 position) =>
        world.Create(ids.Next(), new NetworkTransform { Position = position, Rotation = Quaternion.Identity });

    void Move(Entity entity, float x) => world.Get<NetworkTransform>(entity).Position = new(x, 0f, 0f);
}
