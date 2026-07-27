// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Per-view visibility — docs/plan/06 § Frame structure, step 4.
/// </summary>
/// <remarks>
///     The testing table in doc 06 asks for culling to be "compared against a brute-force CPU oracle
///     over randomised scenes", which is what <see cref="It_agrees_with_a_brute_force_oracle" />
///     does. The rest pin the properties that make the fast path fast — bit packing, per-view
///     independence, the cheap rejections before the frustum test — since each of those is a place
///     an optimisation can quietly start returning the wrong answer.
/// </remarks>
public class VisibilityGroupTests {
    static RenderView Camera(float maximumDistance = 0f) {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") {
            Stages = RenderStageMask.Of(0),
            Position = Vector3.Zero,
            Frustum = new(view * projection),
            MaximumDistance = maximumDistance
        };
    }

    static RenderObject At(Vector3 centre, float radius = 1f, int stage = 0) =>
        new() { Bounds = new(centre, radius), Stages = RenderStageMask.Of(stage) };

    [Fact]
    public void An_object_in_front_is_visible_and_one_behind_is_not() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        var ahead = store.Add(At(new(0f, 0f, 10f)));
        var behind = store.Add(At(new(0f, 0f, -10f)));

        visibility.Cull(store, [Camera()]);

        Assert.True(visibility.IsVisible(0, ahead));
        Assert.False(visibility.IsVisible(0, behind));
    }

    /// <summary>A dead slot is never visible, however good its bounds look.</summary>
    [Fact]
    public void A_removed_object_is_culled() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        var id = store.Add(At(new(0f, 0f, 10f)));
        store.Remove(id);

        visibility.Cull(store, [Camera()]);

        Assert.False(visibility.IsVisible(0, id));
    }

    /// <summary>
    ///     An object in no stage the view draws is rejected before the frustum is consulted.
    /// </summary>
    /// <remarks>
    ///     This is what stops a shadow view walking the UI, and it is the cheapest of the three
    ///     rejections — one <c>and</c> on a value already loaded.
    /// </remarks>
    [Fact]
    public void An_object_in_no_stage_this_view_draws_is_culled() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        var elsewhere = store.Add(At(new(0f, 0f, 10f), stage: 3));

        visibility.Cull(store, [Camera()]);

        Assert.False(visibility.IsVisible(0, elsewhere));
    }

    /// <summary>
    ///     A view's distance cutoff is measured to the sphere's surface.
    /// </summary>
    /// <remarks>
    ///     To the surface, not the centre, so a large object does not pop out while it is still
    ///     visibly on screen — the artefact that makes a shadow-distance setting look broken.
    /// </remarks>
    [Fact]
    public void The_distance_cutoff_measures_to_the_surface() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        var big = store.Add(At(new(0f, 0f, 60f), radius: 20f));
        var small = store.Add(At(new(0f, 0f, 60f), radius: 1f));

        visibility.Cull(store, [Camera(maximumDistance: 50f)]);

        Assert.True(visibility.IsVisible(0, big));
        Assert.False(visibility.IsVisible(0, small));
    }

    /// <summary>Views are independent: a shadow view culling something does not hide it from the camera.</summary>
    [Fact]
    public void Each_view_gets_its_own_answer() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        var id = store.Add(At(new(0f, 0f, 500f)));

        var near = Camera(maximumDistance: 50f);
        var far = Camera();

        visibility.Cull(store, [near, far]);

        Assert.False(visibility.IsVisible(0, id));
        Assert.True(visibility.IsVisible(1, id));
    }

    /// <summary>
    ///     Objects either side of a 64-object word boundary are both answered correctly.
    /// </summary>
    /// <remarks>
    ///     The bit packing's one sharp edge: a <c>ulong</c> holds 64 objects' bits, so an off-by-one
    ///     in the word or bit index shows up only at a multiple of 64 — which a small fixture never
    ///     reaches.
    /// </remarks>
    [Fact]
    public void The_word_boundary_is_not_an_off_by_one() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        for (var i = 0; i < 200; i++) {
            // Alternating in front of and behind the camera, so the expected pattern is known.
            store.Add(At(new(0f, 0f, i % 2 == 0 ? 10f : -10f)));
        }

        visibility.Cull(store, [Camera()]);

        for (var i = 0; i < 200; i++) {
            Assert.Equal(i % 2 == 0, visibility.IsVisible(0, new(i)));
        }

        Assert.Equal(100, visibility.VisibleCount(0));
    }

    /// <summary>
    ///     Running culling on the job system gives the same answer as running it inline.
    /// </summary>
    /// <remarks>
    ///     The property that makes the parallel path safe without a lock: threads take whole 64-bit
    ///     words, so no two ever write the same one. A batch size that was not a multiple of 64
    ///     would make this flaky rather than failing — which is why it is asserted rather than
    ///     assumed.
    /// </remarks>
    [Fact]
    public void Culling_in_parallel_agrees_with_culling_inline() {
        using var store = new RenderObjectStore();
        using var inline = new VisibilityGroup();
        using var parallel = new VisibilityGroup();
        using var scheduler = new JobScheduler();

        var random = new Random(20260727);

        for (var i = 0; i < 4000; i++) {
            store.Add(
                At(
                    new(
                        (float)(random.NextDouble() * 200 - 100),
                        (float)(random.NextDouble() * 200 - 100),
                        (float)(random.NextDouble() * 200 - 100)
                    ),
                    radius: (float)random.NextDouble() * 5f
                )
            );
        }

        var views = new[] { Camera(), Camera(maximumDistance: 40f) };

        inline.Cull(store, views);
        parallel.Cull(store, views, scheduler);

        for (var view = 0; view < views.Length; view++) {
            Assert.Equal(inline.Words(view).ToArray(), parallel.Words(view).ToArray());
        }
    }

    /// <summary>
    ///     The brute-force oracle doc 06's testing table asks for.
    /// </summary>
    /// <remarks>
    ///     The oracle is the definition — "inside the frustum and within range" — written the slow,
    ///     obvious way with no bit packing, no batching and no early exits. The value is entirely in
    ///     it being a second implementation: the fast path can be rewritten and this still says
    ///     whether the answer changed.
    /// </remarks>
    [Fact]
    public void It_agrees_with_a_brute_force_oracle() {
        var scene = Gen.Select(
                Gen.Float[-150f, 150f],
                Gen.Float[-150f, 150f],
                Gen.Float[-150f, 150f],
                Gen.Float[0.1f, 12f]
            )
            .Array[1, 300];

        scene.Sample(
            objects => {
                using var store = new RenderObjectStore();
                using var visibility = new VisibilityGroup();

                foreach (var (x, y, z, radius) in objects) {
                    store.Add(At(new(x, y, z), radius));
                }

                var view = Camera(maximumDistance: 90f);
                visibility.Cull(store, [view]);

                for (var i = 0; i < objects.Length; i++) {
                    var bounds = store[new(i)].Bounds;

                    var reach = view.MaximumDistance + bounds.Radius;
                    var offset = bounds.Center - view.Position;
                    var expected = Vector3.Dot(offset, offset) <= reach * reach
                        && view.Frustum.Contains(bounds) != ContainmentType.Disjoint;

                    Assert.Equal(expected, visibility.IsVisible(0, new(i)));
                }
            },
            iter: 200
        );
    }

    [Fact]
    public void Culling_no_views_or_no_objects_is_harmless() {
        using var store = new RenderObjectStore();
        using var visibility = new VisibilityGroup();

        visibility.Cull(store, []);
        Assert.Equal(0, visibility.VisibleCount(0));

        store.Add(At(new(0f, 0f, 10f)));
        visibility.Cull(store, []);
        Assert.False(visibility.IsVisible(0, new(0)));
    }
}
