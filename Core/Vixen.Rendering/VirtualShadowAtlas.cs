// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     The device side of a virtual shadow map: the physical atlas, the page table, and the pass that
///     asks the frame which pages it needs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The atlas outlives the frame, and that is the whole point.</b> Every other target in this
///         renderer is a graph transient — declared, aliased and forgotten — because its contents are
///         about one frame. A shadow page's contents are about the <em>world</em>: a page whose slot it
///         still holds, whose level's projection did not move, and whose casters did not move, holds
///         depths that are still correct, and redrawing it is the cost a cascade pays four times a
///         frame for nothing. So this owns its texture, and <see cref="VirtualShadowPages" /> owns the
///         bookkeeping that says which pages are still true.
///     </para>
///     <para>
///         <b>The marks come back a frame late, exactly as the cluster traversal's page requests do.</b>
///         What writes them is a dispatch this host has submitted and not waited for, so reading them
///         where they were written would be the one stall a GPU-driven frame is arranged to avoid.
///         Reading them at the head of the next frame costs a page a frame of latency, which is a
///         surface briefly shadowed by the cascades instead — see
///         <c>ClusteredShading.Shadow</c>, which falls through when a page is not there.
///     </para>
///     <para>
///         <b>A bitset rather than a request list</b>, for the reason <c>VirtualShadowMark.rvn</c>
///         gives: the whole address space is sixteen maps of a thousand and twenty-four pages, so one
///         bit each is two kilobytes of readback — against a compacted list that would need an atomic
///         per pixel and a deduplication pass after it. A million pixels asking for the same page is
///         one <c>atomicOr</c> that changes nothing.
///     </para>
/// </remarks>
public sealed class VirtualShadowAtlas : IDisposable {
    /// <summary>How many words the mark bitset occupies.</summary>
    public const int MarkWords = VirtualShadowMap.MaxPages / 32;

    /// <summary>The format the physical page atlas is.</summary>
    /// <remarks>
    ///     Depth rather than a colour target, because what a page holds is what a depth-only caster
    ///     pass writes — the same arrangement the cascade atlas has, and for the same reason a shadow
    ///     pass binds no colour attachment at all: bandwidth spent on a value nobody reads is the most
    ///     expensive mistake available on a tiler.
    /// </remarks>
    public const PixelFormat Format = PixelFormat.Depth32Float;

    readonly IGraphicsDevice device;
    readonly UploadBuffer<VirtualShadowLevel> levels = new("VirtualShadow.Levels");
    readonly UploadBuffer<uint> pageTable = new("VirtualShadow.PageTable");
    readonly DescriptorWrite[] writes = new DescriptorWrite[4];
    readonly uint[] marks = new uint[MarkWords];

    DescriptorSetHandle[] descriptors = [];
    int ring;

    TextureHandle atlas;
    TextureViewHandle atlasView;
    BufferHandle markBuffer;
    BufferHandle markReadback;
    BufferHandle zeros;
    bool atlasReady;

    Effect? allocatedFor;
    EffectConstants? constants;
    int levelCount;
    int clipmapLevels;
    bool disposed;

    const DescriptorSetSlot Slot = (DescriptorSetSlot)VirtualShadowMarkKeys.SceneDepthSet;

    /// <summary>Creates an atlas on a device.</summary>
    /// <param name="device">The device.</param>
    /// <param name="pagesPerSide">How many pages the physical atlas is on a side.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public VirtualShadowAtlas(IGraphicsDevice device, int pagesPerSide = 32) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        Pages = new(pagesPerSide);
        Residency = new(Pages, (long)Pages.SlotCount * VirtualShadowPages.PageBytes);

