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
///     a frame gets one exactly one way: a stage whose <c>shader:</c> is <c>MotionVectors</c>, drawn
///     into a colour target of at least two signed channels. Naming a resource nothing writes leaves
///     every pixel reprojected onto itself, which is TAA with its first defence removed.
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

    /// <summary>The altitude the authored density holds at, in world units.</summary>
    /// <remarks>
    ///     ⚠ Appended, and it has to be — a <c>[DataContract]</c> is serialised in declaration order,
    ///     so a member inserted above re-reads every later one from the wrong offset. See
    ///     <see cref="DistanceFieldAoAsset.View" /> for the same note and the same reason.
    /// </remarks>
    public float Height { get; init; }

    /// <summary>How fast density thins above <see cref="Height" />, per world unit.</summary>
    public float HeightFalloffRate { get; init; } = 0.05f;

    /// <summary>Which way the light travels.</summary>
    /// <remarks>
    ///     ⚠ On <c>WaterAsset.SunDirection</c>'s terms. Left at the default the fog brightens toward
    ///     straight up, which is a scattering peak in a place no sky puts one — fog lit from a
    ///     different day than the sky in the same document.
    /// </remarks>
    public Vector3 SunDirection { get; init; } = new(0f, -1f, 0f);

    /// <summary>The colour the forward-scattering peak goes toward.</summary>
    public Vector3 SunColour { get; init; } = new(1f, 0.9f, 0.7f);

    /// <summary>Henyey–Greenstein anisotropy. Air is forward-scattering, around 0.7.</summary>
    public float SunAnisotropy { get; init; } = 0.7f;

    /// <summary>The marched volume a <c>!VolumetricFog</c> node filled, or empty for the falloff alone.</summary>
    public string Volume { get; init; } = "";

    /// <summary>The volume's near plane, far plane and slice count — the dispatch's own three.</summary>
    /// <remarks>⚠ See <c>FogRenderer.VolumeNear</c>: differing from the dispatch reads the wrong slice.</remarks>
    public float VolumeNear { get; init; } = 0.5f;

    /// <inheritdoc cref="VolumeNear" />
    public float VolumeFar { get; init; } = 64f;

    /// <inheritdoc cref="VolumeNear" />
    public int VolumeSlices { get; init; } = 64;
}

