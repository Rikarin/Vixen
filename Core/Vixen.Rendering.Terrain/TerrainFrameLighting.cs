// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     The frame's lighting, as the ground's lit shaders bind it — one instance per node, refilled
///     every frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>Copied out of the frame rather than reached into it, deliberately.</b> The values are
///         the ones the scene pass reads — the sun <c>SceneLighting</c> extracts, the cascades
///         <c>ShadowMapRenderer.Publish</c> writes, the cluster buffers the lighting feature and the
///         pass publish — but the terrain stack fills one self-contained descriptor set by hand,
///         with ring-offset bindings <c>EffectSetWriter</c> cannot express, so what crosses is plain
///         values in the lit block's own generated types. <see cref="TerrainSceneRenderer" /> is the
///         one filler; <see cref="TerrainRenderer" /> and <see cref="GrassDrawPass" /> are the
///         consumers.
///     </para>
///     <para>
///         ⚠ <b>The cascade array is always four — <c>FrameShadow</c>'s fixed count.</b> A frame
///         that fitted fewer pads the tail with zero matrices, which the containment test can never
///         select because their <c>w</c> row is zero, and repeats the last real split so the
///         fade-out distance stays the frame's own.
///     </para>
/// </remarks>
sealed class TerrainFrameLighting {
    /// <summary>The direction light travels — away from the sun. The scene pass's uniform, verbatim.</summary>
    public Vector3 LightDirection { get; set; }

    /// <summary>The sun's radiance, premultiplied by intensity.</summary>
    public Vector3 LightColor { get; set; }

    /// <summary>Where the camera is, in world space.</summary>
    public Vector3 ViewPosition { get; set; }

    /// <summary>World to view — the frame camera's matrix, which the cascades and the grid were built in.</summary>
    public Matrix4x4 View { get; set; }

    /// <summary>The sun's cascades, near first, tile-folded — padded degenerate past the fitted count.</summary>
    public TerrainLitCascadesElement[] Cascades { get; } = new TerrainLitCascadesElement[4];

    /// <summary>The atlas's texel, for the PCF taps.</summary>
    public Vector2 ShadowTexelSize { get; set; }

    /// <summary>The frame's constant shadow bias, in metres.</summary>
    public float ShadowConstantBias { get; set; }

    /// <summary>And its slope-scaled one.</summary>
    public float ShadowSlopeBias { get; set; }

    /// <summary>Over what distance shadows fade out at the last cascade's end.</summary>
    public float ShadowFadeRange { get; set; }

    /// <summary>The sky's diffuse irradiance, projected into nine coefficients.</summary>
    public Lighting.ShCoefficients EnvironmentSh { get; set; }

    /// <summary>The sky's ambient scale — <c>EnvironmentLight.Intensity</c>.</summary>
    public float AmbientIntensity { get; set; }

    /// <summary>The froxel grid's half-tangents — the culler's own numbers.</summary>
    public Vector2 TanHalfFov { get; set; }

    /// <summary>The camera's near plane, for the grid's slice arithmetic.</summary>
    public float NearPlane { get; set; }

    /// <summary>And its far plane.</summary>
    public float FarPlane { get; set; }

    /// <summary>The cascade atlas the frame rendered, resolved from the graph this frame.</summary>
    public TextureViewHandle ShadowAtlas { get; set; }

    /// <summary>The scene's whole light list, or invalid while the frame has not published one.</summary>
    public BufferHandle LightBuffer { get; set; }

    /// <summary>One list per cluster, or invalid while the frame does not cull its lights.</summary>
    public BufferHandle Clusters { get; set; }
}
