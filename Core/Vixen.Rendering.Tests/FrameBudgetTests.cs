// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Rendering;
using Vixen.Testing;
using Xunit;

namespace Tests;

/// <summary>
///     What a steady-state frame is allowed to cost — docs/plan/06 § Testing, "with allocation
///     assertions".
/// </summary>
/// <remarks>
///     <para>
///         Ten thousand objects through extract → cull → sort, asserting that a settled frame
///         allocates nothing. The number matters less than the property: every per-frame array here
///         is reused, so a change that starts allocating per object per frame fails this rather than
///         showing up months later as a GC spike nobody can attribute.
///     </para>
///     <para>
///         Asserted on a <em>settled</em> frame, after the store, the visibility bitsets and the node
///         lists have all reached their sizes. The first frame allocates all of them and is supposed
///         to; the hundredth is the one a game actually runs.
///     </para>
/// </remarks>
public class FrameBudgetTests {
    const int ObjectCount = 10_000;

    static RenderSystem Scene(out RenderView camera, out RenderStage opaque, out RenderStage blended) {
        var system = new RenderSystem();
        opaque = system.AddStage(new("Opaque"));
        blended = system.AddStage(new("Transparent", RenderSortMode.BackToFront));

        var random = new Random(20260727);

        for (var i = 0; i < ObjectCount; i++) {
            var stages = i % 4 == 0 ? blended.Mask : opaque.Mask;

            system.Objects.Add(
                new() {
                    Bounds = new(
                        new Vector3(
                            (float)(random.NextDouble() * 400 - 200),
                            (float)(random.NextDouble() * 400 - 200),
                            (float)(random.NextDouble() * 400 - 200)
                        ),
                        (float)random.NextDouble() * 3f
                    ),
                    Stages = stages,
                    SortGroup = (uint)(i % 16)
                }
            );
        }

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);

        camera = new("camera") {
            Stages = opaque.Mask | blended.Mask,
            Position = Vector3.Zero,
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);
        return system;
    }

    [Fact]
    public void A_settled_frame_allocates_nothing() {
        using var system = Scene(out var camera, out var opaque, out var blended);

        // Settle: everything that grows has grown, and every list has reached its length.
        for (var i = 0; i < 8; i++) {
            system.Draw();
        }

        // Proof the measurement is over real work rather than an empty frame.
        Assert.NotEmpty(system.Nodes(camera, opaque));
        Assert.NotEmpty(system.Nodes(camera, blended));

        Measured.NothingAllocated(
            system.Draw,
            warmUp: 0,
            passes: 16,
            because: "The per-frame path is meant to reuse every array it touches."
        );
    }

    /// <summary>
    ///     The same frame on the job system, which must agree with the inline one object for object.
    /// </summary>
    /// <remarks>
    ///     Compared as a full ordered list rather than a count: a parallel cull that dropped a word
    ///     would still produce a plausible count, and a sort fed a different set would still produce
    ///     a sorted answer.
    /// </remarks>
    [Fact]
    public void The_parallel_frame_draws_the_same_list_as_the_inline_one() {
        using var inline = Scene(out var inlineCamera, out var inlineOpaque, out _);
        using var parallel = Scene(out var parallelCamera, out var parallelOpaque, out _);
        using var scheduler = new JobScheduler();

        parallel.Scheduler = scheduler;

        inline.Draw();
        parallel.Draw();

        Assert.Equal(
            inline.Nodes(inlineCamera, inlineOpaque).Select(node => node.Object).ToArray(),
            parallel.Nodes(parallelCamera, parallelOpaque).Select(node => node.Object).ToArray()
        );
    }

    /// <summary>Every node in a stage's list really is visible and really is in that stage.</summary>
    /// <remarks>
    ///     The invariant that ties culling and collection together. Asserted over a scene big enough
    ///     to cross many word boundaries, because that is where a bit-index slip hides.
    /// </remarks>
    [Fact]
    public void Every_collected_node_is_visible_and_in_the_stage() {
        using var system = Scene(out var camera, out var opaque, out var blended);
        system.Draw();

        foreach (var stage in (RenderStage[])[opaque, blended]) {
            foreach (var node in system.Nodes(camera, stage)) {
                Assert.True(system.Visibility.IsVisible(camera.Index, node.Object));
                Assert.True(system.Objects[node.Object].Stages.Contains(stage.Index));
                Assert.True(system.Objects[node.Object].IsAlive);
            }
        }

        // And nothing visible in a stage was left out: the two lists partition the visible set,
        // because the scene puts every object in exactly one of the two stages.
        Assert.Equal(
            system.Visibility.VisibleCount(camera.Index),
            system.Nodes(camera, opaque).Count + system.Nodes(camera, blended).Count
        );
    }
}
