// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     A depth pyramid: last frame's depth buffer, min-reduced level by level, for
///     <see cref="GpuVisibilityGroup" /> to occlusion-test against.
/// </summary>
/// <remarks>
///     <para>
///         The device half of <c>Raven/Library/Pipeline/HiZReduce.rvn</c>. It owns one mip-chained
///         <see cref="PixelFormat.R32Float" /> texture and records a dispatch per level into a list
///         the caller supplies — the same division <see cref="VfxGpuSimulation" /> makes, and for the
///         same reason: what to submit and when is the frame's business, not a resource's.
///     </para>
///     <para>
///         <strong>Level 0 is half the depth buffer, not a copy of it.</strong> The first dispatch
///         reduces the depth texture rather than copying it, which removes a whole full-resolution
///         level from both the memory and the chain, and costs nothing in precision: an occlusion
///         test that needed the exact depth of a single pixel would be a test of something too small
///         to cull.
///     </para>
///     <para>
///         <strong>Minimum, because depth is reversed.</strong> Near is 1 and far is 0, so the
///         smallest value in a tile is the furthest surface drawn in it — which is the only number a
///         conservative "is this behind everything" test can use. Reducing by maximum, or by average,
///         culls objects that were visible.
///     </para>
///     <para>
///         <strong>It is persistent, and that is why it is not a graph resource.</strong> The
///         pyramid is written at the end of one frame and read at the start of the next, so a
///         transient the graph aliases would be somebody else's pixels by the time the culler
///         sampled it. What <em>is</em> a graph resource is the depth it reduces, which is why
///         <see cref="Compositor.HiZRenderer" /> exists to declare that read.
///     </para>
/// </remarks>
public sealed class HiZPyramid : IDisposable {
    readonly IGraphicsDevice device;

    TextureHandle texture;
    TextureViewHandle all;
    TextureViewHandle[] sources = [];
    TextureViewHandle[] targets = [];
    ResourceState[] states = [];

    // One set per level per frame in flight. Per level because the whole chain is recorded into one
    // list and a set cannot be rewritten between two dispatches that both read it; per frame because
    // the source of level 0 is the caller's depth texture, whose handle a graph may change — and
    // rewriting a set a submitted frame still references is the race DescriptorAllocator exists for.
    DescriptorSetHandle[] sets = [];
    int ring;
    int slots = 1;

    // Where the set goes, from the reflection checked in beside the shader rather than from a name
    // looked up at run time: a binding index is declaration order within a set, so adding an image
    // above another renumbers it — and the generated constant moves with it while a literal does not.
    const DescriptorSetSlot Slot = (DescriptorSetSlot)HiZReduceKeys.SourceSet;

    Effect? allocatedFor;
    bool disposed;

    /// <summary>Creates a pyramid on a device. Nothing is allocated until the first build.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public HiZPyramid(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <summary>Where the reduction variant is resolved from. Null builds nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null builds nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Level 0's size in texels — half the depth buffer, rounded down.</summary>
    public Int2 Size { get; private set; }

    /// <summary>How many levels the chain has, or zero before the first build.</summary>
    public int Levels { get; private set; }

    /// <summary>A view of every level, which is what the culling pass samples.</summary>
    public TextureViewHandle View => all;

    /// <summary>Whether a build has completed and the pyramid holds a frame's depth.</summary>
    /// <remarks>
    ///     What tells the culler that testing against this would be testing against nothing: a
    ///     pyramid that has never been built holds undefined texels, and undefined texels in a
    ///     min-reduction occlude the whole scene.
    /// </remarks>
    public bool IsBuilt { get; private set; }

