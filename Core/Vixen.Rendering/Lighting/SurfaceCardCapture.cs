// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.SurfaceCache;

namespace Vixen.Rendering.Lighting;

/// <summary>Which of a card's surface planes a pass is drawing.</summary>
public enum SurfaceCapturePlane {
    /// <summary>What fraction of each channel the surface reflects.</summary>
    Albedo,

    /// <summary>Which way it faces, in world space, as signed floats.</summary>
    Normal,

    /// <summary>What it emits, as radiance.</summary>
    Emissive
}

/// <summary>Rasterises one card's capture — the device half of doc 19 § L4's runtime capture.</summary>
/// <remarks>
///     <para>
///         <b>The piece <c>TracedCardCapture</c> exists to referee.</b> That one marches a distance
///         field and is deterministic to the last texel; this one renders whatever the caller draws,
///         reads it back, and writes the same <see cref="SurfaceCacheStore" /> texels — so the two
///         can capture one scene and be compared, the arrangement every capture in this engine has
///         with its reference, in <see cref="IrradianceCubeCapture" />'s exact mould.
///     </para>
///     <para>
///         <b>Three passes over one target rather than one pass over three.</b> A surface texel is
///         albedo, normal, depth and emissive; multiple render targets would take them in one pass
///         and need every material pipeline built against a three-attachment output. Drawing the
///         card three times into the one attachment the scene's pipelines already target — told
///         apart by <see cref="SurfaceCapturePlane" /> — captures the same texels at three small
///         passes per card, and a card is tens of texels across at a budgeted handful per frame.
///         The single-pass capture is an optimisation with this as its baseline and its referee.
///     </para>
///     <para>
///         <b>The projection is derived from the card, not from a camera.</b>
///         <see cref="Projection" /> maps the card's box to clip space so that framebuffer texel
///         (x, y) <i>is</i> card texel (x, y) — one convention, asserted by a closed form against
///         <see cref="SurfaceCard.TexelOrigin" /> — with the engine's reversed depth: one at the
///         card's near plane, zero at its far side, and exactly zero meaning "saw nothing", which is
///         what marks a texel invalid at readback.
///     </para>
///     <para>
///         ⚠ <b>Two-sided rasterisation is the caller's to arrange, and it matters</b> — a card
///         looks at the world from outside its surfaces, but a floor's underside card looks at back
///         faces, and culled away it captures an empty world. The same warning, verbatim, as the
///         cube capture's.
///     </para>
/// </remarks>
public sealed class SurfaceCardCapture : IDisposable {
    /// <summary>Draws the scene into one plane of the card's capture.</summary>
    /// <param name="commands">The open command list, inside the pass.</param>
    /// <param name="plane">Which plane is being drawn — what the fragment colour must mean.</param>
    /// <param name="viewProjection">The card's world-to-clip transform.</param>
    public delegate void DrawCard(ICommandList commands, SurfaceCapturePlane plane, Matrix4x4 viewProjection);

    /// <summary>The attachments a plane is rendered into, for whoever builds the pipelines.</summary>
    /// <remarks>The cube capture's exact pair, for the cube capture's exact reason: a pipeline whose
    ///     formats disagree with the pass is undefined behaviour, not a validation failure.</remarks>
    public static RenderOutput Output { get; } = new([PixelFormat.Rgba32Float], PixelFormat.Depth32Float);

    /// <summary>The planes, in the order they are drawn and stored.</summary>
    static readonly SurfaceCapturePlane[] Planes = [
        SurfaceCapturePlane.Albedo, SurfaceCapturePlane.Normal, SurfaceCapturePlane.Emissive
    ];

    const int ColourStride = 16;
    const int DepthStride = 4;

    readonly IGraphicsDevice device;

    TextureHandle colour;
    TextureHandle depth;
    TextureViewHandle colourView;
    TextureViewHandle depthView;
    BufferHandle colourReadback;
    BufferHandle depthReadback;

    float[] colours = [];
    float[] depths = [];
    SurfaceCard recorded;
    bool hasRecorded;
    bool created;
    bool transitioned;
    bool disposed;

