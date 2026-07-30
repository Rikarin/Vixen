// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>Per-pixel irradiance out of the resolved screen probes — doc 19 § L3's upsample, first form.</summary>
/// <remarks>
///     <para>
///         <strong>The screen-space sibling of <see cref="IndirectDiffuseRenderer" />.</strong> That
///         one asks a world-space field what arrives at a position; this one asks the probe lattice
///         what arrives at a pixel, out of the four planes <see cref="ScreenProbeResolve" /> wrote.
///         What comes out is the same kind of buffer: irradiance over π, for a shading pass to
///         multiply by albedo.
///     </para>
///     <para>
///         <strong>Bilinear only, deliberately.</strong> The bilateral half — weights that also
///         respect depth and normal edges — is the denoiser's opening move and lands with it, against
///         frames this version is the baseline for.
///     </para>
///     <para>
///         ⚠ <strong>The pass compiles and its conventions are pinned; a frame has not drawn it
///         yet.</strong> The image test through a real compositor is the next increment, together with
///         probe placement from the real depth buffer — this node exists so that increment has
///         something to schedule.
///     </para>
/// </remarks>
public sealed class ScreenProbeUpsampleRenderer() : PostEffectRenderer(
    ScreenProbeUpsampleKeys.ShaderName,
    [],
    ScreenProbeUpsampleKeys.ConstantBufferBinding
) {
    /// <summary>The depth that says which pixels show the sky.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals the probes are asked about.</summary>
    public required string Normals { get; init; }

    /// <summary>The probes to upsample — the mirror whose planes the resolve wrote.</summary>
    public required ScreenProbeTexture Probes { get; init; }

    /// <summary>A multiplier on the result, for <see cref="IndirectDiffuseRenderer.Intensity" />'s reasons.</summary>
    public float Intensity { get; set; } = 1f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        var layout = Probes.Probes.Layout;

        parameters.Set(ScreenProbeUpsampleKeys.TileSize, (float)layout.TileSize);

        // The host's integer halving, deliberately — the same tie the anchor pixel takes, so the two
        // sides cannot round it differently.
        parameters.Set(ScreenProbeUpsampleKeys.LatticeOrigin, (layout.TileSize / 2) + 0.5f);
        parameters.Set(ScreenProbeUpsampleKeys.GridSize, new Vector2(layout.GridSize.X, layout.GridSize.Y));
        parameters.Set(ScreenProbeUpsampleKeys.Viewport, new Vector2(layout.Viewport.X, layout.Viewport.Y));
        parameters.Set(ScreenProbeUpsampleKeys.Intensity, Intensity);

        Probes.Apply(parameters);

        Read(bindings, ScreenProbeUpsampleKeys.DepthBufferBinding, Depth);
        Read(bindings, ScreenProbeUpsampleKeys.NormalBufferBinding, Normals);

        // Point, for IndirectDiffuse's reason: a bilinear tap of a depth buffer is a surface that
        // exists nowhere.
        Sample(bindings, ScreenProbeUpsampleKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
