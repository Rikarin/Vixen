// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>Both of doc 34's new asset kinds, loaded by address the way a clip is.</summary>
public class LoadPathTests {
    readonly Skeleton skeleton = TestRigs.Branching();

    // ── Proxy shapes ─────────────────────────────────────────────────────────

    [Fact]
    public void AShapeSetIsBakedOncePerRig() {
        var content = Shapes();

        var first = ProxyShapeCache.Get(content, skeleton);

        Assert.Same(first, ProxyShapeCache.Get(content, skeleton));

        // A second rig is a second bake, because the joint indices are the rig's.
        Assert.NotSame(first, ProxyShapeCache.Get(content, TestRigs.Branching()));
    }

    /// <summary>
    ///     ⚠ <b>An empty set is an answer and not a failure</b> — and the second caller is told the
    ///     same thing as the first. An empty unresolved list on the second ask reads as "nothing was
    ///     wrong" rather than as "somebody already asked".
    /// </summary>
    [Fact]
    public void ASetThatNamesNoJointOfThisRigBakesEmptyAndSaysWhichNamesWentMissing() {
        var content = new ProxyShapeSetContent {
            Name = "wrong body",
            Shapes = [new("fin", ShapeKind.Sphere, "Dorsal", Vector3.Zero, Quaternion.Identity, new(0.1f), new(0.1f), [], false)]
        };

        List<string> first = [];
        List<string> second = [];

        Assert.Equal(0, ProxyShapeCache.Get(content, skeleton, first).Count);
        Assert.Equal(0, ProxyShapeCache.Get(content, skeleton, second).Count);

        Assert.Equal("fin", Assert.Single(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void ForgettingARigsBakeMakesTheNextAskRebuild() {
        var content = Shapes();

        var first = ProxyShapeCache.Get(content, skeleton);

        Assert.True(ProxyShapeCache.Forget(content));

        Assert.NotSame(first, ProxyShapeCache.Get(content, skeleton));
        Assert.False(ProxyShapeCache.Forget(new ProxyShapeSetContent()));
    }

    // ── Move sets ────────────────────────────────────────────────────────────

    [Fact]
    public void AMoveSetResolvesItsClipsThroughTheClipCache() {
        var walk = Clip("Walk");
        var content = Set(("walk", "Assets/Walk.vxanim"), ("run", "Assets/Run.vxanim"));

        List<string> unresolved = [];

        var set = MoveSetCache.Get(
            content,
            skeleton,
            address => address == "Assets/Walk.vxanim" ? walk : null,
            unresolved: unresolved
        );

        // ⚠ The run is dropped rather than baked against nothing: an entry that plays silence reads
        // in game as a character freezing, which is much harder to trace than a missing move.
        Assert.Equal(1, set.Count);
        Assert.Equal("walk", set[0].Name);
        Assert.Equal("run", Assert.Single(unresolved));

        // The motion holds the clip the clip cache made, not a second bake of the same content.
        var motion = Assert.IsType<Motions.ClipMotion>(set[0].Motion);

        Assert.Same(AnimationClipCache.Get(walk, skeleton), motion.Clip);
        Assert.Same(set, MoveSetCache.Get(content, skeleton, _ => walk));
    }

    [Fact]
    public void AnOverlayComposesOverItsBase() {
        var walk = Clip("Walk");
        var limp = Clip("Limp");

        var baseline = Set(("walk", "Assets/Walk.vxanim"), ("idle", "Assets/Walk.vxanim"));
        var injured = Set(("walk", "Assets/Limp.vxanim"));

        injured.Bases.Add("Assets/locomotion.vxmoveset");

        var set = MoveSetCache.Get(
            injured,
            skeleton,
            address => address == "Assets/Limp.vxanim" ? limp : walk,
            _ => baseline
        );

        // The base's idle survives and its walk is replaced.
        Assert.Equal(2, set.Count);

        Assert.True(set.TryGet(MoveKey.Of("walk"), out var replaced));
        Assert.Equal("Limp", ((Motions.ClipMotion) replaced!.Motion).Clip.Name);

        Assert.True(set.TryGet(MoveKey.Of("idle"), out _));
    }

    /// <summary>
    ///     ⚠ <b>An overlay cycle is broken rather than followed.</b> A set naming itself round a chain
    ///     is a mistake somebody will make in a text file, and following it is a stack overflow rather
    ///     than a diagnostic.
    /// </summary>
    [Fact]
    public void AnOverlayCycleIsBrokenAndReported() {
        var walk = Clip("Walk");

        var first = Set(("walk", "Assets/Walk.vxanim"));
        var second = Set(("run", "Assets/Walk.vxanim"));

        first.Bases.Add("second");
        second.Bases.Add("first");

        List<string> unresolved = [];

        var set = MoveSetCache.Get(
            first,
            skeleton,
            _ => walk,
            address => address == "second" ? second : first,
            unresolved
        );

        Assert.Equal(2, set.Count);
        Assert.Contains("first", unresolved);
    }

    /// <summary>With no loader at all, a set bakes empty rather than throwing.</summary>
    [Fact]
    public void ASetWithNoLoaderIsEmptyRatherThanBroken() {
        var set = MoveSetCache.Get(Set(("walk", "Assets/Walk.vxanim")), skeleton);

        Assert.Equal(0, set.Count);
    }

    static ProxyShapeSetContent Shapes() =>
        new() {
            Name = "body",
            Shapes = [
                new("belly", ShapeKind.Sphere, "Spine", Vector3.Zero, Quaternion.Identity, new(0.2f), new(0.2f), [], false)
            ]
        };

    static MoveSetContent Set(params (string Name, string Clip)[] rows) =>
        new() {
            Name = "locomotion",
            Entries = [.. rows.Select(static row => new MoveEntryRecord { Name = row.Name, Clip = row.Clip })]
        };

    static AnimationClipContent Clip(string name) =>
        new() { Name = name, Data = new() { Name = name, Duration = 1f } };
}