        levels.Device = device;
        pageTable.Device = device;
    }

    /// <summary>The physical pages and the table that finds them.</summary>
    public VirtualShadowPages Pages { get; }

    /// <summary>
    ///     What decides which pages are allocated — <see cref="PageResidency" />, shared with geometry.
    /// </summary>
    /// <remarks>
    ///     <b>Improvement 6 of <c>docs/plan/22-virtualized-geometry.md</c>, made concrete.</b> One service
    ///     with one request queue, one budget, one eviction order and one set of counters, serving a
    ///     consumer whose pages are <em>drawn</em> rather than read. That the same class serves both
    ///     without a shadow-shaped hook in it is the claim the seam was drawn to make.
    /// </remarks>
    public PageResidency Residency { get; }

    /// <summary>Where the marking variant is resolved from. Null marks nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null marks nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>How many world units one texel of clipmap level zero covers.</summary>
    public float FirstTexel { get; private set; }

    /// <summary>How many maps this frame has, and how many of them are the clipmap's.</summary>
    public int LevelCount => levelCount;

    /// <summary>How many of them are the directional clipmap's, which are always first.</summary>
    public int ClipmapLevels => clipmapLevels;

    /// <summary>How many pages the last <see cref="ServiceMarks" /> saw asked for.</summary>
    public int MarkedPages { get; private set; }

    /// <summary>
    ///     How many of <see cref="MarkedPages" /> the table this frame published actually answers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one number that says whether the map is working, and the only one that does.</b>
    ///         Every other counter here is about what the host attempted — pages marked is the lookup
    ///         asking, pages drawn is the caster stage answering, residency is the pool holding. None
    ///         of them is <em>coverage</em>: a frame can mark a hundred pages, draw its full budget and
    ///         hold a full pool while the shading pass still finds nothing at the address it looks up,
    ///         because a page allocated and not yet drawn is deliberately absent from
    ///         <see cref="VirtualShadowPages.Table" /> and an invalidated one is unpublished outright.
    ///         What the pixel gets then is <c>ClusteredShading.Shadow</c>'s cascade fallback, which
    ///         renders — so the failure is invisible in a picture and legible only here.
    ///     </para>
    ///     <para>
    ///         Measured at <see cref="UploadTable" />, which is the moment the table becomes the one
    ///         this frame's shading reads, against the marks <see cref="ServiceMarks" /> read at the
    ///         head of the same frame. Those marks are a frame late by construction — see the class
    ///         remarks — so this is "what the previous frame's pixels asked for, against what this
    ///         frame can answer", which is as close as a CPU counter gets to what the shading pass
    ///         sees without a second readback.
    ///     </para>
    /// </remarks>
    public int AnsweredPages { get; private set; }

    /// <summary>How many it does not — the pages this frame shades from the cascades instead.</summary>
    public int AbsentPages => MarkedPages - AnsweredPages;

    /// <summary>The atlas, for a pass that draws into it.</summary>
    public TextureHandle Texture => atlas;

    /// <summary>Its view.</summary>
    public TextureViewHandle View => atlasView;

    /// <summary>Whether the atlas has been created and cleared at least once.</summary>
    public bool IsBuilt => atlas.IsValid && atlasReady;

    /// <summary>The effect key selecting the marking pass.</summary>
    public static EffectKey Key =>
        EffectKey.Of(VirtualShadowMarkKeys.ShaderName, [], Materials.MaterialCompiler.PassComposition());

    /// <summary>Sets this frame's maps and uploads them.</summary>
    /// <param name="records">The clipmap levels first, then one per shadowed spot light.</param>
    /// <param name="clipmap">How many of them are the clipmap's.</param>
    /// <param name="firstTexel">How many world units one texel of clipmap level zero covers.</param>
    /// <exception cref="ArgumentOutOfRangeException">There are more maps than the address space holds.</exception>
    public void Begin(ReadOnlySpan<VirtualShadowLevel> records, int clipmap, float firstTexel) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(records.Length, VirtualShadowMap.MaxLevels);

        levelCount = records.Length;
        clipmapLevels = Math.Clamp(clipmap, 0, records.Length);
        FirstTexel = firstTexel;

        // One entry rather than none, for the reason PunctualShadowRenderer keeps one: the buffer is a
        // binding of the frame's set, a set is written wholly or not at all, and a frame with no maps
        // in it would otherwise fail to bind and draw nothing at all.
        levels.Begin();
        levels.Add(records.IsEmpty ? [default] : records);
        levels.Upload();
    }

    /// <summary>Hands the page table to the device, as the shading pass will read it.</summary>
    /// <remarks>
    ///     Also where <see cref="AnsweredPages" /> is counted, and it has to be here: this is the
    ///     instant the table stops changing and becomes the one the frame's shading reads, so the
    ///     intersection of it with the marks is what the lookup will find and not an estimate of it.
    /// </remarks>
    public void UploadTable() {
        ObjectDisposedException.ThrowIf(disposed, this);

        var table = Pages.Table;

        AnsweredPages = 0;

        for (var word = 0; word < marks.Length; word++) {
            var bits = marks[word];

            while (bits != 0) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                var page = (word * 32) + bit;

                bits &= bits - 1;

                if (page < table.Length && table[page] != VirtualShadowMap.PageAbsent) {
                    AnsweredPages++;
                }
            }
        }

        pageTable.Begin();
        pageTable.Add(table);
        pageTable.Upload();
    }

    /// <summary>
    ///     Records the marking dispatch, and clears the bitset it is about to fill.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="depth">The frame's depth, as the camera left it.</param>
    /// <param name="camera">The view whose depth it is.</param>
    /// <param name="size">The screen, in pixels.</param>
    /// <returns>Whether the dispatch was recorded.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     ⚠ Must be recorded <em>after</em> whatever wrote the depth and before the page draws that
    ///     answer it. Nothing here can check either — an open command list does not say what has been
    ///     recorded into it — which is why <c>VirtualShadowRenderer</c> owns the
    ///     ordering, exactly as <see cref="Compositor.VisibilityBufferRenderer" /> owns the binning's.
    /// </remarks>
    public bool Mark(ICommandList list, TextureViewHandle depth, RenderView camera, Int2 size) {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Effects is null || Pipelines is null || levelCount == 0 || !depth.IsValid || size.X <= 0 || size.Y <= 0) {
            return false;
        }

        if (Effects.Resolve(Key) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid || !EnsureBuffers() || !EnsureDescriptors(effect)) {
            return false;
        }

        ring = (ring + 1) % Math.Max(descriptors.Length, 1);
        Bind(effect, depth, camera, size);

        list.Barrier(new([new(markBuffer, ResourceState.ShaderWrite, ResourceState.CopyDestination)], []));
        list.CopyBuffer(zeros, 0, markBuffer, 0, MarkWords * sizeof(uint));
        list.Barrier(new([new(markBuffer, ResourceState.CopyDestination, ResourceState.ShaderWrite)], []));

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);
        // One workgroup per VirtualShadowMap.MarkTile square, which is eight blocks of
        // VirtualShadowMap.MarkBlock pixels each way — the tiling VirtualShadowMark.Main derives from
        // its group index. Rounded up, so the screen's right and bottom edges are covered by a
        // workgroup whose out-of-range pixels read nothing.
        list.Dispatch(
            (size.X + VirtualShadowMap.MarkTile - 1) / VirtualShadowMap.MarkTile,
            (size.Y + VirtualShadowMap.MarkTile - 1) / VirtualShadowMap.MarkTile
        );

        list.Barrier(new([new(markBuffer, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
        list.CopyBuffer(markBuffer, 0, markReadback, 0, MarkWords * sizeof(uint));
        list.Barrier(new([new(markBuffer, ResourceState.CopySource, ResourceState.ShaderWrite)], []));

        return true;
    }

    /// <summary>
    ///     Reads the previous frame's marks and asks the residency service for what they named.
    /// </summary>
    /// <param name="maxLoads">How many pages may be allocated this frame.</param>
    /// <returns>How many pages became resident.</returns>
    /// <remarks>
    ///     A frame late, and deliberately — see the class remarks. What is lost by being late is a page
    ///     of latency on newly revealed geometry, which the cascade fallback covers.
    /// </remarks>
    public int ServiceMarks(int maxLoads = 16) {
        ObjectDisposedException.ThrowIf(disposed, this);

        MarkedPages = 0;

        if (!markReadback.IsValid) {
            return 0;
        }

        device.Read(markReadback, 0, MemoryMarshal.AsBytes(marks.AsSpan()));

        for (var word = 0; word < marks.Length; word++) {
            var bits = marks[word];

            while (bits != 0) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);

                bits &= bits - 1;
                MarkedPages++;

                Residency.Request(new(VirtualShadowPages.Source, (word * 32) + bit));
            }
        }

        // Touching is what keeps the pages a frame is about to *read* out of the eviction order — the
        // same line UploadResidency has in the geometry path, and the same failure without it: a pool
        // that thrashes hardest on exactly the pages it is using. Owing is the draw-side half of the
        // same statement: Request above ignores a page that is already resident, so a marked page
        // whose slot exists but whose depths were never published — dropped between the take and the
        // draw, or re-queued past what the budget drains — has no voice but this one. Owe puts it at
        // the newest end of the draw queue, where the budget looks first.
        for (var word = 0; word < marks.Length; word++) {
            var bits = marks[word];

            while (bits != 0) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                var page = (word * 32) + bit;

                bits &= bits - 1;
                Residency.Touch(new(VirtualShadowPages.Source, page));
                Pages.Owe(page);
            }
        }

        return Residency.Service(maxLoads);
    }

    /// <summary>Puts what a shading pass needs where it can find it.</summary>
    /// <param name="parameters">The frame's parameters.</param>
    /// <param name="passes">The shader names that compose <c>VirtualShadowLookup</c>.</param>
    /// <param name="samplers">Where the comparison sampler comes from.</param>
    /// <param name="camera">The view whose footprint decides a level.</param>
    /// <param name="height">How many pixels tall that view is.</param>
    /// <param name="constantBias">The constant depth bias, already in a level's normalised depth.</param>
    /// <param name="slopeBias">The slope-scaled depth bias, already in a level's normalised depth.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         By name and per pass, which is <see cref="Compositor.PunctualShadowRenderer" />'s
    ///         arrangement: a composed feature's bindings belong to whichever pass composed it, so the
    ///         same values are written once per pass rather than once for a set nobody owns.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two biases are converted before they arrive here, and until they were
    ///         published at all the lookup used its own declaration's defaults</b> — raw normalised
    ///         depth against a four hundred metre box, which is metres of world. See
    ///         <see cref="VirtualShadowMap.DepthScale" /> for what that cost and
    ///         <see cref="Compositor.VirtualShadowRenderer.ConstantBias" /> for where the metres are
    ///         stated.
    ///     </para>
    /// </remarks>
    public void Publish(
        ParameterCollection parameters,
        IReadOnlyList<string> passes,
        SamplerCache? samplers,
        RenderView camera,
        int height,
        float constantBias,
        float slopeBias
    ) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(camera);

        foreach (var pass in passes) {
            parameters.Set(ParameterKeys.New<BufferHandle>($"{pass}.shadowLevels"), levels.Buffer);
            parameters.Set(ParameterKeys.New<BufferHandle>($"{pass}.shadowPageTable"), pageTable.Buffer);
            parameters.Set(ParameterKeys.New<int>($"{pass}.shadowLevelCount"), levelCount);
            parameters.Set(ParameterKeys.New<int>($"{pass}.shadowClipmapLevels"), clipmapLevels);
            parameters.Set(ParameterKeys.New<float>($"{pass}.shadowFirstTexel"), FirstTexel);
            parameters.Set(ParameterKeys.New<int>($"{pass}.shadowAtlasPages"), Pages.PagesPerSide);
            parameters.Set(ParameterKeys.New<float>($"{pass}.shadowScreenHeightScale"), camera.ScreenHeightScale);
            parameters.Set(ParameterKeys.New<int>($"{pass}.shadowScreenHeight"), height);
            parameters.Set(ParameterKeys.New<float>($"{pass}.shadowPageConstantBias"), constantBias);
            parameters.Set(ParameterKeys.New<float>($"{pass}.shadowPageSlopeBias"), slopeBias);

            if (atlasView.IsValid) {
                parameters.Set(ParameterKeys.New<TextureViewHandle>($"{pass}.shadowPages"), atlasView);
            }

            if (samplers is not null) {
                // Nearest, not linear: the lookup compares after sampling, and a linear sampler
                // blends four depths into a value no surface wrote — see ShadowMapRenderer.Scene
                // for the full argument. Linear filtering of Depth32Float is optional in Vulkan too.
                parameters.Set(ParameterKeys.New<SamplerHandle>($"{pass}.shadowPageSampler"), samplers.PointClamp);
            }
        }
    }

    /// <summary>Creates the atlas, if it does not exist.</summary>
    /// <returns>Whether it does now.</returns>
    public bool EnsureAtlas() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (atlas.IsValid) {
            return atlasView.IsValid;
        }

        atlas = device.CreateTexture(
            new(
                Format,
                Pages.AtlasTexels,
                Pages.AtlasTexels,
                TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
                Name: "VirtualShadow.Atlas"
            )
        );

        atlasView = atlas.IsValid ? device.CreateTextureView(atlas) : default;
        atlasReady = false;

        return atlasView.IsValid;
    }

    /// <summary>Says the atlas has been cleared, so a shading pass may read it.</summary>
    public void MarkCleared() => atlasReady = atlas.IsValid;

    void Bind(Effect effect, TextureViewHandle depth, RenderView camera, Int2 size) {
        writes[0] = DescriptorWrite.Texture(VirtualShadowMarkKeys.SceneDepthBinding, depth);
        writes[1] = DescriptorWrite.Storage(VirtualShadowMarkKeys.LevelsBinding, levels.Buffer);
        writes[2] = DescriptorWrite.Storage(VirtualShadowMarkKeys.MarksBinding, markBuffer);

        constants ??= new(device, "VirtualShadowMark.Constants");

        Matrix4x4.Invert(camera.ViewProjection, out var inverse);

        var parameters = new ParameterCollection();
        parameters.Set(VirtualShadowMarkKeys.InverseViewProjection, inverse);
        parameters.Set(VirtualShadowMarkKeys.ViewPosition, camera.Position);
        parameters.Set(VirtualShadowMarkKeys.ScreenHeightScale, camera.ScreenHeightScale);
        parameters.Set(VirtualShadowMarkKeys.Screen, size);
        parameters.Set(VirtualShadowMarkKeys.LevelCount, levelCount);
        parameters.Set(VirtualShadowMarkKeys.ClipmapLevels, clipmapLevels);
        parameters.Set(VirtualShadowMarkKeys.FirstTexel, FirstTexel);

        var count = constants.Update(effect, parameters) ? 4 : 3;

        if (count == 4) {
            writes[3] = DescriptorWrite.Uniform(
                VirtualShadowMarkKeys.ConstantBufferBinding,
                constants.Buffer,
                constants.Offset,
                constants.Size
            );
        }

        device.UpdateDescriptorSet(descriptors[ring], writes.AsSpan(0, count));
    }

    bool EnsureBuffers() {
        if (markBuffer.IsValid) {
            return true;
        }

        markBuffer = device.CreateBuffer(
            new(
                MarkWords * sizeof(uint),
                BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "VirtualShadow.Marks"
            )
        );

        markReadback = device.CreateBuffer(
            new(
                MarkWords * sizeof(uint),
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                "VirtualShadow.MarkReadback"
            )
        );

        zeros = device.CreateBuffer(
            new(MarkWords * sizeof(uint), BufferUsage.CopySource, MemoryAccess.HostUpload, "VirtualShadow.Zeros")
        );

        if (zeros.IsValid) {
            device.Write(zeros, 0, MemoryMarshal.AsBytes<uint>(new uint[MarkWords]));
        }

        return markBuffer.IsValid && markReadback.IsValid && zeros.IsValid;
    }

    bool EnsureDescriptors(Effect effect) {
        if (ReferenceEquals(allocatedFor, effect) && descriptors.Length > 0) {
            return true;
        }

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        var count = Math.Max(device.FramesInFlight, 1);
        descriptors = new DescriptorSetHandle[count];

        for (var index = 0; index < count; index++) {
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "VirtualShadowMark");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        allocatedFor = effect;
        ring = 0;

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var buffer in (BufferHandle[])[markBuffer, markReadback, zeros]) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        markBuffer = default;
        markReadback = default;
        zeros = default;

        if (atlasView.IsValid) {
            device.Destroy(atlasView);
            atlasView = default;
        }

        if (atlas.IsValid) {
            device.Destroy(atlas);
            atlas = default;
        }

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];

        levels.Dispose();
        pageTable.Dispose();
        Residency.Dispose();

        constants?.Dispose();
        constants = null;
    }
}
