// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     Temporal antialiasing: the frame resolved against the ones before it.
/// </summary>
/// <remarks>
///     <para>
///         The default antialiasing where it can run, and the only one that adds information rather
///         than hiding its absence. FXAA finds edges in a frame and softens them; this jitters the
///         camera by a fraction of a pixel each frame and accumulates the results, so a slow pan
///         genuinely resolves detail below one pixel — which is also why it doubles as the sampling
///         budget for anything stochastic downstream.
///     </para>
///     <para>
///         <strong>It owns its history, and that is the whole difference from every other effect
///         here.</strong> A pass cannot read the target it writes, so there are two textures and the
///         node alternates them: this frame reads the one last frame wrote and writes the other. They
///         are <em>imports</em> rather than graph resources, because a transient is dead at the end of
///         the frame and a history that dies every frame is a history of nothing.
///     </para>
///     <para>
///         <strong>The jitter is the host's, and it has to be.</strong> This pass resolves samples
///         taken at different subpixel offsets; taking them is the camera's job, before anything is
///         drawn. <see cref="Jitter" /> is the sequence to offset the projection by — a pass that
///         accumulated an unjittered camera would average the same sample over and over, which is a
///         frame that gets blurrier and no sharper.
///     </para>
/// </remarks>
public sealed class TemporalAntialiasingRenderer() : PostEffectRenderer(
    TaaKeys.ShaderName,
    TaaKeys.UsedPermutationKeys,
    TaaKeys.ConstantBufferBinding
), IDisposable {
    readonly TextureHandle[] textures = new TextureHandle[2];
    readonly TextureViewHandle[] views = new TextureViewHandle[2];
    TextureDescription description;
    Int2 allocated;
    int parity;

    /// <summary>The frame it antialiases.</summary>
    public required string Source { get; init; }

    /// <summary>Where each pixel was last frame, in screen space.</summary>
    public required string MotionVectors { get; init; }

    /// <summary>The depth, for rejecting history across a silhouette.</summary>
    public required string Depth { get; init; }

    /// <summary>Whether history is clipped to the neighbourhood's variance rather than its bounds.</summary>
    /// <remarks>
    ///     The difference between ghosting and flickering. A box around the neighbourhood's minimum
    ///     and maximum is easy and too permissive — a bright pixel one frame widens the box enough to
    ///     admit stale history for several after it. Variance clipping fits the box to the
    ///     distribution instead, which is tighter almost everywhere.
    /// </remarks>
    public bool VarianceClipping { get; set; } = true;

    /// <summary>How much of the history survives each frame, 0 to 1.</summary>
    /// <remarks>
    ///     Nine tenths is the usual setting: it takes about twenty frames to converge, which is a
    ///     third of a second, and holds enough history that a still camera resolves properly. Higher
    ///     converges further and ghosts longer; the clipping above is what keeps that trade sane.
    /// </remarks>
    public float Feedback { get; set; } = 0.9f;

    /// <summary>How many standard deviations of the neighbourhood the history may be.</summary>
    public float VarianceGamma { get; set; } = 1.25f;

    /// <summary>Whether the history holds anything yet.</summary>
    /// <remarks>
    ///     False for the first frame after a resize or a cut, where the "history" is an uninitialised
    ///     texture. A host that ignores this gets one frame of whatever memory the allocator handed
    ///     back, which on some drivers is the last thing that lived there.
    /// </remarks>
    public bool HasHistory { get; private set; }

    /// <summary>
    ///     The subpixel offset to jitter the projection by, in pixels, for a frame index.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The Halton (2, 3) sequence, centred on zero: the standard choice, because it fills the
    ///         pixel more evenly than random offsets at every prefix length — which matters when the
    ///         camera stops after eight frames rather than after a thousand.
    ///     </para>
    ///     <para>
    ///         Returned rather than applied, because what it offsets is the *projection matrix*, and
    ///         that belongs to the view. <c>CameraExtractionSystem.JitterTarget</c> is what turns it on
    ///         — the host sets it to the frame's size when a tree has one of these in it — and
    ///         <c>CameraMath.Jittered</c> is where it reaches a matrix. The motion vectors need no
    ///         separate arrangement: both this frame's matrix and last frame's carry their own offset,
    ///         so the vector between them already describes where the history texel is rather than
    ///         where the world point was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sequence itself is <see cref="CameraMath.SubpixelJitter" />, and this is a call
    ///         to it.</b> Two Haltons would agree until somebody changed one, and what would then
    ///         disagree is the offset the camera sampled at and the offset this pass believes it
    ///         sampled at — a resolve reading its history from the wrong place, which looks exactly
    ///         like a resolve that is merely soft.
    ///     </para>
    /// </remarks>
    public static Vector2 Jitter(int frameIndex) => CameraMath.SubpixelJitter(frameIndex);

    /// <inheritdoc />
    protected override void DeclareOutput(CompositorFrame frame, Int2 size) {
        ArgumentNullException.ThrowIfNull(frame);

        var device = Device ?? frame.Device;

        if (device is null) {
            return;
        }

        Allocate(device, size);

        // This frame writes one and reads the other. Imported rather than declared, so the graph
        // treats them as memory somebody else owns — which is exactly what they are, and what stops
        // it aliasing next frame's history over something in this one.
        frame.Add(Output, Import(frame, device, parity), description.Format);
        frame.Add(HistoryName, Import(frame, device, 1 - parity), description.Format);
    }

    /// <inheritdoc />
    protected override void Configure(
        CompositorFrame frame,
        ParameterCollection parameters,
        IList<ResourceBinding> bindings
    ) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Set(TaaKeys.VarianceClipping, VarianceClipping);
        parameters.Set(TaaKeys.TexelSize, TexelSize(frame.Size));

        // No history to blend on the first frame, so take the current one whole. Blending against
        // undefined memory is a frame of garbage that fades out over twenty more.
        parameters.Set(TaaKeys.Feedback, HasHistory ? Feedback : 0f);
        parameters.Set(TaaKeys.VarianceGamma, VarianceGamma);

        Read(bindings, TaaKeys.CurrentBinding, Source);
        Read(bindings, TaaKeys.HistoryBinding, HistoryName);
        Read(bindings, TaaKeys.MotionVectorsBinding, MotionVectors);
        Read(bindings, TaaKeys.DepthBufferBinding, Depth);

        // History is reprojected to a position between texels, so it is sampled linearly; the depth
        // and the neighbourhood are read at exact texels, where a linear tap would average two
        // surfaces into one that is not there.
        Sample(bindings, TaaKeys.LinearSamplerBinding, Samplers!.LinearClamp);
        Sample(bindings, TaaKeys.PointSamplerBinding, Samplers!.PointClamp);

        // Flipped once the frame is configured rather than at the start of it, so everything above
        // agrees about which texture is which.
        parity = 1 - parity;
        HasHistory = true;
    }

    /// <summary>What the previous frame's result is published under.</summary>
    string HistoryName => $"{this}.History";

    /// <summary>The texture this frame resolves into.</summary>
    /// <remarks>
    ///     Exposed because "the two alternate" is the property the effect rests on and the only place
    ///     it is visible: both are imported under the same two names every frame, so the swap is in
    ///     the handles rather than in anything the graph or the command stream says.
    /// </remarks>
    public TextureHandle Target => textures[parity];

    /// <summary>The texture this frame reads its history from.</summary>
    public TextureHandle History => textures[1 - parity];

    /// <summary>Creates the pair, or recreates it when the frame changed size.</summary>
    /// <remarks>
    ///     A resize invalidates the history rather than scaling it: reprojecting a differently sized
    ///     history is a resample of a resample, and the frame after a resize is the one nobody is
    ///     looking at anyway.
    /// </remarks>
    void Allocate(IGraphicsDevice device, Int2 size) {
        if (allocated == size && textures[0].IsValid) {
            return;
        }

        Release(device);

        description = new(
            Format,
            Math.Max(size.X, 1),
            Math.Max(size.Y, 1),
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: HistoryName
        );

        for (var i = 0; i < 2; i++) {
            textures[i] = device.CreateTexture(description);
            views[i] = device.CreateTextureView(textures[i]);
        }

        allocated = size;
        HasHistory = false;
    }

    Graphics.RenderGraph.GraphTexture Import(CompositorFrame frame, IGraphicsDevice device, int index) =>
        frame.Graph.ImportTexture(textures[index], views[index], description);

    void Release(IGraphicsDevice device) {
        for (var i = 0; i < 2; i++) {
            if (views[i].IsValid) {
                device.Destroy(views[i]);
            }

            if (textures[i].IsValid) {
                device.Destroy(textures[i]);
            }

            views[i] = default;
            textures[i] = default;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing && Device is not null) {
            Release(Device);
        }

        base.Dispose(disposing);
    }
}
