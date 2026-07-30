// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Lighting;

/// <summary>Renders the six faces of a probe's cube — the device half of doc 19 § L2's filler B.</summary>
/// <remarks>
///     <para>
///         <b>The piece that was owed.</b> <see cref="CapturedIrradianceFiller" /> turns a cube into
///         four coefficients and is checked against arithmetic with nothing rendered; this is what
///         produces the cube, and until it existed doc 19 § 7's promise to WebGL2 — the same lighting
///         model at a different update rate — was stated rather than true, because a target with no
///         compute has no other way to fill a probe.
///     </para>
///     <para>
///         <b>Six 90° frusta from <see cref="ShadowProjections.Cube" />, which is not a coincidence.</b>
///         <see cref="CubeMapping.Direction" /> is <i>derived</i> from that same matrix by
///         unprojecting it, so the direction a projected texel came from and the direction the
///         projection asks about are the same function evaluated twice rather than two conventions
///         that have to be kept in step. A capture that renders with one convention and integrates
///         with another comes back mirrored, which still looks like an environment.
///     </para>
///     <para>
///         <b>It draws nothing itself.</b> What a probe sees is the scene shaded — and how a project
///         shades is a pipeline decision, not this type's. So the caller supplies a
///         <see cref="DrawFace" />, and doc 19's bounce iteration is the same callback shading with
///         the field's previous answer: a source that reads the field produces a second bounce by
///         being called twice.
///     </para>
///     <para>
///         ⚠ <b>Two-sided rasterisation is the caller's to arrange, and it matters.</b> A probe inside
///         a closed room sees the room's inside faces, which are back faces to a camera standing in
///         it. Culled away, the room is invisible and the probe reports an unobstructed sky — the
///         brightest possible wrong answer, and the one <see cref="IrradianceCapture.Validity" /> is
///         then unable to catch, because a probe that sees nothing looks exactly like a probe in the
///         open.
///     </para>
/// </remarks>
public sealed class IrradianceCubeCapture : IDisposable {
    /// <summary>Draws the scene into one face.</summary>
    /// <param name="commands">The open command list, inside the face's render pass.</param>
    /// <param name="face">Which face is being drawn.</param>
    /// <param name="viewProjection">Its world-to-clip transform.</param>
    public delegate void DrawFace(ICommandList commands, CubeFace face, Matrix4x4 viewProjection);

    /// <summary>The attachments a face is rendered into, for whoever builds the pipelines.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A caller cannot guess these, and guessing wrong is not an error.</b> A pipeline whose
    ///         attachment formats disagree with the render pass it is used in is undefined behaviour
    ///         rather than a validation failure on most drivers — it faults the GPU and takes the
    ///         process with it, which is what a <see cref="DrawFace" /> drawing through
    ///         <c>RenderSystem</c> does when its <c>RenderDrawContext.Output</c> is left at its default
    ///         of no attachments at all.
    ///     </para>
    ///     <para>
    ///         So the formats are part of the callback's contract and are published rather than
    ///         private. A caller building pipelines by hand passes them to the pipeline description; a
    ///         caller drawing a scene puts this in the draw context and the describer does the rest.
    ///     </para>
    /// </remarks>
    public static RenderOutput Output { get; } = new([PixelFormat.Rgba32Float], PixelFormat.Depth32Float);

    /// <summary>What a colour texel weighs, and it is four floats rather than four bytes.</summary>
    /// <remarks>
    ///     <b>A bake is the one place radiance must not be clamped.</b> A cube read back through an
    ///     8-bit target has every value above one flattened to one, and a probe beside a bright window
    ///     is exactly the probe whose answer that destroys — the constant band comes back at the
    ///     window's clamped value rather than its real one, and a second bounce carries the error on.
    /// </remarks>
    const int ColourStride = 16;

    /// <summary>What a depth texel weighs.</summary>
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

    /// <summary>How far what each texel saw is, in CubeImage's own row order.</summary>
    float[] distances = [];
    bool created;
    bool transitioned;
    bool recorded;
    bool disposed;