/// <summary>The three compute dispatches that fill a froxel fog volume.</summary>
/// <remarks>
///     Paired with <see cref="FogAsset" /> exactly as <c>GlobalDistanceField</c> is paired with
///     <see cref="DistanceFieldAoAsset" />: this fills a volume and names it, and the fog node reads
///     the name. A document with one and not the other is not broken — the composite falls back to
///     the analytic falloff, and the dispatches nothing reads are culled by the graph.
/// </remarks>
[DataContract("VolumetricFog")]
public sealed record VolumetricFogAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The view whose camera the froxels are laid out in front of.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>The names the three volumes are published under.</summary>
    public string Media { get; init; } = "FogMedia";

    /// <inheritdoc cref="Media" />
    public string Scattered { get; init; } = "FogScattered";

    /// <inheritdoc cref="Media" />
    public string Output { get; init; } = "FogVolume";

    /// <summary>The cascade atlas the froxels are shadowed against.</summary>
    /// <remarks>
    ///     ⚠ Naming it does not turn shadowing on — the frame declaring it does, along with the
    ///     cascades published under <see cref="ScenePass" />. See <c>VolumetricFogRenderer.Shadowed</c>.
    /// </remarks>
    public string ShadowAtlas { get; init; } = "ShadowAtlas";

    /// <summary>Whether the froxels are shadowed against those cascades at all.</summary>
    /// <remarks>
    ///     ⚠ A ceiling under the frame's own answer, not a switch: false gives a glow where a shaft
    ///     would have been, and true cannot conjure an atlas a frame never declared. See
    ///     <see cref="PostFidelityQuality.VolumetricShadows" /> for what it buys and costs.
    /// </remarks>
    public bool Shadows { get; init; } = true;

    /// <summary>Whose published cascades and biases the shadow taps use.</summary>
    public string ScenePass { get; init; } = "ForwardPlus";

    /// <summary>How many froxels across, down and deep.</summary>
    public int Width { get; init; } = 160;

    /// <inheritdoc cref="Width" />
    public int Height { get; init; } = 90;

    /// <inheritdoc cref="Width" />
    public int Slices { get; init; } = 64;

    /// <summary>Where the grid begins and ends, in metres of view depth.</summary>
    /// <remarks>⚠ Not the camera's far plane. See <c>VolumetricFogRenderer.Far</c>.</remarks>
    public float Near { get; init; } = 0.5f;

    /// <inheritdoc cref="Near" />
    public float Far { get; init; } = 64f;

    /// <summary>How much light a metre of the medium takes out of a ray.</summary>
    public float Density { get; init; } = 0.02f;

    /// <summary>What fraction of that is scattered rather than absorbed, per channel.</summary>
    public Vector3 ScatteringAlbedo { get; init; } = new(0.9f, 0.92f, 0.95f);

    /// <summary>Whether density thins with altitude.</summary>
    public bool HeightFalloff { get; init; } = true;

    /// <summary>The altitude the authored density holds at, and how fast it thins above.</summary>
    public float FogHeight { get; init; }

    /// <inheritdoc cref="FogHeight" />
    public float HeightFalloffRate { get; init; } = 0.05f;

    /// <summary>Which way the light travels, and what it carries.</summary>
    public Vector3 SunDirection { get; init; } = new(0f, -1f, 0f);

    /// <inheritdoc cref="SunDirection" />
    public Vector3 SunColour { get; init; } = new(1f, 0.9f, 0.7f);

    /// <summary>Henyey–Greenstein anisotropy.</summary>
    public float PhaseG { get; init; } = 0.7f;

    /// <summary>What arrives from the whole sky. ⚠ Without it a back-lit valley is black.</summary>
    public Vector3 AmbientColour { get; init; } = new(0.35f, 0.42f, 0.55f);
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

    /// <summary>The elevation below which a horizon does not count, as a sine.</summary>
    /// <remarks>
    ///     The self-occlusion guard for the march's one-texel first sample — see
    ///     <see cref="AmbientOcclusionRenderer.Bias" /> for the trade at both ends.
    /// </remarks>
    public float Bias { get; init; } = 0.1f;

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
    /// <remarks>
    ///     ⚠ <b>EV 17, and the floor has to reach daylight or the clamp becomes the meter.</b> These
    ///     are linear multipliers, so the <em>dimmest</em> exposure is the one that bounds the
    ///     <em>brightest</em> scene — and a sunlit surface is around EV 15. This used to be 0.03,
    ///     which is EV 4.8: a dim interior. Every outdoor scene lit in real photometric units then
    ///     asked for an exposure ten stops below what it was allowed, settled on the floor, and
    ///     tonemapped a thousand times over white — a frame that is uniformly blown out and reports
    ///     nothing wrong, because the meter did converge. It converged on the clamp.
    /// </remarks>
    public float MinimumExposure { get; init; } = Photometry.ExposureFromEv100(17f);

    /// <summary>The brightest exposure it will settle at.</summary>
    public float MaximumExposure { get; init; } = 8f;

    /// <summary>The edge of the first reduction, in texels.</summary>
    public int StartSize { get; init; } = 512;

    /// <summary>Whether the frame is metered by a histogram rather than by a geometric mean.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two answer different questions.</b> A geometric mean is a good number for a
    ///         scene of roughly uniform brightness and a bad one for the two cases exposure exists
    ///         for — a dark room with a bright window, a bright street with a dark doorway. The mean
    ///         sits between the two populations and exposes for neither. Percentiles throw the window
    ///         and the doorway away and expose for what is left, which is what a spot meter does.
    ///     </para>
    ///     <para>
    ///         Off by default, because a document that turned it on without meaning to would get a
    ///         different exposure for a reason it never wrote down. Unreal ships both and calls them
    ///         "Histogram" and "Basic"; this is the same pair.
    ///     </para>
    /// </remarks>
    public bool UseHistogram { get; init; }

    /// <summary>The darkest luminance the histogram resolves, as a base-2 log.</summary>
    public float MinimumLogLuminance { get; init; } = -10f;

    /// <summary>And the brightest.</summary>
    public float MaximumLogLuminance { get; init; } = 20f;

    /// <summary>The fraction of the frame discarded from the dark end before the mean is taken.</summary>
    public float LowPercentile { get; init; } = 0.5f;

    /// <summary>And from the bright end.</summary>
    public float HighPercentile { get; init; } = 0.95f;

    /// <summary>How strongly the centre of the frame counts for more than its edge.</summary>
    /// <remarks>
    ///     Zero meters the whole frame evenly; higher narrows the weight toward the middle, which is
    ///     what stops a bright sky at the top of the frame underexposing the subject in the middle.
    /// </remarks>
    public float MeteringPower { get; init; } = 1f;
}

