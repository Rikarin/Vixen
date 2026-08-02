// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>What one cell's bake is handed: where it is drawing, and what camera it is drawing with.</summary>
/// <param name="Cell">Which cell of the grid.</param>
/// <param name="View">The orthographic camera for that direction.</param>
/// <param name="Viewport">Where in the atlas it lands, padding excluded.</param>
public readonly record struct ImpostorBakeCell(ImpostorCell Cell, ImpostorView View, Viewport Viewport);

/// <summary>
///     Rendering a mesh once from every direction of an <see cref="ImpostorGrid" /> into one atlas.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T7]'s owed item.</b> <see cref="ImpostorGrid" />,
///         <see cref="ImpostorAtlas" /> and <see cref="ImpostorView" /> are the arithmetic — the fold,
///         the layout and the per-cell camera — and this is the pass that uses them. It owns the atlas
///         textures and the depth target, and it records one render pass with a viewport per cell.
///     </para>
///     <para>
///         ⚠ <b>One render pass for the whole atlas, not one per cell.</b> A 9×9 grid is eighty-one
///         cells; a pass each would clear and store a 1152-texel target eighty-one times, which on a
///         tiler is eighty-one full-frame resolves to bake one tree. The clear happens once and the
///         viewport moves.
///     </para>
///     <para>
///         ⚠ <b>It does not know what a mesh is, and that is the seam.</b> The caller draws — it owns
///         the pipeline, the vertex and index buffers and the material — and what this supplies is the
///         camera and the rectangle. A baker that bound a mesh would need an asset database in a class
///         whose job is a render pass.
///     </para>
///     <para>
///         ⚠ <b>The draw rectangle is inset by the padding, and the gutter is left for the dilation
///         to fill.</b> A bake that used the whole cell would put the silhouette's edge texels against
///         the neighbouring cell's, and a bilinear tap at four hundred metres reaches across — which
///         is a tree wearing a stripe of the tree next to it.
///     </para>
///     <para>
///         ⚠ <b>Depth is cleared to zero, which is <em>far</em>.</b> The engine's convention is
///         reversed-Z; clearing to one here is the classic mistake and produces an atlas that depth
///         tests away entirely — eighty-one blank cells and no error anywhere.
///     </para>
/// </remarks>
public sealed class ImpostorBake : IDisposable {
    readonly IGraphicsDevice device;

    readonly TextureHandle albedo;
    readonly TextureHandle normal;
    readonly TextureHandle depth;

    readonly TextureViewHandle albedoView;
    readonly TextureViewHandle normalView;
    readonly TextureViewHandle depthView;

    readonly TextureViewHandle[] albedoLevels;
    readonly TextureViewHandle[] normalLevels;

    DescriptorSetLayoutHandle setLayout;
    PipelineLayoutHandle pipelineLayout;
    PipelineHandle dilating;
    PipelineHandle reducing;
    BufferHandle constants;
    DescriptorSetHandle[] sets = [];

    bool disposed;

