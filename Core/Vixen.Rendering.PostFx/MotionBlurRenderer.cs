// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>The smear a shutter that is open for a finite time leaves behind.</summary>
/// <remarks>
///     <para>
///         <b>The shutter comes off the camera, not off a slider here.</b> <c>Camera.ShutterTime</c>
///         already decides the exposure; this is the second thing it decides. A motion vector spans
///         one frame, and what happened during the exposure is the fraction of that frame the shutter
///         was open for — so 1/60 at 60 Hz is a full frame of smear and 1/500 is almost none, which is
///         the relationship a real camera has. An intensity beside the aperture would be one more
///         number free to disagree with the brightness, which is the fault the whole camera merge was
///         about.
///     </para>
///     <para>
///         ⚠ <b>It needs a motion-vector target, and there is exactly one way to get one</b>: a stage
///         whose shader is <c>MotionVectors</c>, drawn into a colour target of at least two signed
///         channels. Without it every pixel reads zero and the pass is a copy — which is what a frame
///         that forgot the stage looks like, and is why the node names the resource rather than
///         guessing it.
///     </para>
///     <para>
///         ⚠ <b>Before the tonemap.</b> A smear is light landing on the sensor over an interval, so
///         averaging it is averaging radiance; after the curve it would average two different curves'
///         outputs and a bright object crossing a dark one would smear grey rather than bright.
///     </para>
/// </remarks>
public sealed class MotionBlurRenderer() : PostEffectRenderer(
    MotionBlurKeys.ShaderName,
    MotionBlurKeys.UsedPermutationKeys,
    MotionBlurKeys.ConstantBufferBinding
) {
    /// <summary>The scene colour it smears.</summary>
    public required string Source { get; init; }

    /// <summary>Where every pixel was last frame.</summary>
    public required string MotionVectors { get; init; }

    /// <summary>The view whose camera carries the shutter.</summary>
    /// <remarks>
    ///     ⚠ Without it there is no shutter, so <see cref="ShutterFraction" /> falls back to
    ///     <see cref="DefaultShutterFraction" /> — half a frame, which is a 180° shutter and the
    ///     convention film has used since there were rotating discs in cameras.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>How long the last frame took, for turning a shutter time into a fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>The frame's own duration, not a nominal 60 Hz.</b> A motion vector is measured over
    ///     whatever interval actually elapsed, so a frame that took twice as long already carries
    ///     twice the motion — dividing by a constant would double the smear on a slow frame, which is
    ///     the wrong way round from what anybody wants when the frame rate drops.
    /// </remarks>
    public float DeltaTime { get; set; } = 1f / 60f;

    /// <summary>A 180° shutter, which is half of each frame.</summary>
    public const float DefaultShutterFraction = 0.5f;

    /// <summary>How many taps the line is sampled with.</summary>
    public int Samples { get; set; } = 8;

    /// <summary>Whether a pixel takes the longest velocity near it rather than only its own.</summary>
    public bool UseNeighbourMax { get; set; } = true;

    /// <summary>The longest smear allowed, in pixels.</summary>
    public float MaximumRadius { get; set; } = 32f;

    /// <summary>Below this many pixels of motion the pass is a copy.</summary>
    public float MinimumRadius { get; set; } = 0.5f;

    /// <summary>What fraction of a frame the shutter is open for.</summary>
    /// <remarks>
    ///     Clamped to one at the top: a shutter open for longer than a frame cannot smear over more
    ///     motion than a frame contains, because that is all the motion the vector describes. It would
    ///     take a second frame of history to do better, and a long exposure that samples two frames is
    ///     a different effect.
    /// </remarks>
    public float ShutterFraction =>
        View?.Camera?.Lens is { HasLens: true, ShutterTime: > 0f } lens
            ? Math.Clamp(lens.ShutterTime / Math.Max(DeltaTime, 1e-6f), 0f, 1f)
            : DefaultShutterFraction;

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(MotionBlurKeys.Samples, Math.Max(Samples, 1));
        parameters.Set(MotionBlurKeys.UseNeighbourMax, UseNeighbourMax);
        parameters.Set(MotionBlurKeys.TexelSize, TexelSize(frame.Size));
        parameters.Set(MotionBlurKeys.ShutterFraction, ShutterFraction);
        parameters.Set(MotionBlurKeys.MaxRadius, MaximumRadius);
        parameters.Set(MotionBlurKeys.MinRadius, MinimumRadius);

        Read(bindings, MotionBlurKeys.SourceBinding, Source);
        Read(bindings, MotionBlurKeys.MotionVectorsBinding, MotionVectors);

        Sample(bindings, MotionBlurKeys.SourceSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, MotionBlurKeys.PointSamplerBinding, Samplers!.PointClamp);
    }
}