    /// <summary>
    ///     Records the dispatches that reduce a depth buffer into the chain.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="depth">A view of the depth texture, which must already be shader-readable.</param>
    /// <param name="depthSize">The depth texture's size in pixels.</param>
    /// <returns>False when there is nothing to build with, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     The depth texture's state is the caller's: a pass that declared it as a read has already
    ///     been given the barrier for it, which is exactly what <see cref="Compositor.HiZRenderer" />
    ///     arranges. Everything the pyramid owns is transitioned here.
    /// </remarks>
    public bool Build(ICommandList list, TextureViewHandle depth, Int2 depthSize) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Effects is null
            || Pipelines is null
            || !depth.IsValid
            || depthSize.X <= 0
            || depthSize.Y <= 0
            || !GpuCulling.IsSupported(device)) {
            return false;
        }

        if (Effects.Resolve(EffectKey.Of(GpuCulling.ReduceShaderName)) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid || !EnsureTexture(depthSize) || !EnsureSets(effect)) {
            return false;
        }

        ring = (ring + 1) % slots;
        list.BindPipeline(pipeline);

        for (var level = 0; level < Levels; level++) {
            var size = GpuCulling.LevelSize(Size, level);
            var set = sets[(ring * Levels) + level];

            device.UpdateDescriptorSet(
                set,
                [
                    DescriptorWrite.Texture(HiZReduceKeys.SourceBinding, level == 0 ? depth : sources[level - 1]),
                    new(HiZReduceKeys.TargetBinding, DescriptorKind.StorageTexture, TextureView: targets[level])
                ]
            );

            Transition(list, level, ResourceState.ShaderWrite);
            list.BindDescriptorSet(Slot, set);

            list.Dispatch(
                Groups(size.X),
                Groups(size.Y)
            );

            // Back to readable straight away, because the next level's source is this one. The
            // barrier is per level rather than over the whole texture: a barrier covering every mip
            // would order this dispatch against levels it does not touch.
            Transition(list, level, ResourceState.ShaderRead);
        }

        IsBuilt = true;
        return true;
    }

    /// <summary>How many workgroups cover one dimension of a level.</summary>
    static int Groups(int texels) =>
        Math.Max(1, (texels + GpuCulling.ReduceWorkgroupSize - 1) / GpuCulling.ReduceWorkgroupSize);

    void Transition(ICommandList list, int level, ResourceState next) {
        if (states[level] == next) {
            return;
        }

        list.Barrier(new([], [new(texture, states[level], next, level, 1)]));
        states[level] = next;
    }

    /// <summary>Makes the chain, or remakes it when the depth buffer's size changed.</summary>
    /// <remarks>
    ///     Remade rather than resized, because a window drag changes this every frame it is held and
    ///     a texture cannot be resized on any API anyway. The contents are dropped with it, which is
    ///     why <see cref="IsBuilt" /> goes back to false: a pyramid at the wrong size is not a
    ///     stale answer, it is somebody else's.
    /// </remarks>
    bool EnsureTexture(Int2 depthSize) {
        var wanted = new Int2(Math.Max(1, depthSize.X / 2), Math.Max(1, depthSize.Y / 2));

        if (texture.IsValid && wanted == Size) {
            return true;
        }

        DestroyTexture();

        Size = wanted;
        Levels = PixelFormats.MipLevelCount(wanted.X, wanted.Y);

        texture = device.CreateTexture(
            new(
                PixelFormat.R32Float,
                wanted.X,
                wanted.Y,
                TextureUsage.Sampled | TextureUsage.Storage,
                MipLevels: Levels,
                Name: "HiZ"
            )
        );

        if (!texture.IsValid) {
            return false;
        }

        all = device.CreateTextureView(texture);
        sources = new TextureViewHandle[Levels];
        targets = new TextureViewHandle[Levels];
        states = new ResourceState[Levels];

        for (var level = 0; level < Levels; level++) {
            // Two views of one level, because a level is read as a texture and written as an image
            // and the two are different descriptor kinds — not because they see different texels.
            sources[level] = device.CreateTextureView(texture, PixelFormat.Undefined, level, 1);
            targets[level] = device.CreateTextureView(texture, PixelFormat.Undefined, level, 1);
            states[level] = ResourceState.Undefined;
        }

        // The sets point at views that no longer exist.
        DestroySets();
        IsBuilt = false;

        return all.IsValid;
    }

    bool EnsureSets(Effect effect) {
        var wanted = Math.Max(1, device.FramesInFlight);

        if (sets.Length == wanted * Levels && ReferenceEquals(allocatedFor, effect)) {
            return true;
        }

        DestroySets();

        if ((int)Slot >= effect.SetLayouts.Length || !effect.SetLayouts[(int)Slot].IsValid) {
            return false;
        }

        slots = wanted;
        sets = new DescriptorSetHandle[slots * Levels];
        ring = 0;

        for (var index = 0; index < sets.Length; index++) {
            sets[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "HiZ");

            if (!sets[index].IsValid) {
                return false;
            }
        }

        allocatedFor = effect;
        return true;
    }

    void DestroySets() {
        foreach (var set in sets) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        sets = [];
        allocatedFor = null;
    }

    void DestroyTexture() {
        foreach (var view in sources) {
            device.Destroy(view);
        }

        foreach (var view in targets) {
            device.Destroy(view);
        }

        if (all.IsValid) {
            device.Destroy(all);
            all = default;
        }

        if (texture.IsValid) {
            device.Destroy(texture);
            texture = default;
        }

        sources = [];
        targets = [];
        states = [];
        Levels = 0;
        Size = default;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        IsBuilt = false;

        DestroySets();
        DestroyTexture();
    }
}
