// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;

namespace Vixen.Rendering.PostFx;

/// <summary>Doc 19 § L3's gather as one compositor node: place, trace, resolve, upsample.</summary>
/// <remarks>
///     <para>
///         <b>The node that schedules the screen-probe passes as one graph.</b> Each half exists and
///         is tested alone — <see cref="ScreenProbeTraceFill" /> against the CPU gather,
///         <see cref="ScreenProbeResolve" /> against the CPU projection,
///         <see cref="ScreenProbeUpsampleRenderer" /> against the CPU lattice walk — and this owns
///         none of that arithmetic. It owns the ordering: probes are placed from what the frame drew,
///         the trace and resolve run in one compute pass, the resolved planes enter the graph as
///         imports, and the upsample draws them. The same division of labour as
///         <see cref="Compositor.IrradianceFieldRenderer" />, one probe kind over.
///     </para>
///     <para>
///         <b>Placement reads the depth buffer of a frame that has finished.</b> The anchors' depths
///         and normals live on the device, so this node copies them back every frame and places
///         probes from the copy <see cref="Latency" /> frames old — a probe lattice one frame behind
///         the camera, which the temporal half of the denoiser will meet again as reprojection. The
///         matrix that reconstructs a copy is the matrix snapshotted <i>with</i> it: this frame's
///         camera against last frame's depth reconstructs surfaces that exist nowhere.
///     </para>
///     <para>
///         <b>The tracer and resolver are the host's, and only ordered here.</b> What the rays march
///         and what the sky is are questions about the scene — <see cref="Tracer" /> carries its
///         composed sources and parameters exactly as <see cref="Compositor.IrradianceFieldRenderer.DeviceFiller" />
///         does. One thing is imposed: <see cref="ScreenProbeTraceFill.ClearInvalid" /> is switched
///         on, because on an atlas the dispatch owns, the patch of a probe nothing placed is
///         undefined memory and the resolve reads validity out of it.
///     </para>
///     <para>
///         ⚠ <b>The lattice is sized on the first build and a resized frame is refused.</b> Rebuilding
///         the textures mid-flight while frames still reference them is a use-after-free with
///         latency; until resizing exists as a deliberate step, a host that resizes recreates the
///         node. Owed with the rest of the renderer integration.
///     </para>
/// </remarks>
public sealed class ScreenProbeGatherRenderer : SceneRenderer, IDisposable {
    /// <summary>How many planes the resolve writes.</summary>
    const int ProbePlanes = 4;

    readonly string[] planeNames = new string[ProbePlanes];

    ScreenProbeAtlas? atlas;
    ScreenProbeTexture? texture;
    ScreenProbeHistoryTexture? history;
    ReconstructedScreenSurface? surface;
    ScreenProbeUpsampleRenderer? upsample;
    EffectPipelineDescriber? modules;
    Matrix4x4 placedViewProjection = Matrix4x4.Identity;
    Matrix4x4[] forwardMatrices = [];

    BufferHandle depthReadback;
    BufferHandle normalReadback;
    byte[] depthBytes = [];
    byte[] normalBytes = [];
    Matrix4x4[] matrices = [];
    long depthStride;
    long normalStride;
    int slots;
    long frames;
    IGraphicsDevice? owner;
    bool disposed;

    /// <summary>The depth the probes stand on — and the sky test, in its <c>.r</c>.</summary>
    public required string Depth { get; init; }

    /// <summary>The normals the probes are biased along, encoded as the G-buffer stores them.</summary>
    public required string Normals { get; init; }

    /// <summary>The name the upsampled irradiance is published under.</summary>
    public string Output { get; init; } = "PostFx";

    /// <summary>The device everything is created on, or null to take the frame's.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where the upsample's samplers come from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where the upsample's descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>What traces the probes — the host's, with its sources and sky. Null traces nothing.</summary>
    public ScreenProbeTraceFill? Tracer { get; set; }

    /// <summary>What resolves them to spherical harmonics. Null resolves nothing.</summary>
    public ScreenProbeResolve? Resolver { get; set; }

    /// <summary>What folds each frame into the probes' history — the denoiser's temporal half.</summary>
    /// <remarks>
    ///     Null draws the raw resolve, which is the comparison composition. Set, the upsample reads
    ///     the accumulated planes instead, and the driver's camera is fed the matrix the surfaces
    ///     were placed under — a frame older than this one, exactly as the surfaces are.
    /// </remarks>
    public ScreenProbeAccumulateFill? Accumulator { get; set; }

    /// <summary>Why the accumulator recorded nothing last frame, or null.</summary>
    public string? AccumulateSkipped { get; private set; }

