// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The per-view block — set 1 of the four-set convention.
/// </summary>
/// <remarks>
///     The half of "where am I being drawn from" that did not exist. A world matrix reached a shader
///     through a push constant and a view-projection reached it through nothing at all, so a shadow
///     caster could not be told which cascade it was drawing for.
/// </remarks>
public class ViewConstantsTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly DescriptorAllocator allocator;
    readonly DescriptorSetLayoutHandle layout;

    public ViewConstantsTests() {
        allocator = new(device);

        layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerView, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "view")
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    ViewConstants Constants() => new(device) { Descriptors = allocator, Layout = layout };

    /// <summary>The recorder sees a list when it is submitted, not while it is being written.</summary>
    void Submit(ICommandList list) {
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    static RenderView View(string name, float x) {
        var view = new RenderView(name) { Position = new(x, 0f, 0f) };

        view.ViewProjection = new(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(x, 0f, 0f, 1f)
        );

        return view;
    }

    /// <summary>
    ///     Setting a view's matrix re-derives its frustum.
    /// </summary>
    /// <remarks>
    ///     Two properties describing one volume is a bug waiting to be written: a view culled against
    ///     last frame's planes and drawn with this frame's matrix drops geometry at the edges, and
    ///     nothing reports it. The shadow renderer had exactly that shape — it set the frustum from a
    ///     matrix it then discarded — which is why nothing could tell a caster its cascade.
    /// </remarks>
    [Fact]
    public void Setting_the_matrix_derives_the_frustum() {
        var view = new RenderView("camera");
        var before = view.Frustum;

        view.ViewProjection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f);

        Assert.NotEqual(before, view.Frustum);
        Assert.Equal(new BoundingFrustum(view.ViewProjection), view.Frustum);
    }

    /// <summary>Each view gets its own block, holding its own matrix.</summary>
    [Fact]
    public void Every_view_gets_its_own_block() {
        using var constants = Constants();
        var list = device.BeginCommandList();

        var first = View("first", 1f);
        var second = View("second", 2f);

        allocator.BeginFrame();
        Assert.True(constants.Bind(list, first));
        Assert.True(constants.Bind(list, second));

        Assert.Equal(2, constants.Count);
        Assert.Equal(first.ViewProjection, constants.Of(first).Get(ViewConstants.ViewProjection));
        Assert.Equal(second.ViewProjection, constants.Of(second).Get(ViewConstants.ViewProjection));
    }

    /// <summary>
    ///     Two views are two sets, bound at the per-view slot.
    /// </summary>
    /// <remarks>
    ///     The allocator is content-addressed, so two views sharing a matrix would share a set — which
    ///     is correct and is why the two here differ. What must never happen is one set for two
    ///     different matrices, because then a cascade draws with its neighbour's projection.
    /// </remarks>
    [Fact]
    public void Two_views_bind_two_sets_at_the_per_view_slot() {
        using var constants = Constants();
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        constants.Bind(list, View("first", 1f));
        constants.Bind(list, View("second", 2f));
        Submit(list);

        var binds = device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet).ToList();

        Assert.Equal(2, binds.Count);
        Assert.All(binds, bind => Assert.Equal((long)DescriptorSetSlot.PerView, bind.A));
        Assert.NotEqual(binds[0].B, binds[1].B);
    }

    /// <summary>A view whose matrix did not move does not go to the GPU again.</summary>
    /// <remarks>
    ///     A camera that is still, a shadow cascade the snapping held in place — both are ordinary,
    ///     and both should cost a bind and nothing else. It is also what keeps the ring free: the
    ///     block only moves regions when it is rewritten.
    /// </remarks>
    [Fact]
    public void A_view_that_did_not_move_is_not_uploaded_again() {
        using var constants = Constants();
        var list = device.BeginCommandList();
        var view = View("camera", 1f);

        allocator.BeginFrame();
        constants.Bind(list, view);
        constants.Bind(list, view);
        constants.Bind(list, view);
        Submit(list);

        // Three binds, one upload — which is what the second and third calls cost.
        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal(1, allocator.WriteCount);
        Assert.Equal(2, allocator.ReuseCount);
    }

    /// <summary>Without a layout or an allocator it binds nothing rather than throwing.</summary>
    /// <remarks>
    ///     A project that has not set set 1 up should draw without it, not fail to draw. The shape
    ///     every optional piece of the compositor has.
    /// </remarks>
    [Fact]
    public void An_unconfigured_block_binds_nothing() {
        using var constants = new ViewConstants(device);
        var list = device.BeginCommandList();

        Assert.False(constants.IsConfigured);
        Assert.False(constants.Bind(list, View("camera", 1f)));
        Submit(list);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));
    }

    /// <summary>Forgetting a view gives its buffer back.</summary>
    [Fact]
    public void Forgetting_a_view_returns_its_buffer() {
        var before = device.LiveResourceCount;
        using var constants = Constants();
        var list = device.BeginCommandList();
        var view = View("gone", 1f);

        allocator.BeginFrame();
        constants.Bind(list, view);

        Assert.True(device.LiveResourceCount > before);
        Assert.True(constants.Forget(view));
        Assert.False(constants.Forget(view));
        Assert.Equal(0, constants.Count);
    }
}
