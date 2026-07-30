// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Xunit;

namespace Vixen.Editor.Profiler.Tests;

/// <summary>
///     Rebuilding the nesting the sample rings deliberately do not keep.
/// </summary>
/// <remarks>
///     ⚠ <b>Every case here is built with the samples in <i>completion</i> order, because that is
///     the order the ring produces and the order that makes a naive reading build the tree upside
///     down.</b> A test that fed them in begin order would pass against a builder that ignores the
///     sort entirely, which is the one bug worth having a test for.
/// </remarks>
public sealed class FlameTreeTests {
    static readonly ProfilingKey Frame = ProfilingKey.Register("Test.Frame");
    static readonly ProfilingKey Cull = ProfilingKey.Register("Test.Cull");
    static readonly ProfilingKey Draw = ProfilingKey.Register("Test.Draw");
    static readonly ProfilingKey Inner = ProfilingKey.Register("Test.Inner");

    static ProfilerSample Sample(ProfilingKey key, int depth, long begin, int duration, int frame = 0) =>
        new(key, depth, begin, duration, frame);

    [Fact]
    public void ChildrenLandUnderTheParentThatContainsThem() {
        // Completion order: the two children close before the parent does.
        ProfilerSample[] samples = [
            Sample(Cull, 1, 110, 30),
            Sample(Draw, 1, 150, 40),
            Sample(Frame, 0, 100, 100)
        ];

        var root = Assert.Single(FlameNode.Build(samples));

        Assert.Equal("Test.Frame", root.Name);
        Assert.Equal(0, root.Level);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("Test.Cull", root.Children[0].Name);
        Assert.Equal("Test.Draw", root.Children[1].Name);
        Assert.All(root.Children, child => Assert.Equal(1, child.Level));
    }

    [Fact]
    public void NestingGoesAsDeepAsTheSamplesDo() {
        ProfilerSample[] samples = [
            Sample(Inner, 2, 120, 10),
            Sample(Cull, 1, 110, 30),
            Sample(Frame, 0, 100, 100)
        ];

        var root = Assert.Single(FlameNode.Build(samples));

        Assert.Equal(3, root.Height);
        Assert.Equal(3, root.Count);
        Assert.Equal("Test.Inner", root.Children[0].Children[0].Name);
        Assert.Equal(2, root.Children[0].Children[0].Level);
    }

    /// <summary>
    ///     Two frames are two roots, and the second must not become a child of the first merely
    ///     because they are both at depth zero.
    /// </summary>
    [Fact]
    public void ConsecutiveRootsAreSiblings() {
        ProfilerSample[] samples = [
            Sample(Frame, 0, 100, 50),
            Sample(Frame, 0, 200, 50, frame: 1)
        ];

        var roots = FlameNode.Build(samples);

        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.Empty(root.Children));
    }

    /// <summary>
    ///     A parent's first child usually opens in the same tick it does, and depth is what tells
    ///     the two apart.
    /// </summary>
    [Fact]
    public void AChildBeginningInTheSameTickIsStillAChild() {
        ProfilerSample[] samples = [
            Sample(Cull, 1, 100, 40),
            Sample(Frame, 0, 100, 100)
        ];

        var root = Assert.Single(FlameNode.Build(samples));

        Assert.Equal("Test.Frame", root.Name);
        Assert.Single(root.Children);
    }

    /// <summary>
    ///     ⚠ A ring that wrapped mid-frame hands over a child whose parent went over the side. It
    ///     draws as a root, because that is what anything left in the capture can tell.
    /// </summary>
    [Fact]
    public void AnOrphanBecomesARootRatherThanFloatingAtItsRecordedDepth() {
        ProfilerSample[] samples = [Sample(Inner, 3, 120, 10)];

        var root = Assert.Single(FlameNode.Build(samples));

        Assert.Equal(0, root.Level);
        Assert.Equal(3, root.Sample.Depth);
    }

    /// <summary>Self time is the parent's minus its children's, and never negative.</summary>
    [Fact]
    public void SelfTimeExcludesTheChildren() {
        ProfilerSample[] samples = [
            Sample(Cull, 1, 110, 30),
            Sample(Draw, 1, 150, 40),
            Sample(Frame, 0, 100, 100)
        ];

        var root = Assert.Single(FlameNode.Build(samples));

        // 100 ticks with 70 accounted for by the children.
        Assert.True(root.SelfMilliseconds < root.Milliseconds);
        Assert.True(root.SelfMilliseconds > 0d);
        Assert.Equal(root.Children[0].Milliseconds, root.Children[0].SelfMilliseconds);
    }

    [Fact]
    public void BuildingDoesNotReorderTheCallersArray() {
        ProfilerSample[] samples = [
            Sample(Cull, 1, 110, 30),
            Sample(Frame, 0, 100, 100)
        ];

        FlameNode.Build(samples);

        Assert.Equal(Cull, samples[0].Key);
        Assert.Equal(Frame, samples[1].Key);
    }

    [Fact]
    public void NothingInIsNothingOut() => Assert.Empty(FlameNode.Build([]));
}
