// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Testing;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class TransformSystemTests {
    [Fact]
    public void ARootsWorldTransformIsItsLocalTransform() {
        using var world = new World();
        var system = new TransformSystem();
        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 2, 3)));

        system.Resolve(world);

        Assert.Equal(new Vector3(1, 2, 3), world.Read<WorldTransform>(entity).Position);
    }

    [Fact]
    public void AChildIsPositionedRelativeToItsParent() {
        using var world = new World();
        var system = new TransformSystem();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new(10, 0, 0)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 5, 0)));

        Hierarchy.SetParent(world, child, parent);
        system.Resolve(world);

        Assert.Equal(new Vector3(10, 5, 0), world.Read<WorldTransform>(child).Position);
    }

    [Fact]
    public void AParentsRotationCarriesToItsChild() {
        using var world = new World();
        var system = new TransformSystem();

        var parent = Hierarchy.CreateTransform(
            world,
            LocalTransform.Identity with { Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 2) }
        );

        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 0, 0)));
        Hierarchy.SetParent(world, child, parent);

        system.Resolve(world);

        // A quarter turn about +Y takes +X to -Z, in a right-handed system.
        var position = world.Read<WorldTransform>(child).Position;
        Assert.True(MathF.Abs(position.X) < 1e-5f, $"{position}");
        Assert.True(MathF.Abs(position.Z + 1) < 1e-5f, $"{position}");
    }

    [Fact]
    public void AGrandchildFollowsTheWholeChain() {
        using var world = new World();
        var system = new TransformSystem();
        var a = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 0, 0)));
        var b = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 1, 0)));
        var c = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 0, 1)));

        Hierarchy.SetParent(world, b, a);
        Hierarchy.SetParent(world, c, b);
        system.Resolve(world);

        Assert.Equal(new Vector3(1, 1, 1), world.Read<WorldTransform>(c).Position);
    }

    [Fact]
    public void MovingAParentMovesEverythingBelowIt() {
        using var world = new World();
        var system = new TransformSystem();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 1, 0)));
        var grandchild = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 1, 0)));

        Hierarchy.SetParent(world, child, parent);
        Hierarchy.SetParent(world, grandchild, child);
        system.Resolve(world);

        world.AdvanceVersion();
        world.Get<LocalTransform>(parent).Position = new(5, 0, 0);
        system.Resolve(world);

        Assert.Equal(new Vector3(5, 2, 0), world.Read<WorldTransform>(grandchild).Position);
    }

    /// <summary>
    ///     ⚠ <b>Setting a world rotation under a parent turned about a <i>different</i> axis.</b>
    ///     Composition here reads left to right — <c>world = local * parent</c>, the quaternion form
    ///     of what this pass does in matrices — so recovering the local rotation is
    ///     <c>world * conjugate(parent)</c> and not the other way about. The two agree whenever the
    ///     rotations commute, which is why every earlier test of this setter passed while it was
    ///     wrong: they all turned the parent and the child about the same axis, or left the parent
    ///     unturned.
    /// </summary>
    [Fact]
    public void AWorldRotationSetUnderARotatedParentComesBackUnchanged() {
        using var world = new World();
        var system = new TransformSystem();

        var parent = Hierarchy.CreateTransform(
            world,
            LocalTransform.Identity with { Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 2) }
        );

        var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        Hierarchy.SetParent(world, child, parent);
        system.Resolve(world);

        var wanted = Quaternion.FromAxisAngle(Vector3.UnitX, MathF.PI / 2);

        world.AdvanceVersion();
        new Transform(world, child).Rotation = wanted;
        system.Resolve(world);

        var actual = new Transform(world, child).Rotation;
        Assert.True(Quaternion.SameRotation(wanted, actual, 1e-5f), $"wanted {wanted}, got {actual}");

        // And the axis it puts the child's own −Z on, which is what a rotate gizmo is really asking
        // for: a quarter turn about +X takes forward from −Z to +Y.
        var forward = Quaternion.Transform(Vector3.Forward, actual);
        Assert.True(Vector3.NearEqual(Vector3.UnitY, forward, 1e-5f), $"{forward}");
    }

    /// <summary>
    ///     The same conversion the other way, and the reason the first one is not a coincidence: a
    ///     local rotation written directly, composed by the pass, must be what the world-space getter
    ///     reports — so the getter and the setter are each other's inverse rather than each other's
    ///     mirror.
    /// </summary>
    [Fact]
    public void TheWorldRotationGetterInvertsTheSetter() {
        using var world = new World();
        var system = new TransformSystem();

        var parent = Hierarchy.CreateTransform(
            world,
            LocalTransform.Identity with { Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 0.7f) }
        );

        var child = Hierarchy.CreateTransform(
            world,
            LocalTransform.Identity with { Rotation = Quaternion.FromAxisAngle(Vector3.UnitX, 1.1f) }
        );

        Hierarchy.SetParent(world, child, parent);
        system.Resolve(world);

        var reported = new Transform(world, child).Rotation;

        world.AdvanceVersion();
        new Transform(world, child).Rotation = reported;
        system.Resolve(world);

        Assert.True(
            Quaternion.SameRotation(
                Quaternion.FromAxisAngle(Vector3.UnitX, 1.1f),
                world.Read<LocalTransform>(child).Rotation,
                1e-5f
            ),
            $"{world.Read<LocalTransform>(child).Rotation}"
        );
    }

    /// <summary>
    ///     The reason the ECS carries change versions at all. A frame in which nothing moved must
    ///     visit nothing — a static scene of ten thousand entities has to cost zero.
    /// </summary>
    [Fact]
    public void AFrameInWhichNothingMovedTouchesNothing() {
        using var world = new World();
        var system = new TransformSystem();
        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 2, 3)));

        world.AdvanceVersion();
        system.Resolve(world);

        // Sabotage the derived value, then run a frame in which nothing moved. A pass that visited
        // it would put it back; the whole point is that it does not.
        world.AdvanceVersion();
        world.Get<WorldTransform>(entity).Value = default;
        world.AdvanceVersion();
        system.Resolve(world);

        Assert.Equal(default, world.Read<WorldTransform>(entity).Value);
    }

    [Fact]
    public void ReparentingCountsAsMovingEvenThoughTheLocalTransformDidNotChange() {
        using var world = new World();
        var system = new TransformSystem();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new(10, 0, 0)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 5, 0)));

        system.Resolve(world);
        Assert.Equal(new Vector3(0, 5, 0), world.Read<WorldTransform>(child).Position);

        world.AdvanceVersion();
        Hierarchy.SetParent(world, child, parent);
        world.AdvanceVersion();
        system.Resolve(world);

        Assert.Equal(new Vector3(10, 5, 0), world.Read<WorldTransform>(child).Position);
    }

    [Fact]
    public void ReparentingKeepingWorldPositionDoesNotMoveTheEntity() {
        using var world = new World();
        var system = new TransformSystem();
        var parent = Hierarchy.CreateTransform(world, LocalTransform.At(new(10, 3, 0)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 2, 3)));

        system.Resolve(world);
        world.AdvanceVersion();

        Hierarchy.SetParentKeepingWorldPosition(world, child, parent);
        world.AdvanceVersion();
        system.Resolve(world);

        var position = world.Read<WorldTransform>(child).Position;
        Assert.True((position - new Vector3(1, 2, 3)).Length() < 1e-4f, $"{position}");
        Assert.Equal(new Vector3(-9, -1, 3), Round(world.Read<LocalTransform>(child).Position));
    }

    [Fact]
    public void ADeepChainResolvesInOnePass() {
        using var world = new World();
        var system = new TransformSystem();
        var chain = new List<Entity>();
        var previous = Entity.Null;

        for (var index = 0; index < 32; index++) {
            var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 1, 0)));

            if (!previous.IsNull) {
                Hierarchy.SetParent(world, entity, previous);
            }

            chain.Add(entity);
            previous = entity;
        }

        system.Resolve(world);

        Assert.Equal(new Vector3(0, 32, 0), world.Read<WorldTransform>(chain[^1]).Position);
        Assert.Equal(31, Hierarchy.DepthOf(world, chain[^1]));
    }

    /// <summary>
    ///     What the Phase 2 exit criteria ask for: a scene with a hierarchy, stepped ten thousand
    ///     times, allocating nothing after warm-up.
    /// </summary>
    [Fact]
    public void ASteadyStateSceneAllocatesNothing() {
        using var world = new World();
        var system = new TransformSystem();
        var roots = new List<Entity>();

        for (var index = 0; index < 200; index++) {
            var root = Hierarchy.CreateTransform(world, LocalTransform.At(new(index, 0, 0)));
            roots.Add(root);

            for (var child = 0; child < 4; child++) {
                var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, child, 0)));
                Hierarchy.SetParent(world, entity, root);
            }
        }

        // Warmed up until the buckets have grown to the depth this scene needs and every chunk has
        // been visited once, then measured over five hundred more.
        Assert.Equal(0, Measured.Bytes(Frame, warmUp: 8, passes: 500));

        return;

        void Frame() {
            world.AdvanceVersion();

            foreach (var root in roots) {
                world.Get<LocalTransform>(root).Position += Vector3.UnitX;
            }

            system.Resolve(world);
        }
    }

    /// <summary>
    ///     A host with no system graph — the editor — resolves and <i>then</i> advances, and every
    ///     edit after the first one is what says whether it got the order right.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A tool's writes are not inside a phase.</b> <c>SystemRunner</c> advances before a
    ///     phase runs, so everything that phase writes is stamped later than anything the previous
    ///     pass recorded as seen. An editor has no phases: a write lands whenever somebody typed,
    ///     stamped with whatever the version currently is. Advancing before the pass would stamp
    ///     those writes with exactly the version the pass just recorded — so the next pass answers
    ///     "nothing changed", and the symptom is an inspector that moves an object once and then
    ///     never again.
    /// </remarks>
    [Fact]
    public void AHostWithNoPhasesSeesEveryEditWhenItAdvancesAfterResolving() {
        using var world = new World();
        var system = new TransformSystem();
        var entity = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Frame();

        for (var edit = 1; edit <= 3; edit++) {
            // Between two frames, the way a pointer event or a typed number arrives.
            new Transform(world, entity).Position = new Vector3(edit, 0, 0);
            Frame();

            Assert.Equal(new Vector3(edit, 0, 0), world.Read<WorldTransform>(entity).Position);
        }

        return;

        void Frame() {
            system.Resolve(world);
            world.AdvanceVersion();
        }
    }

    static Vector3 Round(Vector3 value) =>
        new(MathF.Round(value.X, 3), MathF.Round(value.Y, 3), MathF.Round(value.Z, 3));
}