    /// <summary>Creates a capture on a device. Nothing is allocated until the first record.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public IrradianceCubeCapture(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
    }

    /// <summary>How many texels across one face is.</summary>
    /// <remarks>
    ///     <para>
    ///         Sixteen, which is 1536 texels over the sphere — twenty-four times filler A's
    ///         sixty-four rays, and it costs a raster rather than a march. The payload is four
    ///         coefficients either way, so past a point more texels buy nothing: an L1 band cannot
    ///         represent anything sharper than a cosine lobe.
    ///     </para>
    ///     <para>
    ///         What it does buy is the <i>hit</i> information — <see cref="IrradianceCapture.Validity" />
    ///         and the sun tap read the depth, and those are per-direction rather than projected, so
    ///         they are the reason not to go lower.
    ///     </para>
    /// </remarks>
    public int Size { get; init; } = 16;

    /// <summary>How far a face looks before it stops seeing, which is its far plane.</summary>
    public float Range { get; set; } = 100f;

    /// <summary>Its near plane.</summary>
    /// <remarks>
    ///     ⚠ Also the floor on what a capture can tell you about being buried. Geometry nearer than
    ///     this is clipped away and reads as sky, so a near plane larger than
    ///     <see cref="MinimumDistance" /> makes a probe inside a wall come back valid and bright.
    /// </remarks>
    public float Near { get; set; } = 0.01f;

    /// <summary>What a direction that hit nothing sees.</summary>
    /// <remarks>
    ///     The pass's clear colour, which is the sky as far as a probe is concerned. It is here rather
    ///     than in the draw callback because the clear happens before the callback runs.
    /// </remarks>
    public Color4 Sky { get; set; }

    /// <summary>How near a hit has to be before it means "this probe is inside something".</summary>
    /// <remarks>
    ///     <para>
    ///         In world units, and it wants to be about half a probe spacing — the distance at which
    ///         "the geometry is beside me" becomes "the geometry is where I am". A field cannot tell
    ///         this capture its spacing, because the same source fills bricks of several sizes, so it
    ///         is set by whoever owns both.
    ///     </para>
    ///     <para>
    ///         <b>Distance rather than "did it hit a back face", which is what a ray-traced filler
    ///         would use.</b> A raster capture has no cheap way to know a face's winding at readback,
    ///         and the distance test answers the question that actually matters: a probe in a room
    ///         sees the room's walls at room scale and a probe in a wall sees that wall at nothing.
    ///     </para>
    /// </remarks>
    public float MinimumDistance { get; set; } = 0.1f;

    /// <summary>Which way the sun is, in world space.</summary>
    public Vector3 SunDirection { get; set; } = new(0f, 1f, 0f);

    /// <summary>How wide a cone the sun tap averages over, in radians.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nine taps rather than one, and the width is why.</b> A single tap along the sun
    ///         gives a probe a shadow of exactly zero or one, and a field of those interpolates into
    ///         visible steps at the probe spacing. Averaging a ring around the sun gives ten levels,
    ///         which is what filler A's soft march produces continuously.
    ///     </para>
    ///     <para>
    ///         An eighth of a radian by default, matching <c>IrradianceFieldFill.SunSoftness</c>'s
    ///         reciprocal of eight — the two fillers should disagree about the softness of a shadow no
    ///         more than they disagree about its light.
    ///     </para>
    /// </remarks>
    public float SunRadius { get; set; } = 0.125f;

    /// <summary>How many captures have been recorded.</summary>
    public int Captures { get; private set; }

    /// <summary>Whether the textures exist yet.</summary>
    public bool IsCreated => created;

    /// <summary>Records the six passes that capture one probe's surroundings.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="position">Where the probe stands.</param>
    /// <param name="draw">What to draw into each face.</param>
    /// <exception cref="ArgumentNullException">There is no command list or no draw.</exception>
    /// <remarks>
    ///     One pass and one pair of copies per face, into one target reused six times. Six targets
    ///     would let the copies batch, and would cost six times the memory for a saving no bake can
    ///     measure — this runs once per probe at build time, not once per frame.
    /// </remarks>
    public void Record(ICommandList commands, Vector3 position, DrawFace draw) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(disposed, this);

        Create();

        // ⚠ Once, and only the first list needs it. A freshly created image is UNDEFINED, and while a
        // colour attachment loaded with Clear may begin there, a depth attachment may not — the
        // validation layer refuses the submit, and without it the driver reads a layout that has no
        // contents. Every later face is left in these two states by the pair of barriers below, which
        // is why this cannot simply live at the top of the loop.
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

        var size = new Int3(Size, Size, 1);

        foreach (var face in CubeMapping.Faces) {
            commands.BeginRenderPass(
                new(
                    [new ColourAttachment(colourView, LoadAction.Clear, StoreAction.Store, Sky)],

                    // Cleared to zero, which is FAR: the engine's depth is reversed. Clearing to one
                    // here is the classic mistake and depth-tests the whole scene away, which in a
                    // capture reads as a probe standing in an empty world.
                    new DepthStencilAttachment(depthView),
                    $"IrradianceCubeCapture {face}"
                )
            );

            commands.SetViewport(new(0, 0, Size, Size));
            commands.SetScissor(ScissorRect.Full(new(Size, Size)));

            draw(commands, face, ShadowProjections.Cube(position, face, Range, Near));

            commands.EndRenderPass();

            var offset = (long)(int)face * Size * Size;

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(colour, ResourceState.ColourTarget, ResourceState.CopySource),
                        new TextureBarrier(depth, ResourceState.DepthStencilWrite, ResourceState.CopySource)
                    ]
                )
            );

            commands.CopyTextureToBuffer(new(colour), size, colourReadback, offset * ColourStride);
            commands.CopyTextureToBuffer(new(depth), size, depthReadback, offset * DepthStride);

            // Back, because the next face renders into the same two textures. The last iteration's
            // transition is wasted and is not worth a branch to avoid.
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

        recorded = true;
        Captures++;
    }

    /// <summary>Turns the last recorded capture into the three numbers a probe wants.</summary>
    /// <param name="capture">What was seen.</param>
    /// <returns>False when nothing has been recorded, or the submit has not completed.</returns>
    /// <remarks>
    ///     ⚠ The caller has to have submitted the list and waited. Nothing here can check that: a
    ///     buffer read before its copy has run holds whatever it held, which for a fresh allocation is
    ///     zeros — a probe in the dark, which is a plausible answer and therefore the worst kind of
    ///     wrong.
    /// </remarks>
    public bool TryRead(out IrradianceCapture capture) {
        ObjectDisposedException.ThrowIf(disposed, this);

        capture = default;

        if (!recorded) {
            return false;
        }

        device.Read(colourReadback, 0, MemoryMarshal.AsBytes(colours.AsSpan()));
        device.Read(depthReadback, 0, MemoryMarshal.AsBytes(depths.AsSpan()));

        var image = new CubeImage(Size);
        var seen = 0f;
        var open = 0f;

        foreach (var face in CubeMapping.Faces) {
            var (forward, _) = ShadowProjections.CubeAxes(face);

            for (var y = 0; y < Size; y++) {
                // ⚠ The one flip in the whole path, and it is not arbitrary. The engine's clip space
                // has +Y up — Core/Vixen.Core.Mathematics/Conventions.md, expressed by the Vulkan
                // backend as a negative-height viewport — so the framebuffer's first row is v = +1,
                // while CubeImage's first row is v = −1. Without this the capture is mirrored
                // vertically, which leaves the constant band untouched and inverts one of the three
                // linear ones: a probe lit from above reports light from below, and every test that
                // integrates a uniform sky still passes.
                var row = Size - 1 - y;

                for (var x = 0; x < Size; x++) {
                    var source = (((int)face * Size) + row) * Size + x;
                    var target = (((int)face * Size) + y) * Size + x;
                    var rgba = source * 4;

                    image.At(face, x, y) = new(colours[rgba], colours[rgba + 1], colours[rgba + 2]);

                    // Solid-angle weighted, for the same reason the projection is: a corner texel
                    // covers about a fifth of what a centre one does, and a validity that counted
                    // texels would let the corners outvote the direction the probe is actually facing.
                    var weight = image.SolidAngleOf(x, y);

                    distances[target] = Distance(depths[source], Vector3.Dot(forward, image.DirectionOf(face, x, y)));
                    seen += weight;

                    if (distances[target] > MinimumDistance) {
                        open += weight;
                    }
                }
            }
        }

        capture = new(image, seen > 0f ? open / seen : 0f, SunShadow());

        return true;
    }

    /// <summary>How far away what a texel saw is, in world units, or infinity for the sky.</summary>
    /// <remarks>
    ///     <para>
    ///         Two conversions, and skipping either is a distance that is wrong by a factor nobody
    ///         notices. The buffer holds a <i>reversed</i> device depth — one at the near plane, zero
    ///         at the far one — so the linearisation is not the textbook formula; and what that
    ///         yields is the distance along the face's <i>axis</i> rather than along the ray, which at
    ///         a face's corner differs by the √3 that makes a cube a cube.
    ///     </para>
    ///     <para>
    ///         Zero is the cleared far plane, which is a direction that hit nothing. Infinity rather
    ///         than <see cref="Range" />, so a caller cannot accidentally treat "saw sky" as "saw
    ///         something very distant" — those differ for a probe under an overhang.
    ///     </para>
    /// </remarks>
    float Distance(float deviceDepth, float along) {
        if (deviceDepth <= 0f || along <= 0f) {
            return float.PositiveInfinity;
        }

        var axial = Near * Range / MathF.Max(Near + (deviceDepth * (Range - Near)), 1e-6f);

        return axial / along;
    }

    /// <summary>How much of the sun reaches the probe, averaged over a small cone around it.</summary>
    float SunShadow() {
        var sun = Vector3.Normalize(SunDirection);

        if (!float.IsFinite(sun.X) || sun.LengthSquared() < 0.5f) {
            return 1f;
        }

        // Any vector not parallel to the sun gives a tangent basis, and the axis furthest from it is
        // the one guaranteed not to be.
        var away = MathF.Abs(sun.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(away, sun));
        var up = Vector3.Cross(sun, right);

        var lit = Tap(sun);

        for (var step = 0; step < 8; step++) {
            var angle = step * MathF.Tau / 8f;
            var offset = (right * MathF.Cos(angle)) + (up * MathF.Sin(angle));

            lit += Tap(Vector3.Normalize(sun + (offset * MathF.Tan(SunRadius))));
        }

        return lit / 9f;
    }

    /// <summary>Whether one direction reached the sky, read out of the distances.</summary>
    /// <remarks>
    ///     <para>
    ///         Through <see cref="CubeMapping.Locate" /> rather than by walking a face, because a ring
    ///         around the sun crosses face boundaries whenever the sun is near a cube edge — and a tap
    ///         clamped inside the wrong face is a shadow that changes as the world rotates under a sun
    ///         that did not move.
    ///     </para>
    ///     <para>
    ///         Off <see cref="distances" /> rather than off the raw depths, and that is why the flip
    ///         in <see cref="TryRead" /> writes a second array rather than reading one: <c>Locate</c>
    ///         answers in <see cref="CubeImage" />'s coordinates, and a tap into framebuffer rows
    ///         would be upside down in exactly the half of the sky the sun is usually in.
    ///     </para>
    /// </remarks>
    float Tap(Vector3 direction) {
        var (face, u, v) = CubeMapping.Locate(direction);

        var x = Math.Clamp((int)((u + 1f) * 0.5f * Size), 0, Size - 1);
        var y = Math.Clamp((int)((v + 1f) * 0.5f * Size), 0, Size - 1);

        return float.IsPositiveInfinity(distances[(((int)face * Size) + y) * Size + x]) ? 1f : 0f;
    }

    /// <summary>Makes the two targets and the two readbacks, once.</summary>
    void Create() {
        if (created) {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(Size, 1);

        var texels = 6 * Size * Size;

        colour = device.CreateTexture(
            new() {
                Width = Size, Height = Size, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = Output.ColourFormats[0],
                Usage = TextureUsage.ColourTarget | TextureUsage.CopySource,
                Name = "IrradianceCubeCapture.Colour"
            }
        );

        depth = device.CreateTexture(
            new() {
                Width = Size, Height = Size, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = Output.DepthFormat,
                Usage = TextureUsage.DepthStencilTarget | TextureUsage.CopySource,
                Name = "IrradianceCubeCapture.Depth"
            }
        );

        colourView = device.CreateTextureView(colour);
        depthView = device.CreateTextureView(depth);

        colourReadback = device.CreateBuffer(
            new((long)texels * ColourStride, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "IrradianceCubeCapture.Colour")
        );

        depthReadback = device.CreateBuffer(
            new((long)texels * DepthStride, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "IrradianceCubeCapture.Depth")
        );

        colours = new float[texels * 4];
        depths = new float[texels];
        distances = new float[texels];
        created = true;
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

        device.Destroy(colour);
        device.Destroy(depth);
        device.Destroy(colourReadback);
        device.Destroy(depthReadback);
    }
}