/// <summary>Defocus, from the lens the frame is exposed through.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Before the tonemap, and before <c>!Bloom</c>.</b> Defocus is the lens spreading light
///         across the sensor, which is scene-referred — a blurred highlight has to keep its energy so
///         that averaging it with a dark neighbour gives the bright smear a lens gives rather than a
///         grey one. And the glow has to be built from the image the lens actually focused, not from
///         highlights that were never there.
///     </para>
///     <para>
///         ⚠ <b>Every number comes off the view's <c>Camera</c>.</b> There is no manual mode
///         and that is deliberate: an aperture that sets the exposure and a blur radius typed beside
///         it are two answers to one question. A camera with no lens, or one focused at infinity,
///         leaves the frame sharp.
///     </para>
/// </remarks>
[DataContract("DepthOfField")]
public sealed record DepthOfFieldAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The scene colour it defocuses.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The depth every pixel's distance is read from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The view whose camera carries the lens.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Defocused";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>How many samples the disc is gathered with.</summary>
    public int Samples { get; init; } = 16;

    /// <summary>Whether the bokeh takes the diaphragm's polygon rather than a circle.</summary>
    public bool UseBladeShape { get; init; } = true;

    /// <summary>How wide the blur may get, in pixels.</summary>
    public float MaximumRadius { get; init; } = 24f;
}

/// <summary>The smear a shutter that is open for a finite time leaves behind.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Before the tonemap.</b> A smear is light landing on the sensor over an interval, so
///         averaging it is averaging radiance. After the curve it would average two curves' outputs,
///         and a bright object crossing a dark one would smear grey rather than bright.
///     </para>
///     <para>
///         ⚠ <b>It needs a motion-vector target, and a frame gets one exactly one way</b>: a stage
///         whose <c>shader:</c> is <c>MotionVectors</c>, drawn into a colour target of at least two
///         signed channels. A frame that forgot that stage reads zeros everywhere and this node is a
///         copy — the same failure <c>!TemporalAntialiasing</c> has always had.
///     </para>
///     <para>
///         <b>There is no intensity.</b> The shutter comes off the named view's camera, because
///         <c>Camera.ShutterTime</c> is already what decides the exposure — one number, two answers,
///         which is what the camera component is for. With no view it falls back to a 180° shutter.
///     </para>
/// </remarks>
[DataContract("MotionBlur")]
public sealed record MotionBlurAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The scene colour it smears.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Where every pixel was last frame.</summary>
    public string MotionVectors { get; init; } = string.Empty;

    /// <summary>The view whose camera carries the shutter.</summary>
    public string View { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Blurred";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>How many taps the line is sampled with.</summary>
    public int Samples { get; init; } = 8;

    /// <summary>Whether a pixel takes the longest velocity near it rather than only its own.</summary>
    /// <remarks>
    ///     ⚠ Off, the smear stops at a moving object's silhouette, because the still background beside
    ///     it has no velocity to gather along. On is the cheap approximation of a tile-max pass and
    ///     fixes it out to the sample radius and no further.
    /// </remarks>
    public bool UseNeighbourMax { get; init; } = true;

    /// <summary>The longest smear allowed, in pixels.</summary>
    public float MaximumRadius { get; init; } = 32f;

    /// <summary>Below this many pixels of motion the pass is a copy.</summary>
    public float MinimumRadius { get; init; } = 0.5f;
}

/// <summary>One exposure per region of the frame rather than one for the whole of it.</summary>
/// <remarks>
///     <para>
///         <b>The thing a single exposure cannot do.</b> A frame with a sunlit window and an unlit
///         interior has ten or twelve stops between the two, and a tone curve has about six to spend —
///         so whatever the meter picks, one of them is white or black. That is what a camera does and
///         what an eye does not. Unreal 5 ships this; Unity has no counterpart.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap.</b> It moves radiance around before the curve shapes it, which is
///         the point: the purpose is to bring the highlights into the range the curve can spend, and
///         after the curve there is nothing left to recover.
///     </para>
///     <para>
///         ⚠ <b><c>view</c> or <c>ev100</c>, and it has to agree with the tonemap's.</b> The pivot is
///         the luminance that stays exactly where it is, and it belongs wherever the meter says middle
///         grey is. A document that gives this and the tonemap different exposures gets a frame that
///         is locally right and globally wrong.
///     </para>
/// </remarks>
[DataContract("LocalExposure")]
public sealed record LocalExposureAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The linear HDR colour it re-exposes.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The view whose lens supplies the exposure value, or empty for <see cref="Ev100" />.</summary>
    public string View { get; init; } = "";

    /// <summary>
    ///     The buffer <c>!AutoExposure</c> published this frame's measured exposure in, or empty for
    ///     an authored one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one that matters, because this node only ever runs in a metered frame.</b> The
    ///     pivot has to be the exposure the tonemap will apply, and in a metered frame that number
    ///     exists only on the device. See <see cref="LocalExposureRenderer.ExposureBuffer" />.
    /// </remarks>
    public string ExposureBuffer { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "LocallyExposed";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>The exposure value the compression pivots around, when no view supplies one.</summary>
    public float Ev100 { get; init; } = 12f;

    /// <summary>How many taps the bilateral blur uses along each axis of its cross.</summary>
    public int Taps { get; init; } = 6;

    /// <summary>How far the bilateral gather reaches, in texels of the quarter-resolution base.</summary>
    public float BlurRadius { get; init; } = 6f;

    /// <summary>How different two luminances have to be, in stops, before a tap stops counting.</summary>
    /// <remarks>
    ///     ⚠ The one number that decides whether the result has halos. Large, and a bright window
    ///     bleeds across its frame so the wall beside it is compressed as though it were bright — the
    ///     dark ring people call the HDR halo. Small, and the base follows every edge and there is no
    ///     large-scale brightness left to compress.
    /// </remarks>
    public float EdgeRange { get; init; } = 1.5f;

    /// <summary>How much the highlights are brought down. 0 is off.</summary>
    public float HighlightContrast { get; init; } = 0.6f;

    /// <summary>And how much the shadows are brought up.</summary>
    public float ShadowContrast { get; init; } = 0.4f;

    /// <summary>A ceiling on how far any pixel may move, in stops.</summary>
    public float MaximumStops { get; init; } = 4f;
}

