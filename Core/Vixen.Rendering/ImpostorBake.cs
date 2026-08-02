// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

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

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(depthView);
        device.Destroy(normalView);
        device.Destroy(albedoView);
        device.Destroy(depth);
        device.Destroy(normal);
        device.Destroy(albedo);
    }
}