    /// <summary>Creates a capture on a device. Nothing is allocated until the first record.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public SurfaceCardCapture(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
    }

    /// <summary>The widest card this capture can take, in texels along either axis.</summary>
    /// <remarks><c>CardGenerator</c>'s own ceiling by default. The targets are made once at this
    ///     size and every card renders into their corner — a card is a viewport, not a texture.</remarks>
    public int MaxResolution { get; init; } = 64;

    /// <summary>How many captures have been recorded.</summary>
    public int Captures { get; private set; }

    /// <summary>Whether the textures exist yet.</summary>
    public bool IsCreated => created;

    /// <summary>The card's world-to-clip transform: card texel (x, y) is framebuffer texel (x, y).</summary>
    /// <param name="card">The card.</param>
    /// <remarks>
    ///     Clip x is the card's U over its half-extent, clip y its V — the cyclic frame, so the
    ///     rasteriser and <see cref="SurfaceCard.TryProject" /> cannot disagree about which way U
    ///     runs — and clip z is the engine's reversed depth over the box: one at the near plane,
    ///     zero at the far side. Row-vector convention, like every matrix the engine hands a shader.
    /// </remarks>
    public static Matrix4x4 Projection(in SurfaceCard card) {
        var (plane, half) = card.Extents;
        var direction = card.Direction;
        var sign = Component(direction, card.Axis / 2);

        Span<float> rows = stackalloc float[16];

        rows[(card.UComponent * 4) + 0] = 1f / plane.X;
        rows[(card.VComponent * 4) + 1] = 1f / plane.Y;
        rows[((card.Axis / 2) * 4) + 2] = sign / (2f * half);
        rows[12] = -Component(card.Centre, card.UComponent) / plane.X;
        rows[13] = -Component(card.Centre, card.VComponent) / plane.Y;
        rows[14] = (half - (sign * Component(card.Centre, card.Axis / 2))) / (2f * half);
        rows[15] = 1f;

        return new(
            new Vector4(rows[0], rows[1], rows[2], rows[3]),
            new Vector4(rows[4], rows[5], rows[6], rows[7]),
            new Vector4(rows[8], rows[9], rows[10], rows[11]),
            new Vector4(rows[12], rows[13], rows[14], rows[15])
        );
    }

    /// <summary>Records the three passes that capture one card.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="card">The card to capture.</param>
    /// <param name="draw">What to draw into each plane.</param>
    /// <exception cref="ArgumentNullException">There is no command list or no draw.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The card is wider than <see cref="MaxResolution" />.</exception>
    /// <remarks>One card per record-and-read cycle, reusing one pair of targets — the cube capture's
    ///     arrangement, for its reason: this runs a budgeted handful of times per frame.</remarks>
    public void Record(ICommandList commands, in SurfaceCard card, DrawCard draw) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(card.Resolution.X, MaxResolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(card.Resolution.Y, MaxResolution);

        Create();

        // Once, and only the first list needs it — the cube capture's barrier, for its reason.
        if (!transitioned) {
            transitioned = true;

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(colour, ResourceState.Undefined, ResourceState.ColourTarget),
                        new TextureBarrier(depth, ResourceState.Undefined, ResourceState.DepthStencilWrite)
                    ]
                )
            );
        }

        var resolution = card.Resolution;
        var size = new Int3(resolution.X, resolution.Y, 1);
        var projection = Projection(card);
        var texels = (long)MaxResolution * MaxResolution;

        for (var pass = 0; pass < Planes.Length; pass++) {
            commands.BeginRenderPass(
                new(
                    [new ColourAttachment(colourView, LoadAction.Clear, StoreAction.Store, default)],

                    // Cleared to zero, which is FAR: the engine's depth is reversed, and zero at
                    // readback is the mark of a texel that saw nothing.
                    new DepthStencilAttachment(depthView),
                    $"SurfaceCardCapture {Planes[pass]}"
                )
            );

            commands.SetViewport(new(0, 0, resolution.X, resolution.Y));
            commands.SetScissor(ScissorRect.Full(new(resolution.X, resolution.Y)));

            draw(commands, Planes[pass], projection);

            commands.EndRenderPass();

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(colour, ResourceState.ColourTarget, ResourceState.CopySource),
                        new TextureBarrier(depth, ResourceState.DepthStencilWrite, ResourceState.CopySource)
                    ]
                )
            );

            commands.CopyTextureToBuffer(new(colour), size, colourReadback, pass * texels * ColourStride);

            // The three passes draw one geometry, so any pass's depth is the depth — the last one
            // is simply the one still in the target when the loop ends.
            if (pass == Planes.Length - 1) {
                commands.CopyTextureToBuffer(new(depth), size, depthReadback, 0);
            }

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(colour, ResourceState.CopySource, ResourceState.ColourTarget),
                        new TextureBarrier(depth, ResourceState.CopySource, ResourceState.DepthStencilWrite)
                    ]
                )
            );
        }

        recorded = card;
        hasRecorded = true;
        Captures++;
    }

    /// <summary>Turns the last recorded capture into a card's texels.</summary>
    /// <param name="cache">The cache to write.</param>
    /// <param name="card">The card, by index — its shape must be the recorded one.</param>
    /// <param name="captured">How many texels captured a surface.</param>
    /// <returns>False when nothing has been recorded, or the card is not the recorded shape.</returns>
    /// <exception cref="ArgumentNullException">There is no cache.</exception>
    /// <remarks>
    ///     ⚠ The caller has to have submitted the list and waited — the cube capture's warning,
    ///     verbatim, and the same worst kind of wrong when ignored: a buffer read early holds zeros,
    ///     which decode as a card that saw nothing, which is a plausible answer.
    /// </remarks>
    public bool TryRead(SurfaceCacheStore cache, int card, out int captured) {
        ArgumentNullException.ThrowIfNull(cache);
        ObjectDisposedException.ThrowIf(disposed, this);

        captured = 0;

        if (!hasRecorded || cache.Cards[card].Card != recorded) {
            return false;
        }

        device.Read(colourReadback, 0, MemoryMarshal.AsBytes(colours.AsSpan()));
        device.Read(depthReadback, 0, MemoryMarshal.AsBytes(depths.AsSpan()));

        var shape = recorded;
        var resolution = shape.Resolution;
        var (_, half) = shape.Extents;
        var texels = MaxResolution * MaxResolution;

        for (var y = 0; y < resolution.Y; y++) {
            // ⚠ The one flip in the whole path, and it is the cube capture's flip for the cube
            // capture's reason: the engine's clip space has +Y up, so the framebuffer's first row is
            // v = +1 while card texel (0, 0) is the low corner of the near plane.
            var row = resolution.Y - 1 - y;

            for (var x = 0; x < resolution.X; x++) {
                var texel = new Int2(x, y);
                var source = (row * resolution.X) + x;
                var deviceDepth = depths[source];

                // Exactly zero is the cleared far plane — a direction that hit nothing. A real
                // surface on the far plane itself reads the same, which is the floor this shares
                // with every reversed-depth readback in the engine.
                if (deviceDepth <= 0f) {
                    cache.Invalidate(card, texel);

                    continue;
                }

                var albedo = Colour(0, texels, source);
                var normal = Colour(1, texels, source);
                var emissive = Colour(2, texels, source);

                cache.SetSurface(
                    card,
                    texel,
                    new(
                        albedo,
                        Vector3.Normalize(normal),
                        (1f - deviceDepth) * 2f * half,
                        emissive
                    )
                );

                captured++;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (!created) {
            return;
        }

        device.Destroy(colourView);
        device.Destroy(depthView);
        device.Destroy(colour);
        device.Destroy(depth);
        device.Destroy(colourReadback);
        device.Destroy(depthReadback);
    }

    Vector3 Colour(int plane, int texels, int source) {
        var at = ((plane * texels) + source) * 4;

        return new(colours[at], colours[at + 1], colours[at + 2]);
    }

    static float Component(Vector3 value, int index) => index == 0 ? value.X : index == 1 ? value.Y : value.Z;

    void Create() {
        if (created) {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResolution, 1);

        var texels = (long)MaxResolution * MaxResolution;

        colour = device.CreateTexture(
            new() {
                Width = MaxResolution, Height = MaxResolution, Depth = 1, MipLevels = 1, ArrayLayers = 1,
                SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = Output.ColourFormats[0],
                Usage = TextureUsage.ColourTarget | TextureUsage.CopySource,
                Name = "SurfaceCardCapture.Colour"
            }
        );

        depth = device.CreateTexture(
            new() {
                Width = MaxResolution, Height = MaxResolution, Depth = 1, MipLevels = 1, ArrayLayers = 1,
                SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = Output.DepthFormat,
                Usage = TextureUsage.DepthStencilTarget | TextureUsage.CopySource,
                Name = "SurfaceCardCapture.Depth"
            }
        );

        colourView = device.CreateTextureView(colour);
        depthView = device.CreateTextureView(depth);

        colourReadback = device.CreateBuffer(
            new(texels * Planes.Length * ColourStride, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "SurfaceCardCapture.Colour")
        );

        depthReadback = device.CreateBuffer(
            new(texels * DepthStride, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "SurfaceCardCapture.Depth")
        );

        colours = new float[texels * Planes.Length * 4];
        depths = new float[texels];
        created = true;
    }
}
