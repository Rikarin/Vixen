// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;

namespace Vixen.Rendering.PostFx;

/// <summary>Traced reflections over a real frame — doc 19 § L5 in the compositor.</summary>
/// <remarks>
///     <para>
///         <b>The node that turns the kernel's fixture contract into a frame's.</b> The device tests
///         hand <c>ReflectionTrace</c> a positions plane; a frame has no such thing — it has the
///         depth it drew and the normals its G-buffer holds. This node points the kernel at both,
///         switches it into reconstruction (the same <c>Transform.UvDepthToWorld</c> placement and
///         the upsample lean on), and reuses the very same depth for the screen march, so SSR and
///         reconstruction cannot disagree about which camera drew the frame.
///     </para>
///     <para>
///         <b>The normals resource is a contract: world normal in xyz, roughness in alpha.</b> A
///         G-buffer that packs differently writes an adapter pass, not a different kernel.
///     </para>
///     <para>
///         <b>The composed sources are the host's.</b> The clipmap, the surface cache, the field and
///         the miss probes each belong to whoever owns them, and each writes its names into
///         <see cref="Trace" />'s parameters under <c>ReflectionTrace.&lt;source&gt;.*</c> — this
///         node knows about ordering and the frame, nothing else, the arrangement every renderer
///         node here keeps.
///     </para>
/// </remarks>
public sealed class ReflectionRenderer : SceneRenderer, IDisposable {
    ReflectionTraceFill? trace;
    TextureHandle output;
    TextureViewHandle outputView;
    Int2 outputSize;
    IGraphicsDevice? owner;
    bool transitioned;
    bool disposed;

    /// <summary>The frame resource the positions are reconstructed from and the screen march reads.</summary>
    public string Depth { get; set; } = "Depth";

    /// <summary>The frame resource holding world normals in xyz and roughness in alpha.</summary>
    public string Normals { get; set; } = "Normals";

    /// <summary>The name this node publishes its answer under — radiance in rgb, validity in alpha.</summary>
    public string Target { get; set; } = "Reflections";

    /// <summary>The frame resource a screen hit reflects, or null for no screen march at all.</summary>
    /// <remarks>SSR wants the frame's own colour, and which colour that is — last frame's lit
    ///     buffer, this frame's opaque pass — is a scheduling decision the host makes by naming it.
    ///     Null hands every sharp ray straight to the field march.</remarks>
    public string? Colour { get; set; }

    /// <summary>Where the kernel's variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>The device the output is made on, or null to take the frame's.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The camera that drew the frame — the forward matrix, for the screen march.</summary>
    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Its inverse, for the reconstruction. The host has both; inverting here would
    ///     manufacture error.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Where the camera stands.</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>The kernel's driver — sources, thresholds and march settings are set here.</summary>
    /// <remarks>Created on the first build, because it needs the device. The node overwrites the
    ///     frame-shaped properties every build — planes, viewports, matrices, reconstruction — and
    ///     leaves every judgement call to whoever configures this.</remarks>
    public ReflectionTraceFill? Trace => trace;

    /// <summary>Why the last frame recorded nothing, or null.</summary>
    public string? Skipped { get; private set; }

    /// <summary>The published answer's texture — for a test's readback or a host's own composite.</summary>
    public TextureHandle Output => output;

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if ((Device ?? frame.Device) is not { } device || Effects is null || Pipelines is null || Allocator is null) {
            return;
        }

        var depth = frame.Texture(ToString(), Depth);
        var normals = frame.Texture(ToString(), Normals);
        var colour = Colour is { } name ? frame.Texture(ToString(), name) : default;

        EnsureOutput(device, frame.Size);

        trace ??= new(device);
        trace.Effects = Effects;
        trace.Pipelines = Pipelines;
        trace.Descriptors = Allocator;

        // The answer, published for whoever composites it. Imported as ShaderRead on both ends;
        // the pass below brackets its own write, because the texture is this node's, not the
        // graph's.
        frame.Graph.ImportTexture(
            output,
            outputView,
            Description(frame.Size, Target),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;
                pass.SideEffect();

                // Both reads order this pass after whatever drew the frame — without them the
                // dispatch reflects last frame's texels or none.
                pass.Reads(depth);
                pass.Reads(normals);

                if (Colour is not null) {
                    pass.Reads(colour);
                }

                pass.Execute(
                    context => {
                        var commands = context.CommandList;

                        if (!transitioned) {
                            transitioned = true;
                            commands.Barrier(
                                new([], [new TextureBarrier(output, ResourceState.Undefined, ResourceState.ShaderRead)])
                            );
                        }

                        trace.Positions = default;
                        trace.Normals = context.View(normals);
                        trace.Target = outputView;
                        trace.ScreenDepth = context.View(depth);
                        trace.ScreenColour = Colour is not null ? context.View(colour) : default;
                        trace.ReconstructFromDepth = true;
                        trace.ViewProjection = ViewProjection;
                        trace.InverseViewProjection = InverseViewProjection;
                        trace.CameraPosition = CameraPosition;
                        trace.Viewport = frame.Size;
                        trace.ScreenViewport = frame.Size;

                        commands.Barrier(
                            new(
                                [],
                                [new TextureBarrier(output, ResourceState.ShaderRead, SurfaceCacheTexture.PlaneIsBeingWritten)]
                            )
                        );

                        trace.Record(commands);
                        Skipped = trace.Skipped;

                        commands.Barrier(
                            new(
                                [],
                                [new TextureBarrier(output, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.ShaderRead)]
                            )
                        );
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        trace?.Dispose();
        Destroy();
    }

    static TextureDescription Description(Int2 size, string name) =>
        new(
            PixelFormat.Rgba32Float,
            size.X,
            size.Y,
            TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.CopySource,
            Name: name
        );

    /// <summary>The answer's texture, frame-sized, remade when the window changes.</summary>
    void EnsureOutput(IGraphicsDevice device, Int2 size) {
        if (output.IsValid && size == outputSize) {
            return;
        }

        Destroy();

        owner = device;
        outputSize = size;
        transitioned = false;

        output = device.CreateTexture(Description(size, Target));
        outputView = device.CreateTextureView(output);
    }

    void Destroy() {
        if (owner is not { } device) {
            return;
        }

        if (outputView.IsValid) {
            device.Destroy(outputView);
            outputView = default;
        }

        if (output.IsValid) {
            device.Destroy(output);
            output = default;
        }
    }
}
