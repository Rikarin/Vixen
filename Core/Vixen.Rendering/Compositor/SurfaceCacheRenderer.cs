// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.SurfaceCache;

namespace Vixen.Rendering.Compositor;

/// <summary>Keeps the surface cache captured, lit and bounced, frame by frame — doc 19 § L4 live.</summary>
/// <remarks>
///     <para>
///         The counterpart to <see cref="IrradianceFieldRenderer" />, and the same shape for the
///         same reasons: a store that knows how to hold surfaces, a mirror that knows how to copy
///         one up, kernels that know how to light one, and this to sequence them — capture, then
///         light, then the bounce, then the swap, in an order a caller cannot accidentally invert.
///     </para>
///     <para>
///         <b>The capture is one card a frame, one frame late.</b> <see cref="SurfaceCardCapture" />
///         holds a single card's readback, and a readback is only readable after its submit — so
///         each frame first decodes the card recorded <i>last</i> frame into the store, then records
///         the next card round-robin. That is the budget's floor and its current ceiling; a wider
///         budget is a capture with more readbacks in flight, and it can arrive without this node's
///         order changing.
///     </para>
///     <para>
///         <b>One author per plane, never two.</b> <see cref="Radiosity" /> lights and bounces on
///         the CPU and the upload carries the result; <see cref="LightFill" /> and
///         <see cref="GatherFill" /> dispatch, and the mirror then refuses to copy the planes they
///         own. Asking for both is refused rather than resolved, for
///         <see cref="IrradianceFieldRenderer.DeviceFiller" />'s exact reason: lighting that
///         flickers between two answers is not a mode anybody chose.
///     </para>
///     <para>
///         <b>The gather's set 0 is applied here, before its record, and that is load-bearing.</b>
///         The bounce reads the front of the double buffer while writing the back, so the cache's
///         names must be written with the front <i>as it stands</i>, and the swap must come after
///         the record — one frame's bounce reading the previous frame's answer, which is the
///         recurrence the CPU pair converges by. Consumers named in <see cref="Passes" /> get the
///         post-swap front, so a trace this frame reads the bounce this frame produced.
///     </para>
/// </remarks>
public sealed class SurfaceCacheRenderer : SceneRenderer, IDisposable {
    int cursor;
    int pending = -1;
    bool disposed;

    /// <summary>The cache to keep. Null does nothing at all.</summary>
    public SurfaceCacheStore? Store { get; set; }

    /// <summary>What captures cards at runtime, or null for a store somebody captured already.</summary>
    public SurfaceCardCapture? Capture { get; set; }

    /// <summary>What the capture draws — the scene, by whoever knows how to draw it.</summary>
    public SurfaceCardCapture.DrawCard? Draw { get; set; }

    /// <summary>What lights and bounces on the CPU, or null for a cache the dispatches author.</summary>
    public CardRadiosity? Radiosity { get; set; }

    /// <summary>The lighting dispatch — doc 19 § L4's direct pass on the device.</summary>
    public SurfaceCacheLightFill? LightFill { get; set; }

    /// <summary>The bounce dispatch. Recording it is what advances the swap.</summary>
    public SurfaceCacheGatherFill? GatherFill { get; set; }

    /// <summary>Its mirror on the device, made on the first build.</summary>
    public SurfaceCacheTexture? Texture { get; private set; }

    /// <summary>Where the consumers' names go — the frame's set 0.</summary>
    /// <remarks>Null writes nothing, which is what a node kept for its dispatches alone wants.</remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>Which passes read this cache through the composed sampler.</summary>
    /// <remarks>The screen-probe trace alone by default — the pass whose hit branch composes the
    ///     slot. Each entry is qualified with <see cref="Source" />, one fewer place to misspell
    ///     the half that resolves to nothing silently.</remarks>
    public IList<string> Passes { get; } = ["ScreenProbeTrace"];

    /// <summary>The shader filling the slot, which is the other half of every binding's name.</summary>
    public string Source { get; set; } = MaterialCompiler.SurfaceCacheShader;

    /// <summary>From the surface toward the sun, for the CPU lighting path.</summary>
    public Vector3 TowardSun { get; set; } = new(0f, 1f, 0f);

    /// <summary>The sun's irradiance on a perpendicular surface, for the CPU lighting path.</summary>
    public Vector3 SunIrradiance { get; set; } = Vector3.One;