    /// <summary>Creates the atlas textures a bake renders into.</summary>
    /// <param name="device">The device.</param>
    /// <param name="atlas">The layout — the grid, the cell size and the gutter.</param>
    /// <param name="colourFormat">What the albedo atlas is stored as.</param>
    /// <param name="normalFormat">And the normal atlas.</param>
    /// <param name="depthFormat">And the depth target the bake tests against.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ImpostorBake(
        IGraphicsDevice device,
        ImpostorAtlas atlas,
        PixelFormat colourFormat = PixelFormat.Rgba8UNorm,
        PixelFormat normalFormat = PixelFormat.Rgba8UNorm,
        PixelFormat depthFormat = PixelFormat.Depth32Float
    ) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;

        Atlas = atlas;

        var size = atlas.Resolution;

        // ⚠ The mip count is the atlas's own, which is how many levels are *safe* rather than how
        // many fit. A mip that mixes two cells is the bleed the gutter exists to stop, arriving
        // through a different door.
        albedo = device.CreateTexture(
            new(
                colourFormat,
                size,
                size,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                MipLevels: atlas.MipLevels,
                Name: "impostor albedo"
            )
        );

        // Two targets rather than one, because an impostor without normals is a flat cut-out: the
        // far field still receives the sun, and a billboard that cannot answer "which way am I
        // facing" shades as a card.
        normal = device.CreateTexture(
            new(
                normalFormat,
                size,
                size,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                MipLevels: atlas.MipLevels,
                Name: "impostor normals"
            )
        );

        depth = device.CreateTexture(
            new(depthFormat, size, size, TextureUsage.DepthStencilTarget, Name: "impostor depth")
        );

        albedoView = device.CreateTextureView(albedo);
        normalView = device.CreateTextureView(normal);
        depthView = device.CreateTextureView(depth);

        // ⚠ A view per level, because a storage image is a view of *one* level and the reduce writes
        // a different one each dispatch. A single view of the whole chain would make every dispatch
        // write level 0, which is a chain of identical levels — invisible until something minifies.
        albedoLevels = new TextureViewHandle[atlas.MipLevels];
        normalLevels = new TextureViewHandle[atlas.MipLevels];

        for (var level = 0; level < atlas.MipLevels; level++) {
            albedoLevels[level] = device.CreateTextureView(albedo, baseMipLevel: level, mipLevelCount: 1);
            normalLevels[level] = device.CreateTextureView(normal, baseMipLevel: level, mipLevelCount: 1);
        }
    }

    /// <summary>The layout this bake fills.</summary>
    public ImpostorAtlas Atlas { get; }

    /// <summary>The albedo atlas, for the material that draws the impostor.</summary>
    public TextureViewHandle Albedo => albedoView;

    /// <summary>And the normals.</summary>
    public TextureViewHandle Normals => normalView;

    /// <summary>How many cells the last <see cref="Record" /> drew.</summary>
    public int CellsBaked { get; private set; }

    /// <summary>Where a cell's mesh is drawn, in atlas pixels.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>The viewport.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="ImpostorAtlas.RectOf" /> already excludes the gutter, and adding the
    ///     padding again here is the mistake this line exists to not make.</b> A double inset draws
    ///     the tree into the middle four-fifths of its cell, which is not wrong enough to look wrong —
    ///     it is a silhouette a few per cent small, uniformly, which reads as the impostor being at a
    ///     slightly different distance than the mesh it replaces.
    /// </remarks>
    public Viewport ViewportOf(ImpostorCell cell) {
        var (x, y, width, height) = Atlas.RectOf(cell);

        return new(x, y, width, height);
    }

    /// <summary>And the scissor that goes with it, so a draw cannot spill into the gutter.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>The rectangle.</returns>
    /// <remarks>
    ///     A viewport transforms and a scissor rejects, and a mesh whose vertices leave the unit cube
    ///     — which is every mesh whose bounding sphere was underestimated — is clipped by the second
    ///     and not the first.
    /// </remarks>
    public ScissorRect ScissorOf(ImpostorCell cell) {
        var (x, y, width, height) = Atlas.RectOf(cell);

        return new(x, y, width, height);
    }

    /// <summary>Records the whole bake: one render pass, one viewport and one draw per cell.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="centre">The mesh's centre, in its own space.</param>
    /// <param name="radius">How far it reaches from there — the bounding sphere's radius.</param>
    /// <param name="draw">What to draw for one cell. Called once per cell, in row-major order.</param>
    /// <returns>How many cells were drawn.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or no draw.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The bake is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>One radius for every cell, from the bounding sphere.</b> Fitting each view's own
    ///     extent would pack the atlas better and would make the impostor breathe as the blend moves
    ///     between cells, because the same vertex would be a different number of texels from the
    ///     centre in each.
    /// </remarks>
    public int Record(ICommandList commands, Vector3 centre, float radius, Action<ICommandList, ImpostorBakeCell> draw) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ObjectDisposedException.ThrowIf(disposed, this);

        CellsBaked = 0;

        // Transparent black, because what is not the mesh has to be nothing rather than a colour:
        // the alpha is the silhouette, and a cell cleared to opaque anything draws a square.
        commands.BeginRenderPass(
            new(
                [
                    new(albedoView, LoadAction.Clear, StoreAction.Store, new(0f, 0f, 0f, 0f)),
                    new(normalView, LoadAction.Clear, StoreAction.Store, new(0.5f, 0.5f, 1f, 0f))
                ],
                new DepthStencilAttachment(depthView, ClearDepth: 0f),
                "impostor bake"
            )
        );

        for (var z = 0; z < Atlas.Grid.Side; z++) {
            for (var x = 0; x < Atlas.Grid.Side; x++) {
                var cell = new ImpostorCell(x, z);

                commands.SetViewport(ViewportOf(cell));
                commands.SetScissor(ScissorOf(cell));

                draw(commands, new(cell, ImpostorView.For(Atlas.Grid, cell, centre, radius), ViewportOf(cell)));

                CellsBaked++;
            }
        }

        commands.EndRenderPass();

        return CellsBaked;
    }

    /// <summary>Gives the bake the shaders that finish an atlas: the dilation and the reduce.</summary>
    /// <param name="dilate">The <c>Reduce = false</c> variant's compute stage.</param>
    /// <param name="reduce">And the <c>Reduce = true</c> one's.</param>
    /// <exception cref="ArgumentException">A shader is not valid.</exception>
    /// <exception cref="ObjectDisposedException">The bake is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>Separate from the constructor, because a bake without them is still a bake.</b> The
    ///     atlas is legible with one level and an empty gutter — it is what the pass before this one
    ///     produces — so a caller that only wants the photographs should not have to compile two
    ///     compute variants to get them.
    /// </remarks>
    public void Finishing(ShaderHandle dilate, ShaderHandle reduce) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!dilate.IsValid || !reduce.IsValid) {
            throw new ArgumentException(
                "Finishing an impostor atlas needs both compute stages — the dilation and the reduce.",
                dilate.IsValid ? nameof(reduce) : nameof(dilate)
            );
        }

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(ImpostorFinishKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(ImpostorFinishKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
                    new(ImpostorFinishKeys.TargetBinding, DescriptorKind.StorageTexture, ShaderStage.Compute)
                ],
                "impostor finish"
            )
        );

        pipelineLayout = device.CreatePipelineLayout(new([setLayout], [], "impostor finish"));
        dilating = device.CreateComputePipeline(new(dilate, pipelineLayout, "impostor dilate"));
        reducing = device.CreateComputePipeline(new(reduce, pipelineLayout, "impostor reduce"));

        constants = device.CreateBuffer(
            new(
                ImpostorFinishKeys.ConstantBufferSize,
                BufferUsage.Uniform,
                MemoryAccess.HostUpload,
                "impostor finish constants"
            )
        );

        // ⚠ A set per dispatch rather than one rebound between them. A descriptor set written twice
        // in one command list is a set the second dispatch reads while the first is still using it —
        // and the two dispatches here are the two atlases times every level of the chain.
        sets = new DescriptorSetHandle[2 * Atlas.MipLevels];

        for (var index = 0; index < sets.Length; index++) {
            sets[index] = device.CreateDescriptorSet(setLayout, $"impostor finish {index}");
        }
    }

    /// <summary>Records the dilation and the whole mip chain, for both atlases.</summary>
    /// <param name="commands">Where to record.</param>
    /// <returns>How many dispatches were recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finishing" /> was never called.</exception>
    /// <exception cref="ObjectDisposedException">The bake is gone.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Dilate first, then reduce, and the order is the whole point.</b> Reducing an
    ///         undilated level averages the silhouette's edge with transparent black, so the fringe
    ///         the dilation exists to remove is baked into every level below — and each level halves
    ///         it into a wider band. Dilating afterwards would fix level 0 and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A dispatch per level rather than one with a loop</b>, for <c>HiZReduce</c>'s
    ///         reason: a level cannot be read until the whole of the level above it is written, and a
    ///         workgroup can only wait for itself. The barriers between them are recorded here.
    ///     </para>
    /// </remarks>
    public int Finish(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!dilating.IsValid) {
            throw new InvalidOperationException(
                "An impostor atlas cannot be finished before `Finishing` has given it the two shaders."
            );
        }

        var dispatches = 0;

        dispatches += Chain(commands, albedoLevels, 0);
        dispatches += Chain(commands, normalLevels, Atlas.MipLevels);

        Dispatches = dispatches;

        return dispatches;
    }

    /// <summary>How many dispatches the last <see cref="Finish" /> recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Dilates one atlas's level 0, then reduces the whole chain onto itself.</summary>
    int Chain(ICommandList commands, TextureViewHandle[] levels, int firstSet) {
        var barrier = new TextureBarrier[1];
        var dispatches = 0;

        for (var level = 0; level < Atlas.MipLevels; level++) {
            var reduce = level > 0;
            var size = Atlas.Resolution >> level;
            var cell = Math.Max(Atlas.CellSize >> level, 1);
            var set = sets[firstSet + level];

            // ⚠ The gutter shrinks with the level, and rounding it *up* is what keeps the fringe out:
            // at level three a four-texel gutter is half a texel, and a dilation of zero texels is no
            // dilation at all. It only ever runs at level 0 here, and the number is written anyway so
            // that a caller reading the block back sees what the level meant.
            var block = new byte[ImpostorFinishKeys.ConstantBufferSize];

            new ImpostorFinishConstants {
                CellSize = cell,
                Padding = Math.Max((Atlas.Padding + (1 << level) - 1) >> level, 1)
            }.Write(block);

            device.Write(constants, 0, block);

            device.UpdateDescriptorSet(
                set,
                [
                    DescriptorWrite.Uniform(ImpostorFinishKeys.ConstantBufferBinding, constants),
                    DescriptorWrite.Texture(ImpostorFinishKeys.SourceBinding, levels[Math.Max(level - 1, 0)]),
                    DescriptorWrite.StorageImage(ImpostorFinishKeys.TargetBinding, levels[level])
                ]
            );

            barrier[0] = new(
                default,
                reduce ? ResourceState.ShaderWrite : ResourceState.ColourTarget,
                ResourceState.ShaderWrite
            );

            commands.Barrier(new([], barrier));
            commands.BindPipeline(reduce ? reducing : dilating);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);

            var groups = (Math.Max(size, 1) + 7) / 8;

            commands.Dispatch(groups, groups);

            dispatches++;
        }

        return dispatches;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in sets) {
            device.Destroy(set);
        }

        if (dilating.IsValid) {
            device.Destroy(constants);
            device.Destroy(reducing);
            device.Destroy(dilating);
            device.Destroy(pipelineLayout);
            device.Destroy(setLayout);
        }

        foreach (var view in albedoLevels) {
            device.Destroy(view);
        }

        foreach (var view in normalLevels) {
            device.Destroy(view);
        }

        device.Destroy(depthView);
        device.Destroy(normalView);
        device.Destroy(albedoView);
        device.Destroy(depth);
        device.Destroy(normal);
        device.Destroy(albedo);
    }
}