/// <summary>Ghosts, a halo and a starburst — what a bright source does to the rest of the frame.</summary>
/// <remarks>
///     <para>
///         <b>Not bloom with a different name.</b> Bloom is light scattered a short way from where it
///         landed; a flare is light that reflected between two lens elements and landed somewhere
///         else. Where it lands is not arbitrary — a ghost sits on the line from the highlight through
///         the centre of the frame — which is why the whole effect is one vector and a list of scales.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap and before <c>!Bloom</c>.</b> A flare is light arriving at the
///         sensor and has to be able to blow out. After the curve it is a wash over the picture, and a
///         highlight already at white would gain a ghost brighter than itself.
///     </para>
///     <para>
///         ⚠ <b><see cref="Threshold" /> is in the source's units.</b> In a physically lit frame that
///         is cd/m² and nothing is near one, so the default of one flares the floor. The same argument
///         <c>!Bloom</c>'s threshold makes, and the same failure if it is left alone.
///     </para>
/// </remarks>
[DataContract("LensFlare")]
public sealed record LensFlareAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The linear HDR colour the flare is built from and added onto.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The view whose lens supplies the blade count, or empty for <see cref="StarburstBlades" />.</summary>
    public string View { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Flared";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>Luminance above which a pixel contributes to a flare.</summary>
    public float Threshold { get; init; } = 1f;

    /// <summary>How many ghosts are traced along the centre vector.</summary>
    public int Ghosts { get; init; } = 5;

    /// <summary>How far apart consecutive ghosts are, as a fraction of the vector to the centre.</summary>
    public float GhostSpacing { get; init; } = 0.35f;

    /// <summary>How bright the ghosts are.</summary>
    public float GhostIntensity { get; init; } = 0.06f;

    /// <summary>Whether the halo ring is drawn.</summary>
    public bool UseHalo { get; init; } = true;

    /// <summary>How far out the halo ring sits, in UV.</summary>
    public float HaloRadius { get; init; } = 0.45f;

    /// <summary>How thick it is.</summary>
    public float HaloThickness { get; init; } = 0.2f;

    /// <summary>How bright it is.</summary>
    public float HaloIntensity { get; init; } = 0.04f;

    /// <summary>How far the three channels are sampled apart, in UV. Zero is no fringing.</summary>
    /// <remarks>
    ///     Not decoration: a lens is corrected for one wavelength and a ghost forms at surfaces coated
    ///     for transmission, so a real ghost fringes at its edge. Without it a ghost is a flat blob.
    /// </remarks>
    public float ChromaticOffset { get; init; } = 0.006f;

    /// <summary>Whether the diaphragm's diffraction spikes are drawn.</summary>
    public bool UseStarburst { get; init; }

    /// <summary>How many spikes, when no view supplies a blade count.</summary>
    public float StarburstBlades { get; init; } = 6f;

    /// <summary>How strongly the spikes modulate the ghosts.</summary>
    public float StarburstIntensity { get; init; } = 0.5f;

    /// <summary>A rotation on the spike pattern, in radians.</summary>
    public float StarburstAngle { get; init; }

    /// <summary>A tint on the whole flare, for a lens with a coloured coating.</summary>
    public Vector3 Tint { get; init; } = Vector3.One;
}