    /// <summary>The device its texture is made on, or null to take the frame's.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many texels the runtime capture has decoded into the store.</summary>
    public int Captured { get; private set; }

    /// <summary>How many bounces have been recorded or run since this node was made.</summary>
    /// <remarks>The convergence counter: a cache that looks single-bounce with this climbing is a
    ///     gather finding no light, and with this still is a scheduling failure.</remarks>
    public int Bounces { get; private set; }

    /// <summary>The largest change the last CPU gather saw, per channel maximum.</summary>
    /// <remarks>The CPU pair's own convergence measure, surfaced so a host can stop iterating; the
    ///     dispatches' equivalent is a readback, which is a host's decision to pay for.</remarks>
    public float LastChange { get; private set; }

    /// <summary>Declares the pass that keeps the cache.</summary>
    /// <param name="compositor">The compositor.</param>
    /// <param name="frame">The frame being built.</param>
    /// <exception cref="ArgumentNullException">There is no frame.</exception>
    /// <exception cref="InvalidOperationException">Both a CPU radiosity and a dispatch are set.</exception>
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Store is not { } store || (Device ?? frame.Device) is not { } device) {
            return;
        }

        if (Radiosity is not null && (LightFill is not null || GatherFill is not null)) {
            throw new InvalidOperationException(
                $"'{this}' has both a CPU radiosity and a dispatch. A plane can be authored by one "
                + "or the other: with both, every upload copies the CPU's answer over the dispatch's, "
                + "one frame after it wrote it. Clear whichever is not wanted."
            );
        }

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // ⚠ **Graphics in all three shapes, and it used to pick between the three kinds.**
                // The kind is read by the scheduler now, and it decided nothing about the body:
                //
                //  - the capture shape really does record render passes, so that one was right;
                //  - the dispatch shape and the bare-upload shape both run
                //    SurfaceCacheTexture.Upload, which brackets its copies with barriers into
                //    ResourceState.ShaderRead — vertex, fragment and compute stages, none of which a
                //    Vulkan transfer family supports;
                //  - and none of the three declares a single graph resource. The planes belong to the
                //    mirror and reach a shader by name, so no wait edge could be derived in either
                //    direction and a hoisted pass would be one nothing waits for.
                pass.Kind = PassKind.Graphics;

                pass.SideEffect();
                pass.Execute(context => Keep(store, device, context.CommandList));
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Texture?.Dispose();
        Texture = null;
    }

    /// <summary>One frame of keeping: decode, capture, light, upload, dispatch, swap, publish.</summary>
    void Keep(SurfaceCacheStore store, IGraphicsDevice device, ICommandList commands) {
        // Last frame's capture first, so the texels it brought are lit and uploaded this frame
        // rather than next. The read is safe because the frame ring waited on this slot's fence
        // before reusing it — the same guarantee every readback in a frame leans on.
        if (Capture is { } capture && Draw is { } draw && store.Cards.Count > 0) {
            if (pending >= 0 && capture.TryRead(store, pending, out var texels)) {
                Captured += texels;
            }

            pending = -1;

            cursor %= store.Cards.Count;
            capture.Record(commands, store.Cards[cursor].Card, draw);
            pending = cursor;
            cursor++;
        }

        if (Radiosity is { } radiosity) {
            radiosity.Light(store, TowardSun, SunIrradiance);
            LastChange = radiosity.Gather(store);
            Bounces++;
        }

        Texture ??= new(store) {
            DirectIsWritten = LightFill is not null,
            BounceIsWritten = GatherFill is not null
        };

        Texture.Upload(device, commands);

        if (LightFill is { } light) {
            light.Record(commands, Texture);
        }

        if (GatherFill is { } gather) {
            // The cache's own names, with the front as it stands: the bounce reads the previous
            // swap's answer while writing the back, which is the recurrence the CPU pair runs.
            Texture.Apply(gather.Parameters, $"{SurfaceCacheGatherFill.ShaderName}.{MaterialCompiler.SurfaceCacheShader}");

            if (gather.Record(commands, Texture) > 0) {
                Texture.SwapGather();
                Bounces++;
            }
        }

        if (SceneConstants is { } scene) {
            foreach (var pass in Passes) {
                Texture.Apply(scene.Parameters, $"{pass}.{Source}");
            }
        }
    }
}