    /// <summary>This frame's camera — the inverse of the view-projection the frame draws with.</summary>
    /// <remarks>
    ///     Snapshotted beside the frame's depth copy and used when <i>that copy</i> is placed from,
    ///     <see cref="Latency" /> frames later.
    /// </remarks>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>This frame's camera, forward — what the screen trace projects its samples with.</summary>
    /// <remarks>Only read when <see cref="ScreenTraces" /> is on. The host has both matrices;
    ///     deriving one from the other here would manufacture error.</remarks>
    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Whether the trace's first stage marches the frame's own depth buffer.</summary>
    /// <remarks>
    ///     Off by default, deliberately: the probes' origins come from a placement one
    ///     <see cref="Latency" /> old, and the depth the rays march is this frame's — identical for a
    ///     still scene, sheared by one frame of motion for a moving one. That shear is the
    ///     reprojection problem the denoiser owns, and until it exists this is a choice the host
    ///     makes knowingly rather than a default it inherits.
    /// </remarks>
    public bool ScreenTraces { get; set; }

    /// <summary>How many pixels one probe stands for, along each axis.</summary>
    public int TileSize { get; set; } = 16;

    /// <summary>A multiplier on the upsampled result.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>How many frames between a depth copy and placing probes from it.</summary>
    /// <remarks>
    ///     Zero resolves to <see cref="IGraphicsDevice.FramesInFlight" />, which is free — the host's
    ///     own loop has waited on that frame before reusing its resources. Below it, the caller owns
    ///     the wait: a test that idles the device between frames can run at one.
    /// </remarks>
    public int Latency { get; set; }

    /// <summary>The mirror this node keeps — the atlas texture and the resolved planes.</summary>
    public ScreenProbeTexture? Texture => texture;

    /// <summary>The upsample child, for a host that wants to look at what it drew.</summary>
    public ScreenProbeUpsampleRenderer? Upsample => upsample;

    /// <summary>How many probes stood on a surface at the last placement.</summary>
    public int Placed { get; private set; }

    /// <summary>How many placements have run — zero until the first depth copy comes back.</summary>
    public int Placements { get; private set; }

    /// <summary>Why the tracer recorded nothing last frame, or null.</summary>
    public string? TraceSkipped { get; private set; }

    /// <summary>Why the resolver recorded nothing last frame, or null.</summary>
    public string? ResolveSkipped { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if ((Device ?? frame.Device) is not { } device || Samplers is null || Allocator is null) {
            return;
        }

        var depthTexture = frame.Texture(ToString(), Depth);
        var normalTexture = frame.Texture(ToString(), Normals);
        var depthFormat = frame.FormatOf(ToString(), Depth);
        var normalFormat = frame.FormatOf(ToString(), Normals);

        EnsureLattice(frame.Size);
        EnsureReadback(device, depthFormat, normalFormat);

        texture!.EnsureCreated(device);

        // Place from the copy Latency frames back — a frame the host has finished with — under the
        // camera that drew it.
        var wait = Waits(device);
        var fetch = frames - wait;

        if (fetch >= 0) {
            Place((int)(fetch % slots), depthFormat, normalFormat, device);
            placedViewProjection = forwardMatrices[(int)(fetch % slots)];
        }

        // This frame's snapshot: the copy the readback pass below will fill, and its camera —
        // both halves of it, because placement reconstructs with the inverse and the accumulator
        // reprojects with the forward.
        var slot = (int)(frames % slots);

        matrices[slot] = InverseViewProjection;
        forwardMatrices[slot] = ViewProjection;
        frames++;

        DeclareCompute(frame, device, depthTexture);
        PublishPlanes(frame);
        BuildUpsample(compositor, frame, device);
        DeclareReadback(frame, depthTexture, normalTexture, slot);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        upsample?.Dispose();
        texture?.Dispose();
        history?.Dispose();

        if (owner is { } device) {
            if (depthReadback.IsValid) {
                device.Destroy(depthReadback);
            }

            if (normalReadback.IsValid) {
                device.Destroy(normalReadback);
            }
        }
    }

    int Waits(IGraphicsDevice device) => Latency > 0 ? Latency : Math.Max(1, device.FramesInFlight);

    /// <summary>The lattice, its atlas, its mirror and the reconstruction — sized once, from the frame.</summary>
    void EnsureLattice(Int2 size) {
        if (atlas is null) {
            atlas = new(new(size, TileSize));
            texture = new(atlas) { AtlasIsWritten = true };
            history = new(atlas.Layout);
            surface = new(size);

            for (var plane = 0; plane < ProbePlanes; plane++) {
                planeNames[plane] = $"{this}.Probe{plane}";
            }

            return;
        }

        if (atlas.Layout.Viewport != size) {
            throw new InvalidOperationException(
                $"'{this}' laid its probes over {atlas.Layout.Viewport} and the frame is now {size}. "
                + "Resizing the lattice mid-flight would rebuild textures frames still reference — "
                + "recreate the node instead. A deliberate resize step is owed."
            );
        }
    }

