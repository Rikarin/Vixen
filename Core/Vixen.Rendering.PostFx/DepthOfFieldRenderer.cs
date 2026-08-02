// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>Defocus, taken from the lens the frame is exposed through.</summary>
/// <remarks>
///     <para>
///         <b>Every number here comes off the view's <c>Camera</c>, and that is the point.</b> A
///         lens has one aperture: opening it brightens the image and shortens the depth of field
///         together. Both Unreal and HDRP offer a physical camera mode for the same reason, and both
///         also offer a manual one — this has only the physical, because the manual one is what lets
///         an author write a camera that cannot exist and then wonder why the blur and the brightness
///         disagree.
///     </para>
///     <para>
///         ⚠ <b>A frame with no lens is a frame with no defocus</b>, not a frame with a default one.
///         `Camera.HasLens` false, or a focus distance of zero, leaves every pixel sharp —
///         which is what focusing at infinity means and what a project that has not asked for this
///         should get.
///     </para>
///     <para>
///         ⚠ <b>It runs before the tonemap.</b> Defocus is the lens spreading light across the sensor,
///         which is scene-referred: a blurred highlight has to keep its energy so that averaging it
///         with a dark neighbour gives the bright smear a lens gives, and after the curve it would
///         give a grey one. It also has to run before <c>!Bloom</c> reads the image, or the glow is
///         built from highlights the lens never focused there.
///     </para>
/// </remarks>
public sealed class DepthOfFieldRenderer() : PostEffectRenderer(
    DepthOfFieldKeys.ShaderName,
    DepthOfFieldKeys.UsedPermutationKeys,
    DepthOfFieldKeys.ConstantBufferBinding
), IPostProcessTarget {
    PostProcessOverlay applied;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied. The authored properties stay exactly as the document set
    ///     them and the overlay is laid over them each time the node configures itself — a node that
    ///     wrote into its own properties here would lose the authored value the first frame a volume
    ///     reached it, and walking back out would restore the volume's numbers rather than the
    ///     document's.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The scene colour it defocuses.</summary>
    public required string Source { get; init; }

    /// <summary>The depth every pixel's distance is read from.</summary>
    public required string Depth { get; init; }

    /// <summary>The view whose camera carries the lens.</summary>
    /// <remarks>
    ///     ⚠ Without it there is no aperture, no focal length and no focus distance, so the pass is a
    ///     copy. That is the honest answer rather than a guessed lens: a defocus nobody authored,
    ///     applied to a frame that did not ask for one, is worse than none.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>How many samples the disc is gathered with.</summary>
    /// <remarks>
    ///     A permutation, so the loop unrolls. Sixteen holds up at moderate radii; a wide aperture
    ///     wants more, because the samples land on a spiral and too few of them look like a spiral.
    /// </remarks>
    public int Samples { get; set; } = 16;

    /// <summary>Whether the bokeh takes the diaphragm's polygon rather than a circle.</summary>
    public bool UseBladeShape { get; set; } = true;

    /// <summary>How wide the blur may get, in pixels.</summary>
    /// <remarks>A ceiling on the cost as much as on the look: every sample is a dependent read.</remarks>
    public float MaximumRadius { get; set; } = 24f;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        var camera = View?.Camera;
        var lens = camera?.Lens ?? default;

        parameters.Set(DepthOfFieldKeys.Samples, Math.Max(Samples, 1));
        parameters.Set(DepthOfFieldKeys.UseBladeShape, UseBladeShape && lens.BladeCount >= 3);
        parameters.Set(DepthOfFieldKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(DepthOfFieldKeys.MaxRadius, applied.MaximumDefocus?.Over(MaximumRadius) ?? MaximumRadius);

        // Millimetres are what a lens is quoted in and metres are what the scene is measured in, so
        // the conversion happens once, here, rather than in the shader where it would be a constant
        // multiplied per pixel and a unit nobody could see.
        parameters.Set(DepthOfFieldKeys.FocalLength, lens.FocalLength * 0.001f);
        parameters.Set(DepthOfFieldKeys.SensorWidth, lens.SensorWidth * 0.001f);
        parameters.Set(DepthOfFieldKeys.Aperture, lens.Aperture);
        parameters.Set(DepthOfFieldKeys.BladeCount, lens.BladeCount);

        // ⚠ Zero unless there is a whole lens, which is what makes the pass a copy rather than a
        // guess. `HasLens` is false for a zeroed component, which is what a view with no camera at
        // all leaves behind — and a focus distance of zero is what every camera starts with, so a
        // frame nobody has focused stays sharp.
        parameters.Set(DepthOfFieldKeys.FocusDistance, lens.HasLens ? lens.FocusDistance : 0f);

        parameters.Set(DepthOfFieldKeys.NearPlane, camera?.NearPlane ?? 0.1f);
        parameters.Set(DepthOfFieldKeys.FarPlane, camera?.FarPlane ?? 1000f);

        Read(bindings, DepthOfFieldKeys.SourceBinding, Source);
        Read(bindings, DepthOfFieldKeys.DepthBufferBinding, Depth);

        Sample(bindings, DepthOfFieldKeys.SourceSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, DepthOfFieldKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
