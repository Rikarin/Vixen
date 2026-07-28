// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Physics.Tests;

public sealed class PhysicsLayerTests {
    [Fact]
    public void EverythingCollidesWithEverythingUntilToldOtherwise() {
        var layers = PhysicsLayers.Define().Add("A").Add("B").Add("C").Build();

        Assert.Equal(3, layers.Count);

        for (byte first = 0; first < 3; first++) {
            for (byte second = 0; second < 3; second++) {
                Assert.True(layers.Collide(new(first), new(second)));
            }
        }
    }

    /// <summary>
    ///     Jolt asks "may A collide with B" in whichever order the broad phase produced the pair, so
    ///     a matrix that answered differently each way would give a collision that depends on body
    ///     creation order.
    /// </summary>
    [Fact]
    public void SeparatingTwoLayersIsSymmetricAndSoIsJoiningThemAgain() {
        var layers = PhysicsLayers.Define().Add("A").Add("B").Separate("A", "B").Build();

        Assert.False(layers.Collide(new(0), new(1)));
        Assert.False(layers.Collide(new(1), new(0)));
        Assert.True(layers.Collide(new(0), new(0)));

        var rejoined = PhysicsLayers.Define().Add("A").Add("B").Separate("A", "B").Join("A", "B").Build();

        Assert.True(rejoined.Collide(new(0), new(1)));
        Assert.True(rejoined.Collide(new(1), new(0)));
    }

    [Fact]
    public void ALayerCanBeStoppedFromCollidingWithItself() {
        var layers = PhysicsLayers.Define().Add("Ghosts").Separate("Ghosts", "Ghosts").Build();

        Assert.False(layers.Collide(new(0), new(0)));
        Assert.Equal(PhysicsLayerMask.None, layers.CollidesWith(new(0)));
    }

    [Fact]
    public void LayersAreFoundByNameAndDescribeTheirBroadPhase() {
        var layers = PhysicsLayers.Define()
            .Add("Level", PhysicsBroadPhase.Static)
            .Add("Props")
            .Build();

        Assert.True(layers.TryFind("Level", out var level));
        Assert.True(layers.TryFind("Props", out var props));
        Assert.False(layers.TryFind("Nothing", out _));

        Assert.Equal("Level", layers.NameOf(level));
        Assert.Equal(PhysicsBroadPhase.Static, layers.BroadPhaseOf(level));
        Assert.Equal(PhysicsBroadPhase.Moving, layers.BroadPhaseOf(props));
    }

    [Fact]
    public void ABuilderRefusesADuplicateNameAnEmptyTableAndAnUnknownLayer() {
        Assert.Throws<ArgumentException>(() => PhysicsLayers.Define().Add("A").Add("A"));
        Assert.Throws<InvalidOperationException>(() => PhysicsLayers.Define().Build());
        Assert.Throws<ArgumentException>(() => PhysicsLayers.Define().Add("A").Separate("A", "B"));
    }

    [Fact]
    public void ALayerOutsideTheTableIsRefusedRatherThanReadPastTheEnd() {
        var layers = PhysicsLayers.Define().Add("Only").Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => layers.CollidesWith(new(7)));
    }

    /// <summary>
    ///     The default is two layers rather than one, because a world with a single broad-phase layer
    ///     puts the level geometry in the same tree as the crates and pays for it on every step.
    /// </summary>
    [Fact]
    public void TheDefaultTableSplitsStaticFromMoving() {
        Assert.Equal(2, PhysicsLayers.Default.Count);
        Assert.Equal(PhysicsBroadPhase.Static, PhysicsLayers.Default.BroadPhaseOf(PhysicsLayer.Default));
        Assert.Equal(PhysicsBroadPhase.Moving, PhysicsLayers.Default.BroadPhaseOf(new(1)));
        Assert.True(PhysicsLayers.Default.Collide(new(0), new(1)));
    }

    [Fact]
    public void MasksCombineTheWayASetShould() {
        var first = new PhysicsLayer(0).AsMask;
        var second = new PhysicsLayer(3).AsMask;
        var both = first | second;

        Assert.True(both.Contains(new(0)));
        Assert.True(both.Contains(new(3)));
        Assert.False(both.Contains(new(1)));

        Assert.Equal(first, both.Without(new(3)));
        Assert.Equal(both, first.With(new(3)));
        Assert.Equal(PhysicsLayerMask.None, both & new PhysicsLayer(1).AsMask);
        Assert.False((~PhysicsLayerMask.All).Contains(new(0)));
    }

    [Fact]
    public void ABodyOnALayerTheWorldDoesNotDeclareIsRefused() {
        using var world = new PhysicsWorld(new() { Layers = PhysicsLayers.Define().Add("Only").Build() });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => world.CreateBody(
                Bodies.BodyDescription.Static(world.Shapes.Box(0.5f), Core.Mathematics.Vector3.Zero) with {
                    Layer = new(4)
                }
            )
        );
    }
}
