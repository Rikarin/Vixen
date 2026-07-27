// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The frame: extract, cull, prepare, sort — docs/plan/06 § Frame structure.
/// </summary>
public class RenderSystemTests {
    static RenderView Camera(RenderStageMask stages) {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") { Stages = stages, Position = Vector3.Zero, Frustum = new(view * projection) };
    }

    static RenderObject At(float z, RenderStageMask stages, uint group = 0) =>
        new() { Bounds = new(new Vector3(0f, 0f, z), 0.5f), Stages = stages, SortGroup = group };

    // --- Stages and views ---------------------------------------------------

    [Fact]
    public void A_stage_takes_the_next_index_and_a_mask_of_its_own() {
        using var system = new RenderSystem();

        var opaque = system.AddStage(new("Opaque"));
        var transparent = system.AddStage(new("Transparent", RenderSortMode.BackToFront));

        Assert.Equal(0, opaque.Index);
        Assert.Equal(1, transparent.Index);
        Assert.False(opaque.Mask.Intersects(transparent.Mask));
        Assert.Same(opaque, system.FindStage("Opaque"));
    }

    /// <summary>
    ///     A shadow view collects only the stages it asked for, whatever else the object is in.
    /// </summary>
    /// <remarks>
    ///     The property the whole view/stage split exists for: one object, extracted once, appearing
    ///     in a camera's opaque list and a shadow cascade's caster list without either knowing about
    ///     the other.
    /// </remarks>
    [Fact]
    public void A_view_collects_only_the_stages_it_enables() {
        using var system = new RenderSystem();

        var opaque = system.AddStage(new("Opaque"));
        var casters = system.AddStage(new("ShadowCaster"));

        system.Objects.Add(At(10f, opaque.Mask | casters.Mask));
        system.Objects.Add(At(20f, opaque.Mask));

        var camera = Camera(opaque.Mask);
        var shadow = Camera(casters.Mask);

        system.SetViews([camera, shadow]);
        system.Draw();

        Assert.Equal(2, system.Nodes(camera, opaque).Count);
        Assert.Single(system.Nodes(shadow, casters));

        // The shadow view enabled no opaque stage, so it collected no opaque work at all.
        Assert.Empty(system.Nodes(shadow, opaque));
    }

    // --- Sorting ------------------------------------------------------------

