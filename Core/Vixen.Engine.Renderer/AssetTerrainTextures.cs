// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;
using Vixen.Rendering.Terrain;

namespace Vixen.Engine.Renderer;

/// <summary>A terrain's layer textures, out of the asset system.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ITerrainTextures" />'s implementation, which the interface has been waiting
///         for.</b> A <c>.vxlayer</c> names its albedo and its surface map as <c>vx:</c> references;
///         <see cref="AssetTextureSource" /> already knows how to turn one of those into a resident
///         view, decoding off the frame's thread and answering nothing until the bytes are there. All
///         this adds is the parse.
///     </para>
///     <para>
///         ⚠ <b>A reference that does not parse is answered with nothing rather than thrown on.</b>
///         Every layer of every terrain is resolved during <c>Upload</c>, which is inside the frame; a
///         layer somebody typed a path into instead of dropping an asset onto would take the level
///         down. What it does instead is draw as the renderer's white default, which is the same thing
///         a layer with no texture yet looks like — and the layer list in the panel is where the
///         reference is visible.
///     </para>
///     <para>
///         ⚠ <b>The same source as the materials, not one of its own.</b> A terrain layer and a mesh
///         material naming the same rock texture must be one upload; two sources would be two copies
///         of every ground texture in the level, which on a terrain with sixteen layers is the largest
///         avoidable allocation in a frame.
///     </para>
/// </remarks>
public sealed class AssetTerrainTextures : ITerrainTextures {
    readonly AssetTextureSource source;

    /// <summary>Resolves layer references through a texture source.</summary>
    /// <param name="source">Where textures come from. The renderer's own, not a second one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    public AssetTerrainTextures(AssetTextureSource source) {
        ArgumentNullException.ThrowIfNull(source);

        this.source = source;
    }

    /// <summary>How many references have been asked for that did not parse.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter that tells "the texture has not loaded yet" from "that is not a
    ///     reference".</b> Both draw white, and only one of them ever stops.
    /// </remarks>
    public int Unparsed { get; private set; }

    /// <inheritdoc />
    public TextureViewHandle Resolve(string reference) {
        if (string.IsNullOrEmpty(reference)) {
            return default;
        }

        if (!AssetReference.TryParse(reference, out var parsed)) {
            Unparsed++;
            return default;
        }

        return source.TryGet(parsed, out var view) ? view : default;
    }
}
