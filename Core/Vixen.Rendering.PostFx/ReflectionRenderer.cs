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

    /// <summary>The view whose camera drives the three above, or null for a host that sets them.</summary>
    /// <remarks>
    ///     <para>
    ///         What lets a document say <c>view: Camera</c> here the way <c>!Ssao</c> already does —
    ///         a matrix is per-frame data no file can carry, and a view is the frame's name for one.
    ///         Set, it overwrites the matrix pair and the position every build, so the node tracks
    ///         the camera without the host pushing three values a frame.
    ///     </para>
    ///     <para>
    ///         The inverse is derived here, which is the one concession: a view carries the product
    ///         and not the factors. A host holding exact inverses of its own leaves this null and
    ///         sets the pair itself, which is why the properties above stay writable.
    ///     </para>
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>How far a mirror ray looks before deciding it escaped.</summary>
    /// <remarks>
    ///     This and the five below mirror <see cref="ReflectionTraceFill" />'s own knobs, default for
    ///     default, and overwrite the kernel's every build — so a document owns them the way it owns
    ///     any node's numbers. What deliberately stays on <see cref="Trace" /> is the composed
    ///     sources: which field a ray marches and what a hit answers with are questions about the
    ///     scene, and they belong to whoever owns those objects.
    /// </remarks>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a mirror ray starts.</summary>
    public float Bias { get; set; } = 0.01f;

    /// <summary>The roughness at and above which the field answers instead of the trace.</summary>
    public float RoughnessThreshold { get; set; } = 0.5f;

    /// <summary>How far below the threshold the cross-fade starts. Zero is the hard switch.</summary>
    public float RoughnessBlend { get; set; }

    /// <summary>How many equal steps a screen ray takes.</summary>
    public int ScreenSteps { get; set; } = 32;

    /// <summary>How deep behind a surface a sample still counts as inside it, in device depth.</summary>
    public float ScreenThickness { get; set; } = 0.02f;

    /// <summary>The nearest chain the screen march skips by, or null for the fixed-step walk.</summary>
    /// <remarks>
    ///     <para>
    ///         Host-owned, wearing <see cref="HiZReduction.Nearest" /> — any other reduction is
    ///         ignored, because a chain of farthest texels skips rays through walls. Built here,
    ///         inside the node's own pass, so the reduce dispatches and the march cannot be
    ///         reordered apart; its barriers are <see cref="HiZPyramid" />'s own.
    ///     </para>
    ///     <para>
    ///         A host sharing one chain between this and the probes' gather sets
    ///         <see cref="HiZPyramid.BuildsPerFrame" /> to cover both, exactly as a two-phase cull
    ///         does.
    ///     </para>
    /// </remarks>
    public HiZPyramid? Pyramid { get; set; }

    /// <summary>How deep the march's shell is in view units, where a perspective ray can say. Zero
    ///     keeps the device-depth shell — forwarded to <see cref="ReflectionTraceFill.ScreenLinearThickness" />.</summary>
    public float ScreenLinearThickness { get; set; }

    /// <summary>The kernel's driver — the composed sources are set here.</summary>
    /// <remarks>Created on the first build, because it needs the device. The node overwrites the
    ///     frame-shaped properties every build — planes, viewports, matrices, reconstruction — and
    ///     the six knobs it mirrors (see <see cref="MaxDistance" />); what is left to whoever
    ///     configures this is the sources, which are the judgement calls about the scene.</remarks>
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

        // The document's camera, when one was named. Before anything reads the matrices, so the
        // dispatch below and the upsample of whoever composites this agree about the frame.
        if (View is { } camera) {
            ViewProjection = camera.ViewProjection;
            CameraPosition = camera.Position;

            if (Matrix4x4.Invert(camera.ViewProjection, out var inverted)) {
                InverseViewProjection = inverted;
            }
        }

        var depth = frame.Texture(ToString(), Depth);
        var normals = frame.Texture(ToString(), Normals);
        var colour = Colour is { } name ? frame.Texture(ToString(), name) : default;

        EnsureOutput(device, frame.Size);

        trace ??= new(device);
        trace.Effects = Effects;
        trace.Pipelines = Pipelines;
        trace.Descriptors = Allocator;

        // The node's own knobs, forwarded every build — see MaxDistance for why these six are the
        // node's and the composed sources are not.
        trace.MaxDistance = MaxDistance;
        trace.Bias = Bias;
        trace.RoughnessThreshold = RoughnessThreshold;
        trace.RoughnessBlend = RoughnessBlend;
        trace.ScreenSteps = ScreenSteps;
        trace.ScreenThickness = ScreenThickness;

        // The answer, published for whoever composites it — into the frame's namespace, not just
        // the graph, or `reflections: <Target>` in a document is a CompositorBindingException at
        // build. Imported as ShaderRead on both ends because the pass below brackets its own
        // write: the texture is this node's, not the graph's — PublishPlanes in the gather is the
        // same arrangement for the same reason.
        frame.Add(
            Target,
            frame.Graph.ImportTexture(
                output,
                outputView,
                Description(frame.Size, Target),
                ResourceState.ShaderRead,
                ResourceState.ShaderRead
            ),
            PixelFormat.Rgba32Float
        );

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // ⚠ **A dispatch that declares Graphics, and the two things it writes are why.**
                // The output is imported and then bracketed by this body's own barriers rather than
                // declared, so the graph is not told the pass produces it; and Pyramid is host-owned
                // and may be the same chain the probe gather marches, which is a second pass reading
                // something this one wrote with nothing in the graph joining them. Either alone
                // makes this unhoistable — a compute segment nothing waits for — and on one queue
                // both are ordered by declaration order, which is what this node was built on.
                pass.Kind = PassKind.Graphics;
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

                        // The chain first, into the same list: the march may only skip by texels
                        // the reduce has written, and the pyramid's own barriers order the two.
                        var chain = Pyramid is { Reduction: HiZReduction.Nearest } pyramid
                            && pyramid.Build(commands, context.View(depth), frame.Size)
                                ? Pyramid
                                : null;

                        trace.ScreenPyramid = chain?.View ?? default;
                        trace.ScreenPyramidLevels = chain is null ? 0 : chain.Levels + 1;
                        trace.ScreenLinearThickness = ScreenLinearThickness;
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