    [Fact]
    public void An_opaque_stage_draws_near_before_far() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var far = system.Objects.Add(At(100f, opaque.Mask));
        var near = system.Objects.Add(At(5f, opaque.Mask));
        var middle = system.Objects.Add(At(50f, opaque.Mask));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal([near, middle, far], system.Nodes(camera, opaque).Select(node => node.Object));
    }

    [Fact]
    public void A_transparent_stage_draws_far_before_near() {
        using var system = new RenderSystem();
        var blended = system.AddStage(new("Transparent", RenderSortMode.BackToFront));

        var near = system.Objects.Add(At(5f, blended.Mask));
        var far = system.Objects.Add(At(100f, blended.Mask));

        var camera = Camera(blended.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal([far, near], system.Nodes(camera, blended).Select(node => node.Object));
    }

    /// <summary>
    ///     Grouping outranks depth in an opaque stage, which is what makes the sort worth doing.
    /// </summary>
    /// <remarks>
    ///     Sorting purely front-to-back is the classic mistake: it makes a scene slower the better it
    ///     is culled, because the draw order stops correlating with pipeline state. Group first and
    ///     depth within a group is one 64-bit comparison, not two passes.
    /// </remarks>
    [Fact]
    public void Grouping_outranks_depth_and_depth_orders_within_a_group() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var groupOneFar = system.Objects.Add(At(90f, opaque.Mask, group: 1));
        var groupTwoNear = system.Objects.Add(At(5f, opaque.Mask, group: 2));
        var groupOneNear = system.Objects.Add(At(10f, opaque.Mask, group: 1));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        // Group 1 entirely before group 2, and near before far inside group 1 — even though the
        // nearest object in the scene belongs to group 2.
        Assert.Equal(
            [groupOneNear, groupOneFar, groupTwoNear],
            system.Nodes(camera, opaque).Select(node => node.Object)
        );
    }

    /// <summary>
    ///     Transparency ignores grouping, because reordering blended draws changes the image.
    /// </summary>
    [Fact]
    public void A_transparent_stage_ignores_grouping() {
        using var system = new RenderSystem();
        var blended = system.AddStage(new("Transparent", RenderSortMode.BackToFront));

        var nearHighGroup = system.Objects.Add(At(5f, blended.Mask, group: 99));
        var farLowGroup = system.Objects.Add(At(100f, blended.Mask, group: 0));

        var camera = Camera(blended.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal([farLowGroup, nearHighGroup], system.Nodes(camera, blended).Select(node => node.Object));
    }

    /// <summary>Objects at the same distance keep a stable order, so a frame is reproducible.</summary>
    /// <remarks>
    ///     A renderer that draws the same scene in a different order run to run cannot be held to a
    ///     golden image, which is how the whole rendering test strategy in doc 06 is anchored.
    /// </remarks>
    [Fact]
    public void Equal_keys_order_by_id_so_a_frame_is_reproducible() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var first = system.Objects.Add(At(20f, opaque.Mask));
        var second = system.Objects.Add(At(20f, opaque.Mask));
        var third = system.Objects.Add(At(20f, opaque.Mask));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal([first, second, third], system.Nodes(camera, opaque).Select(node => node.Object));
    }

    [Fact]
    public void A_culled_object_produces_no_work() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        system.Objects.Add(At(10f, opaque.Mask));
        system.Objects.Add(At(-10f, opaque.Mask));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Single(system.Nodes(camera, opaque));
    }

    /// <summary>Drawing twice does not accumulate: the lists are rebuilt, not appended to.</summary>
    [Fact]
    public void Drawing_twice_does_not_double_the_work() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        system.Objects.Add(At(10f, opaque.Mask));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);

        system.Draw();
        system.Draw();

        Assert.Single(system.Nodes(camera, opaque));
    }

    [Fact]
    public void More_stages_than_the_mask_holds_is_refused_where_it_can_be_named() {
        using var system = new RenderSystem();

        for (var i = 0; i < RenderStageMask.Capacity; i++) {
            system.AddStage(new($"Stage{i}"));
        }

        var error = Assert.Throws<InvalidOperationException>(() => system.AddStage(new("OneTooMany")));
        Assert.Contains("RenderStageMask", error.Message, StringComparison.Ordinal);
    }

    // --- Features -----------------------------------------------------------

    sealed class CountingFeature : RootRenderFeature {
        public override string Name => "Counting";

        public int Initialized { get; private set; }
        public int Extracted { get; private set; }
        public int Prepared { get; private set; }
        public RenderDataKey<float> Data { get; private set; }

        protected internal override void Initialize(RenderSystem system) {
            Initialized++;
            Data = system.Objects.Data.Register<float>();
        }

        protected internal override void Extract(RenderSystem system) => Extracted++;

        protected internal override void Prepare(RenderSystem system) => Prepared++;

        protected internal override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
            (uint)system.Objects.Data.Data(Data)[id.Index];
    }

    sealed class CountingSubFeature : SubRenderFeature {
        public override string Name => "CountingSub";

        public int Initialized { get; private set; }
        public int Extracted { get; private set; }
        public int Prepared { get; private set; }

        protected internal override void Initialize(RenderSystem system) => Initialized++;

        protected internal override void Extract(RenderSystem system) => Extracted++;

        protected internal override void Prepare(RenderSystem system) => Prepared++;
    }

    [Fact]
    public void A_feature_runs_extract_and_prepare_once_per_frame() {
        using var system = new RenderSystem();
        var feature = system.AddFeature(new CountingFeature());

        system.SetViews([]);
        system.Draw();

        Assert.Equal(1, feature.Initialized);
        Assert.Equal(1, feature.Extracted);
        Assert.Equal(1, feature.Prepared);
    }

    /// <summary>
    ///     A sub-feature added after the root is still initialized, so registration order is free.
    /// </summary>
    /// <remarks>
    ///     The alternative — sub-features only working if added before the root reaches a system —
    ///     is an ordering rule nothing enforces and everyone eventually gets wrong.
    /// </remarks>
    [Fact]
    public void A_sub_feature_is_initialized_whichever_order_it_was_added_in() {
        using var system = new RenderSystem();

        var before = new CountingSubFeature();
        var early = new CountingFeature();
        early.Add(before);

        system.AddFeature(early);

        var after = new CountingSubFeature();
        early.Add(after);

        Assert.Equal(1, before.Initialized);
        Assert.Equal(1, after.Initialized);

        system.SetViews([]);
        system.Draw();

        Assert.Equal(1, before.Extracted);
        Assert.Equal(1, after.Prepared);
    }

    [Fact]
    public void A_sub_feature_belongs_to_one_root() {
        var sub = new CountingSubFeature();
        var first = new CountingFeature();
        var second = new CountingFeature();

        first.Add(sub);

        Assert.Same(first, sub.Parent);
        Assert.Throws<InvalidOperationException>(() => second.Add(sub));
    }

    /// <summary>
    ///     A feature's own sort group is what the key uses, so it can group by pipeline state.
    /// </summary>
    [Fact]
    public void A_feature_decides_its_objects_sort_group() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var feature = system.AddFeature(new CountingFeature());

        var near = system.Objects.Add(At(5f, opaque.Mask) with { FeatureIndex = feature.Index });
        var far = system.Objects.Add(At(100f, opaque.Mask) with { FeatureIndex = feature.Index });

        // The far object is put in the earlier group, which has to win over its greater depth.
        system.Objects.Data.Data(feature.Data)[far.Index] = 1f;
        system.Objects.Data.Data(feature.Data)[near.Index] = 2f;

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        Assert.Equal([far, near], system.Nodes(camera, opaque).Select(node => node.Object));
    }
}