    /// <summary>The readback rings — one region per frame deep enough for the wait.</summary>
    void EnsureReadback(IGraphicsDevice device, PixelFormat depthFormat, PixelFormat normalFormat) {
        var layout = atlas!.Layout;
        var pixels = (long)layout.Viewport.X * layout.Viewport.Y;
        var wanted = Waits(device) + 1;
        var depthSize = pixels * BytesPerPixel(depthFormat, Depth);
        var normalSize = pixels * BytesPerPixel(normalFormat, Normals);

        if (ReferenceEquals(owner, device)
            && depthReadback.IsValid
            && slots == wanted
            && depthBytes.LongLength == depthSize
            && normalBytes.LongLength == normalSize) {
            return;
        }

        if (owner is { } previous) {
            if (depthReadback.IsValid) {
                previous.Destroy(depthReadback);
            }

            if (normalReadback.IsValid) {
                previous.Destroy(normalReadback);
            }
        }

        owner = device;
        slots = wanted;
        frames = 0;
        Placements = 0;
        depthBytes = new byte[depthSize];
        normalBytes = new byte[normalSize];
        matrices = new Matrix4x4[slots];
        forwardMatrices = new Matrix4x4[slots];

        // Two hundred and fifty-six, for the reason every readback ring here aligns to it.
        depthStride = (depthSize + 255) / 256 * 256;
        normalStride = (normalSize + 255) / 256 * 256;

        depthReadback = device.CreateBuffer(
            new(depthStride * slots, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{this}.Depth")
        );

        normalReadback = device.CreateBuffer(
            new(normalStride * slots, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{this}.Normals")
        );
    }

    /// <summary>Reads one slot's copies back, reconstructs, and stands the probes on what it finds.</summary>
    void Place(int slot, PixelFormat depthFormat, PixelFormat normalFormat, IGraphicsDevice device) {
        device.Read(depthReadback, (int)(slot * depthStride), depthBytes);
        device.Read(normalReadback, (int)(slot * normalStride), normalBytes);

        DecodeDepth(depthBytes, depthFormat, surface!.Depth);
        DecodeNormals(normalBytes, normalFormat, surface.Normals);

        surface.InverseViewProjection = matrices[slot];

        var layout = atlas!.Layout;

        Placed = 0;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                if (surface.TrySurface(layout.Anchor(probe), out var position, out var normal)) {
                    atlas.SetSurface(probe, position, normal);
                    Placed++;
                } else {
                    atlas.Invalidate(probe);
                }
            }
        }

        Placements++;
    }

