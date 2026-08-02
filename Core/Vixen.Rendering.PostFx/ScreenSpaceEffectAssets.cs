// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>Fast approximate antialiasing, on the graded image.</summary>
/// <remarks>
///     ⚠ <b>Last, and on display-referred colour.</b> FXAA finds edges by luminance contrast, and
///     contrast in scene-referred light is unbounded — a specular highlight next to a shadow is a
///     ratio of thousands, so every threshold in the shader would be meaningless. Put it after the
///     tonemap or it does nothing useful.
/// </remarks>
[DataContract("Fxaa")]
public sealed record FxaaAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The image it antialiases.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Antialiased";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Whether sub-pixel aliasing is smoothed as well as edges.</summary>
    public bool Subpixel { get; init; } = true;

    /// <summary>Contrast below which a pixel is left alone.</summary>
    public float EdgeThreshold { get; init; } = 0.125f;

    /// <summary>The darkest contrast worth looking at, which keeps noise out of shadows.</summary>
    public float EdgeThresholdMinimum { get; init; } = 0.0312f;

    /// <summary>How much of the sub-pixel estimate is applied.</summary>
    public float SubpixelQuality { get; init; } = 0.75f;
}

/// <summary>Temporal antialiasing, against a reprojected history.</summary>
/// <remarks>
///     ⚠ <b>Before the tonemap, unlike <see cref="FxaaAsset" />.</b> TAA blends this frame with the
///     last one, and blending display-referred values is blending two different curves' outputs —
///     which is why a scene that changes exposure ghosts. It also needs a motion-vector texture, and
///     <b>nothing in the engine produces one yet</b>: see docs/plan/30.
/// </remarks>
[DataContract("TemporalAntialiasing")]
public sealed record TemporalAntialiasingAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>This frame's colour.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Where each pixel was last frame.</summary>
    public string MotionVectors { get; init; } = string.Empty;

    /// <summary>The depth, for rejecting history across a silhouette.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The name the result is published under, which is also next frame's history.</summary>
    public string Output { get; init; } = "Resolved";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>Whether history is clipped to the neighbourhood's colour variance.</summary>
    public bool VarianceClipping { get; init; } = true;

    /// <summary>How much of the history survives each frame.</summary>
    public float Feedback { get; init; } = 0.9f;

    /// <summary>How many standard deviations the clip box spans.</summary>
    public float VarianceGamma { get; init; } = 1.25f;
}

/// <summary>An unsharp mask, for the softness a temporal resolve leaves behind.</summary>
[DataContract("Sharpen")]
public sealed record SharpenAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The image it sharpens.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Sharpened";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Whether each channel is sharpened separately rather than by luminance.</summary>
    public bool PerChannel { get; init; } = true;

    /// <summary>How much of the mask is added back.</summary>
    public float Sharpness { get; init; } = 0.5f;
}

/// <summary>Vignette, chromatic aberration and film grain — the lens-imperfection pass.</summary>
/// <remarks>
///     <para>
///         Three effects in one node because they are one shader, and they are one shader because
///         they are always applied together at the very end and each is a few instructions. Three
///         full-screen passes would cost three times the bandwidth to save nothing.
///     </para>
///     <para>
///         ⚠ <b>After the tonemap.</b> All three model the camera and the film rather than the
///         scene. Grain applied to scene-referred light in particular is invisible in shadow and
///         enormous in highlights, because it is a fixed amount added to an unbounded number.
///     </para>
/// </remarks>
[DataContract("Vignette")]
public sealed record VignetteAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The image it degrades.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Lensed";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Whether the corners are darkened.</summary>
    public bool UseVignette { get; init; } = true;

    /// <summary>Whether the channels are offset radially.</summary>
    public bool UseChromaticAberration { get; init; } = true;

    /// <summary>Whether grain is added.</summary>
    public bool UseGrain { get; init; } = true;

    /// <summary>Whether grain scales with darkness, as real film does.</summary>
    public bool LuminanceWeightedGrain { get; init; } = true;

    /// <summary>0 is no darkening, 1 is fully dark at the corners.</summary>
    public float VignetteIntensity { get; init; } = 0.4f;

    /// <summary>How abrupt the falloff is. Higher keeps the centre clear for longer.</summary>
    public float VignetteSmoothness { get; init; } = 0.5f;

    /// <summary>Channel offset at the screen edge, in UV units.</summary>
    public float AberrationStrength { get; init; } = 0.003f;

    /// <summary>How much grain there is.</summary>
    public float GrainIntensity { get; init; } = 0.04f;

    /// <summary>How large one grain is.</summary>
    public float GrainScale { get; init; } = 1f;

    /// <summary>What colour the corners go towards.</summary>
    public Vector3 VignetteColour { get; init; } = Vector3.Zero;

    /// <summary>Where the vignette is centred, in UV. The screen's middle is (0.5, 0.5).</summary>
    public Vector2 VignetteCentre { get; init; } = new(0.5f, 0.5f);

    /// <summary>0 follows the aspect ratio, 1 is a circle whatever shape the screen is.</summary>
    public float VignetteRoundness { get; init; }

    /// <summary>Whether the image is warped radially before anything else reads it.</summary>
    public bool UseLensDistortion { get; init; }

    /// <summary>How far the image is pushed out (positive) or pulled in (negative) at the edges.</summary>
    public float DistortionIntensity { get; init; }

    /// <summary>Per-axis multipliers, so one axis can be left undistorted.</summary>
    public Vector2 DistortionScale { get; init; } = Vector2.One;

    /// <summary>Where the distortion is centred, in UV.</summary>
    public Vector2 DistortionCentre { get; init; } = new(0.5f, 0.5f);

    /// <summary>A zoom after the warp, to hide the border a positive distortion pulls in from.</summary>
    public float DistortionZoom { get; init; } = 1f;
}

/// <summary>Distance and height fog, reconstructed from depth.</summary>
[DataContract("Fog")]
public sealed record FogAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The image it fogs.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The depth it reconstructs world positions from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The view whose camera the reconstruction uses.</summary>
    /// <remarks>
    ///     ⚠ Not optional decoration. Empty leaves an identity matrix and a camera at the origin, so
    ///     every pixel is unprojected to the wrong place — fog that is smooth, plausible, and correct
    ///     for a view nobody is looking through.
    /// </remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Fogged";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>0 linear, 1 exponential, 2 exponential squared.</summary>
    public int Mode { get; init; } = 2;

    /// <summary>Whether density falls off with height.</summary>
    public bool HeightFalloff { get; init; } = true;

    /// <summary>Whether the fog brightens towards the sun.</summary>
    public bool SunScattering { get; init; } = true;

    /// <summary>What colour it is.</summary>
    public Vector3 Colour { get; init; } = new(0.5f, 0.6f, 0.7f);

    /// <summary>How thick it is.</summary>
    public float Density { get; init; } = 0.02f;

    /// <summary>Where linear fog begins, in metres.</summary>
    public float Start { get; init; } = 10f;

    /// <summary>Where linear fog is total, in metres.</summary>
    public float End { get; init; } = 200f;
}

/// <summary>Outlines from depth and normal discontinuities.</summary>
[DataContract("Outline")]
public sealed record OutlineAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The image it draws over.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The depth it finds silhouettes in.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it finds creases in, or empty for depth alone.</summary>
    public string Normals { get; init; } = "";

    /// <summary>A mask naming what to outline, or empty to outline everything.</summary>
    public string SelectionMask { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Outlined";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Whether creases count as edges, not only silhouettes.</summary>
    public bool UseNormals { get; init; } = true;

    /// <summary>Whether only the masked objects are outlined.</summary>
    public bool SelectionOnly { get; init; }

    /// <summary>What colour the line is.</summary>
    public Vector4 Colour { get; init; } = new(1f, 0.6f, 0f, 1f);

    /// <summary>How wide the line is, in pixels.</summary>
    public float Thickness { get; init; } = 1.5f;

    /// <summary>The camera's near plane, for linearising depth.</summary>
    public float NearPlane { get; init; } = 0.1f;

    /// <summary>The camera's far plane.</summary>
    public float FarPlane { get; init; } = 1000f;

    /// <summary>How large a depth step counts as an edge.</summary>
    public float DepthThreshold { get; init; } = 0.02f;
}

/// <summary>Ground-truth-style ambient occlusion, marched in screen space.</summary>
/// <remarks>
///     The cheap half of the ambient story. <c>!DistanceFieldAo</c> is the other one and they answer
///     different questions: this sees only what is on screen, and that sees the whole level.
/// </remarks>
[DataContract("Ssao")]
public sealed record SsaoAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth it marches.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it orients the integral by.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The view whose camera unprojects the depth.</summary>
    /// <remarks>
    ///     ⚠ Empty leaves an identity projection, which unprojects every pixel to the same place —
    ///     occlusion that is smooth and completely wrong.
    /// </remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "AmbientOcclusion";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>How many directions each pixel marches.</summary>
    public int Directions { get; init; } = 8;

    /// <summary>How many steps along each direction.</summary>
    public int Steps { get; init; } = 6;

    /// <summary>Whether a bent normal is written alongside the occlusion.</summary>
    public bool BentNormal { get; init; }

    /// <summary>How far the march reaches, in metres.</summary>
    public float Radius { get; init; } = 0.5f;

    /// <summary>How strongly the result darkens.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>How fast occlusion falls off with distance.</summary>
    public float Falloff { get; init; } = 1f;

    /// <summary>Resolution scale — half by default, which is where this effect belongs.</summary>
    public float Scale { get; init; } = 0.5f;
}

/// <summary>The measured exposure, reduced on the device and never read back.</summary>
/// <remarks>
///     ⚠ <b>It publishes a buffer, not a texture</b>, under a fixed name — so a <c>!Tonemap</c> picks
///     it up by naming that resource in <c>exposureBuffer</c> rather than by naming this node. The
///     value is produced on the device and consumed on the device; a host that read it back would pay
///     a stall and a frame of latency for a number it never looks at.
/// </remarks>
[DataContract("AutoExposure")]
public sealed record AutoExposureAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The scene colour it measures.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>How long a frame is, for the adaptation rates below.</summary>
    public float DeltaTime { get; init; } = 1f / 60f;

    /// <summary>How fast the eye adapts from dark to bright.</summary>
    public float BrightenRate { get; init; } = 3f;

    /// <summary>How fast it adapts from bright to dark, which is slower in life and here.</summary>
    public float DarkenRate { get; init; } = 1f;

    /// <summary>What the measured average is mapped to.</summary>
    public float MiddleGrey { get; init; } = 0.18f;

    /// <summary>The dimmest exposure it will settle at.</summary>
    public float MinimumExposure { get; init; } = 0.03f;

    /// <summary>The brightest exposure it will settle at.</summary>
    public float MaximumExposure { get; init; } = 8f;

    /// <summary>The edge of the first reduction, in texels.</summary>
    public int StartSize { get; init; } = 512;
}