    /// <summary>The compute pass: state the mirror, trace what stands, resolve what was traced.</summary>
    /// <remarks>
    ///     Declared with a side effect because everything it writes — the atlas, the planes — is
    ///     named into descriptor sets rather than declared to the graph, exactly as the irradiance
    ///     field's refill pass argues.
    /// </remarks>
    void DeclareCompute(CompositorFrame frame, IGraphicsDevice device, GraphTexture depth) {
        var screen = ScreenTraces && Tracer is not null;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Compute;
                pass.SideEffect();

                // The screen trace samples this frame's depth, so the graph must order this pass
                // after the one that draws it — without the declared read, the dispatch runs
                // wherever it was declared and marches last frame's texels or none.
                if (screen) {
                    pass.Reads(depth);
                }

                pass.Execute(
                    context => {
                        var commands = context.CommandList;

                        texture!.Upload(device, commands);

                        if (Tracer is { } tracer) {
                            // Imposed, not configured: the resolve reads validity out of every
                            // patch, and an unplaced probe's patch is undefined unless a job
                            // clears it.
                            tracer.ClearInvalid = true;

                            if (screen) {
                                tracer.ScreenDepth = context.View(depth);
                                tracer.ScreenViewport = atlas!.Layout.Viewport;
                                tracer.ViewProjection = ViewProjection;
                            } else {
                                tracer.ScreenDepth = default;
                            }

                            tracer.Record(commands, texture);
                            TraceSkipped = tracer.Skipped;
                        }

                        if (Resolver is { } resolver) {
                            resolver.Record(commands, texture);
                            ResolveSkipped = resolver.Skipped;
                        }

                        if (Accumulator is { } accumulator) {
                            // The surfaces are a placement old, so the camera handed over is the
                            // one they were placed under — not this frame's.
                            accumulator.ViewProjection = placedViewProjection;
                            accumulator.Record(commands, atlas!, texture, history!);
                            AccumulateSkipped = accumulator.Skipped;
                        }
                    }
                );
            }
        );
    }

    /// <summary>The resolved planes, into the frame's namespace as imports.</summary>
    /// <remarks>
    ///     Imports and not parameter writes, for the reason the first drawn frame found: a
    ///     full-screen pass's textures resolve through the graph and nothing else. Entry and exit
    ///     pinned to <see cref="ResourceState.ShaderRead" /> so the graph does not transition what
    ///     the resolve's own barriers manage.
    /// </remarks>
    void PublishPlanes(CompositorFrame frame) {
        // With an accumulator, the upsample reads the history's back set — the set the swap makes
        // front by the time the pass draws. Without one, the raw resolve, which is the comparison
        // composition.
        var accumulated = Accumulator is not null;

        if (accumulated) {
            history!.EnsureCreated((Device ?? frame.Device)!);
        }

        for (var plane = 0; plane < ProbePlanes; plane++) {
            frame.Add(
                planeNames[plane],
                frame.Graph.ImportTexture(
                    accumulated ? history!.BackTexture(plane) : texture!.ProbePlane(plane),
                    accumulated ? history!.BackView(plane) : texture!.ProbeView(plane),
                    accumulated ? history!.PlaneDescription : texture!.ProbePlaneDescription,
                    ResourceState.ShaderRead,
                    ResourceState.ShaderRead
                ),
                PixelFormat.Rgba32Float
            );
        }
    }

    /// <summary>The upsample, built as a child over the planes just published.</summary>
    void BuildUpsample(GraphicsCompositor compositor, CompositorFrame frame, IGraphicsDevice device) {
        upsample ??= new() {
            Name = $"{this}.Upsample",
            Depth = Depth,
            Normals = Normals,
            Output = Output,
            Probes = texture!,
            Planes = planeNames
        };

        upsample.Modules = modules ??= new(device);
        upsample.Device = device;
        upsample.Samplers = Samplers;
        upsample.Allocator = Allocator;
        upsample.Intensity = Intensity;

        BuildChild(upsample, compositor, frame);
    }

    /// <summary>The copies the next placement reads — declared, so the graph orders and keeps them.</summary>
    void DeclareReadback(CompositorFrame frame, GraphTexture depth, GraphTexture normals, int slot) {
        var layout = atlas!.Layout;
        var depthOffset = slot * depthStride;
        var normalOffset = slot * normalStride;

        frame.Graph.AddPass(
            $"{this}.Readback",
            pass => {
                pass.Kind = PassKind.Transfer;
                pass.Reads(depth, ResourceState.CopySource);
                pass.Reads(normals, ResourceState.CopySource);
                pass.SideEffect();

                pass.Execute(
                    context => {
                        var commands = context.CommandList;

                        commands.CopyTextureToBuffer(
                            new TextureRegion(context.Texture(depth)),
                            new(layout.Viewport.X, layout.Viewport.Y, 1),
                            depthReadback,
                            depthOffset
                        );

                        commands.CopyTextureToBuffer(
                            new TextureRegion(context.Texture(normals)),
                            new(layout.Viewport.X, layout.Viewport.Y, 1),
                            normalReadback,
                            normalOffset
                        );
                    }
                );
            }
        );
    }

    /// <summary>How wide one pixel of a readable target is, or a refusal naming the format.</summary>
    long BytesPerPixel(PixelFormat format, string name) =>
        format switch {
            PixelFormat.Rgba32Float => 16,
            PixelFormat.Rgba8UNorm => 4,
            _ => throw new CompositorBindingException(
                ToString(),
                "target",
                name,
                $"is {format}, which placement cannot read back. Rgba32Float and Rgba8UNorm are what "
                + "the reconstruction decodes; widening the list is a case in one switch"
            )
        };

    static void DecodeDepth(ReadOnlySpan<byte> data, PixelFormat format, Span<float> depth) {
        if (format == PixelFormat.Rgba32Float) {
            var floats = MemoryMarshal.Cast<byte, float>(data);

            for (var i = 0; i < depth.Length; i++) {
                depth[i] = floats[i * 4];
            }
        } else {
            for (var i = 0; i < depth.Length; i++) {
                depth[i] = data[i * 4] / 255f;
            }
        }
    }

    static void DecodeNormals(ReadOnlySpan<byte> data, PixelFormat format, Span<Vector4> normals) {
        if (format == PixelFormat.Rgba32Float) {
            var texels = MemoryMarshal.Cast<byte, Vector4>(data);

            texels[..normals.Length].CopyTo(normals);
        } else {
            for (var i = 0; i < normals.Length; i++) {
                var at = i * 4;

                normals[i] = new(data[at] / 255f, data[at + 1] / 255f, data[at + 2] / 255f, data[at + 3] / 255f);
            }
        }
    }
}
