// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Materials;

namespace Vixen.Rendering;

/// <summary>One corner of a shape that lives on the device.</summary>
/// <param name="Position">Where it is, in the shape's own space.</param>
/// <param name="Normal">Which way the surface faces there. Expected to be unit length.</param>
/// <remarks>
///     Twenty-four bytes, and no colour — which is the difference from <see cref="MeshVertex" /> and
///     the reason both exist. A colour per vertex is what lets one buffer hold twenty objects each in
///     its own colour <em>when the buffer is rewritten every frame</em>. Here the vertices are
///     uploaded once and shared by every entity that has this shape, so a colour on them would be a
///     colour every instance had to agree about — it belongs to <see cref="MeshInstance" />.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshShapeVertex(Vector3 Position, Vector3 Normal);

/// <summary>One entity's copy of a shape: where it is, and what it looks like.</summary>
/// <param name="Transform">Where the entity is, as <c>WorldTransform</c> holds it.</param>
/// <param name="Normals">
///     The matrix normals go through, which is <paramref name="Transform" />'s inverse transpose. See
///     <see cref="Of" />, which is the only reason a caller would build one by hand.
/// </param>
/// <param name="Colour">What colour the surface is, linear and straight-alpha.</param>
/// <param name="Style">
///     Four independent decisions about how this instance is drawn, described by the shader that reads
///     them: an outline width in pixels, an outline depth bias in pixels, whether to light it flat,
///     and whether to take its colour from the world normal instead of from
///     <paramref name="Colour" />.
/// </param>
/// <param name="Surface">
///     How the light behaves on it: metalness in x, perceptual roughness in y. The remaining two lanes
///     are reserved — see <see cref="Materials.MaterialSurface.DielectricF0" /> for what goes in them
///     when something authors it.
/// </param>
/// <param name="Emissive">
///     What it emits on its own, linear, with the material's intensity already folded into the
///     components. Alpha is unused. Black for a surface that emits nothing, which is nearly all of them.
/// </param>
/// <remarks>
///     <para>
///         <b>A hundred and ninety-two bytes per entity per frame, and that number is the whole
///         point.</b> The path this replaces cost one <see cref="MeshVertex" /> per vertex per frame —
///         forty bytes times a shape's vertex count, times every entity, with a cube's four hundred
///         bytes and a sphere's twenty-odd kilobytes rebuilt whether or not anything moved. What crosses
///         the bus now is linear in <em>entities</em>.
///     </para>
///     <para>
///         ⚠ <b><paramref name="Surface" /> and <paramref name="Emissive" /> are the material, and they
///         are per instance because a material is per entity.</b> Two entities sharing a shape and
///         differing only in what they are made of stay one draw, which is what would have been lost by
///         putting them anywhere else — a uniform block would be one material per draw, and a descriptor
///         set would be one per material, which is the compositor's arrangement and needs a compositor.
///         Thirty-two more bytes an entity buys a block-out that reads as brick and metal rather than as
///         grey and grey.
///     </para>
///     <para>
///         ⚠ <b>The normal matrix is stored rather than derived, and it is per entity rather than per
///         vertex.</b> A cube scaled <c>2 1 1</c> transformed by its own matrix comes out with normals
///         that are no longer perpendicular to their faces, and the shading then slides across the
///         object as it is scaled — which reads as the light moving. The inverse is one matrix inverse
///         per entity here; asking the vertex stage for it would be one per vertex, and the shader
///         language has no inverse to ask with.
///     </para>
///     <para>
///         The fourth row of <paramref name="Normals" /> is never read — a direction has no
///         translation — and is kept because a <see cref="Matrix4x4" /> is what
///         <see cref="Matrix4x4.Transpose" /> hands back. Sixteen bytes to avoid a hand-packed
///         three-row layout that only the shader and this struct would agree about.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshInstance(
    Matrix4x4 Transform,
    Matrix4x4 Normals,
    Color4 Colour,
    Vector4 Style,
    Vector4 Surface,
    Color4 Emissive
) {
    /// <summary>An instance of a shape at a transform, with its normal matrix worked out.</summary>
    /// <param name="transform">Where the entity is.</param>
    /// <param name="colour">What colour it is.</param>
    /// <param name="style">How it is drawn. Default for an ordinary shaded surface.</param>
    /// <param name="surface">
    ///     What it is made of. Omitted is <see cref="Materials.MaterialSurface.Default" />, which is a
    ///     fully rough dielectric — the one directional term this renderer drew before it could be told
    ///     anything else.
    /// </param>
    /// <param name="checker">How big a block-out checker square is in metres, or zero for none.</param>
    /// <param name="tint">How strongly the checker is tinted by which axis a face points along.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A matrix that cannot be inverted is passed through as itself.</b> That is a zero
    ///         scale, where the entity has no visible surface and any normal will do — and the
    ///         alternative, refusing to build the instance, would drop an object out of the picture for
    ///         a reason the picture cannot show.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Build one through here rather than through the constructor, because a zeroed
    ///         <see cref="Surface" /> is a mirror.</b> Roughness lives in <c>y</c> and zero roughness is
    ///         a perfect specular — so a caller who forgets the material draws a chrome ball where a
    ///         grey one belongs, which looks like a shading bug rather than like a missing argument.
    ///         This overload's default is the neutral surface, and nothing else in the frame path builds
    ///         an instance any other way.
    ///     </para>
    /// </remarks>
    public static MeshInstance Of(
        in Matrix4x4 transform,
        Color4 colour,
        Vector4 style = default,
        MaterialSurface? surface = null,
        float checker = 0f,
        float tint = 0f
    ) {
        var shading = surface ?? MaterialSurface.Default;

        return new(
            transform,
            Normals: NormalMatrix(transform),
            colour,
            style,
            Packed(shading, checker, tint),
            new(shading.Emissive.R, shading.Emissive.G, shading.Emissive.B, 1f)
        );
    }

    /// <summary>The lanes the shader reads a surface's shading from.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="checker">How big a block-out checker square is in metres, or zero for none.</param>
    /// <param name="tint">How strongly to tint the checker by which axis a face points along.</param>
    /// <returns>Metalness in x, roughness in y, and the checker in the other two.</returns>
    /// <remarks>
    ///     ⚠ <b>The two lanes the shader's own README called reserved, and what they are reserved for
    ///     turned out to be doc 24's P5 blockout material.</b> A world-space checker is a function of
    ///     the fragment's position and normal and of one number, so it costs an instance no more than
    ///     that number — where a checker <i>texture</i> would cost a descriptor set per material and a
    ///     UV layout on geometry that exists to be thrown away.
    /// </remarks>
    public static Vector4 Packed(MaterialSurface surface, float checker = 0f, float tint = 0f) =>
        new(surface.Metalness, surface.Roughness, checker, tint);

    /// <summary>The matrix a normal goes through under a transform.</summary>
    /// <param name="transform">The transform.</param>
    public static Matrix4x4 NormalMatrix(in Matrix4x4 transform) =>
        Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
}

/// <summary>Where one shape's geometry sits in a <see cref="MeshInstanceRenderer" />'s buffers.</summary>
/// <param name="Slice">The vertices and both index ranges, as the geometry buffer allocated them.</param>
/// <param name="TriangleIndices">How many indices the surface is drawn from.</param>
/// <param name="EdgeIndices">
///     How many indices its wireframe is drawn from, immediately after the triangles.
/// </param>
/// <remarks>
///     <para>
///         <b>Two ranges in one allocation, which is what keeps a wireframe view free of a second
///         buffer.</b> The edges are the same vertices in a different order, so they are index data
///         and nothing else — appended to the same slice, drawn with the same vertex offset, and paid
///         for once at registration whether or not anybody presses the key.
///     </para>
///     <para>
///         Counts rather than offsets, because the offsets are derivable and two numbers that must
///         agree are one too many: the triangles start at <c>Slice.FirstIndex</c> and the edges at
///         <c>Slice.FirstIndex + TriangleIndices</c>.
///     </para>
/// </remarks>
public readonly record struct MeshShapeGeometry(GeometrySlice Slice, int TriangleIndices, int EdgeIndices) {
    /// <summary>Whether this names a registered shape.</summary>
    public bool IsValid => Slice.IsValid;

    /// <summary>How many triangles the surface has.</summary>
    public int TriangleCount => TriangleIndices / 3;

    /// <summary>How many segments the wireframe has.</summary>
    public int SegmentCount => EdgeIndices / 2;
}

/// <summary>One run of instances sharing a shape and a topology, which is one draw.</summary>
/// <param name="Geometry">Which shape they are instances of.</param>
/// <param name="First">Where the run starts in the frame's instances.</param>
/// <param name="Count">How many instances it holds.</param>
/// <param name="Edges">Whether it draws the shape's wireframe rather than its surface.</param>
/// <remarks>
///     ⚠ <b>The instances of one batch have to be contiguous.</b> A draw names a first instance and a
///     count, so a collector that appended entities in scene order would produce one batch per entity
///     and lose the whole benefit — grouping by shape before uploading is the caller's job, and it is
///     the only thing the caller has to get right.
/// </remarks>
public readonly record struct MeshInstanceBatch(MeshShapeGeometry Geometry, int First, int Count, bool Edges) {
    /// <summary>How many indices this draw reads.</summary>
    public int IndexCount => Edges ? Geometry.EdgeIndices : Geometry.TriangleIndices;

    /// <summary>Which index it starts at.</summary>
    public int FirstIndex => Geometry.Slice.FirstIndex + (Edges ? Geometry.TriangleIndices : 0);

    /// <summary>Whether there is anything to draw.</summary>
    public bool IsDrawable => Count > 0 && Geometry.IsValid && IndexCount > 0;
}

/// <summary>The shaders a <see cref="MeshInstanceRenderer" /> is built from.</summary>
/// <remarks>
///     Supplied rather than compiled here, the same seam <see cref="MeshShaders" /> describes.
/// </remarks>
/// <param name="Vertex">The vertex stage.</param>
/// <param name="Fragment">The fragment stage.</param>
public readonly record struct MeshInstanceShaders(ShaderHandle Vertex, ShaderHandle Fragment) {
    /// <summary>
    ///     Where the vertex stage reads the eleven attributes: the shape's position and normal, then
    ///     the instance's four transform rows, three normal-matrix rows, colour and style.
    /// </summary>
    /// <remarks>
    ///     Left unset for a stage whose attributes are at 0..10. The Raven shader beside the editor
    ///     declares two streams, so its are at 2..12 — see <see cref="VertexLocations" /> for why the
    ///     number belongs to the shader.
    /// </remarks>
    public VertexLocations Locations { get; init; }
}

/// <summary>The camera a frame's instances are drawn from.</summary>
/// <param name="ViewProjection">The world-to-clip matrix.</param>
/// <param name="Position">Where the camera is.</param>
/// <param name="Forward">Which way it looks. Expected to be unit length.</param>
/// <param name="NearPlane">Its near plane, which floors the depth the pixel scale is taken at.</param>
/// <param name="Orthographic">Whether the projection is orthographic.</param>
/// <param name="PixelScale">
///     How many world units a render pixel is: the whole answer for an orthographic view, and the
///     factor a perspective one is linear in depth by. See <see cref="WorldPerPixel" />.
/// </param>
/// <remarks>
///     ⚠ <b>The camera is here because the selection outline needs it per vertex.</b> An inverted hull
///     is the object's own geometry pushed outwards across the view by a width measured in
///     <em>pixels</em>, which is a different world distance at every vertex and in every projection.
///     The host used to have every vertex in hand and could do that arithmetic itself; a path that
///     uploads geometry once cannot, so what crosses the boundary is the four numbers the arithmetic
///     is made of.
/// </remarks>
public readonly record struct MeshInstanceView(
    Matrix4x4 ViewProjection,
    Vector3 Position,
    Vector3 Forward,
    float NearPlane,
    bool Orthographic,
    float PixelScale
) {
    /// <summary>How many world units a render pixel is at a point.</summary>
    /// <param name="at">The point, in world space.</param>
    /// <remarks>
    ///     ⚠ <b>The host-side mirror of a line in the vertex stage, and it exists to be tested.</b>
    ///     The expansion the shader does with these numbers cannot be asserted on without a device and
    ///     a picture; that the numbers themselves say what the camera says can be, and this is what a
    ///     test compares with <c>EditorCamera.WorldPerPixel</c>. One line each is what keeps them the
    ///     same line.
    /// </remarks>
    public float WorldPerPixel(Vector3 at) =>
        Orthographic
            ? PixelScale
            : PixelScale * MathF.Max(Vector3.Dot(at - Position, Forward), NearPlane);
}

/// <summary>
///     Shapes held on the device, drawn once per entity from a transform.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="MeshRenderer" /> for a scene rather than for a tool, and the difference is
///         which way the cost runs.</b> That renderer takes triangles that are already in world space,
///         so its caller transforms every vertex of every object every frame and the cost is linear in
///         vertices — the arrangement its own remarks call the deliberate limit of that path. This one
///         takes geometry once, into a <see cref="GeometryBuffer" />, and takes a
///         <see cref="MeshInstance" /> per entity per frame. A hundred cubes are one draw of a hundred
///         instances; a hundred <em>different</em> meshes are a hundred draws that bind nothing between
///         them, because they came out of one pair of buffers.
///     </para>
///     <para>
///         ⚠ <b>This is still not <c>RenderSystem</c>, and the boundary is materials rather than
///         geometry.</b> There is no culling, no sorting, no material and no descriptor set here: one
///         key direction, one ambient term, a colour per instance. What that is enough for is an
///         editor viewport — a block-out reads as a space in flat grey — and what it is not enough for
///         is a per-face material or a textured surface, which is the compositor's work and stays
///         there.
///     </para>
///     <para>
///         ⚠ <b>The geometry is device-local, so registering a shape stages it and
///         <see cref="Flush" /> records the copy.</b> Two calls because they happen at different
///         times, exactly as <see cref="GeometryBuffer" /> describes: a shape is registered when the
///         first entity wanting it appears, and the copies belong at one known point in one command
///         list, outside a render pass.
///     </para>
///     <para>
///         ⚠ <b>One buffer per renderer, and a four-pane layout is four copies of eight primitives.</b>
///         That is a few hundred kilobytes and it buys the renderer being the only owner of anything
///         it binds. The day the shapes are a level's worth of edited meshes rather than eight
///         parametric ones, the buffer wants to be shared and handed in — which is a constructor
///         parameter rather than a change of shape.
///     </para>
/// </remarks>
public sealed class MeshInstanceRenderer : IDisposable {
    /// <summary>How many attributes the vertex stage takes, over both buffers.</summary>
    const int Attributes = 13;

    readonly IGraphicsDevice device;
    readonly GeometryBuffer geometry;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle surfaces;
    readonly PipelineHandle wires;
    readonly int slots;
    readonly List<MeshInstanceBatch> drawn = [];

    // Reused across registrations rather than allocated per shape: a registration happens on the
    // frame a kind is first seen, which is a frame that is already doing device work.
    readonly List<MeshShapeVertex> staging = [];
    readonly List<uint> indices = [];
    readonly HashSet<(int From, int To)> seen = [];

    BufferHandle instances;
    long instanceCapacity;
    int slot;
    bool disposed;

    /// <summary>How many instances fit in one frame's region.</summary>
    public int InstanceCapacity => (int) (instanceCapacity / Marshal.SizeOf<MeshInstance>());

    /// <summary>How many regions the instance ring has.</summary>
    public int Regions => slots;

    /// <summary>Which region the last upload wrote.</summary>
    public int Region => slot;

    /// <summary>How many instances the last upload held.</summary>
    public int Count { get; private set; }

    /// <summary>How many draws the last record issued.</summary>
    public int Draws { get; private set; }

    /// <summary>How many of the last upload's instances no draw covers.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Dropped rather than grown, and in whole batches.</b> Half a batch is a draw whose
    ///         instances read past the end of the region — undefined rather than a missing object — and
    ///         growing the buffer mid-frame would mean recreating it while the device reads it. The
    ///         count is what makes the truncation visible instead of a picture quietly missing its end.
    ///     </para>
    ///     <para>
    ///         It counts the tail beyond the last accepted batch, so a batch refused for want of room
    ///         and one naming a shape that was never registered both land in it. Both are "an entity
    ///         this frame did not draw", which is the question a stats overlay is asking.
    ///     </para>
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>How many triangles the last upload's surface batches draw.</summary>
    /// <remarks>
    ///     Counted from the batches rather than from the buffers, for the reason a stats overlay
    ///     exists: what a renderer reports is what it <em>drew</em>, which is the truncated count when
    ///     a frame overflowed.
    /// </remarks>
    public int Triangles { get; private set; }

    /// <summary>How many segments its wireframe batches draw.</summary>
    public int Segments { get; private set; }

    /// <summary>How many shapes are registered.</summary>
    public int Shapes => geometry.SliceCount;

    /// <summary>Which way the light comes from, in world space.</summary>
    /// <remarks>
    ///     ⚠ <b>A direction and not a light</b>, on the same terms as
    ///     <see cref="MeshRenderer.LightDirection" />. It is also what an instance styled flat is given
    ///     as its normal, so that its lambert term is one — which is how an unshaded outline is drawn
    ///     beside shaded surfaces when the whole draw has a single ambient term.
    /// </remarks>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.35f));

    /// <summary>How much light a surface facing away still receives, from zero to one.</summary>
    public float Ambient { get; set; } = 0.35f;

    /// <summary>Builds the pipelines and the buffers a scene's shapes are drawn with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages.</param>
    /// <param name="output">What it draws into.</param>
    /// <param name="instanceCapacity">How many entities one frame may draw.</param>
    /// <param name="vertexCapacity">How many shape vertices the device-local buffer holds.</param>
    /// <param name="indexCapacity">How many indices it holds, triangles and edges together.</param>
    public MeshInstanceRenderer(
        IGraphicsDevice device,
        MeshInstanceShaders shaders,
        RenderOutput output,
        int instanceCapacity = 1 << 14,
        int vertexCapacity = 1 << 18,
        int indexCapacity = 1 << 20
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instanceCapacity);

        this.device = device;
        slots = Math.Max(1, device.FramesInFlight);

        shaders.Locations.Require(Attributes, nameof(MeshInstanceRenderer));

        geometry = new(
            device,
            Marshal.SizeOf<MeshShapeVertex>(),
            vertexCapacity,
            indexCapacity,
            IndexFormat.UInt32,
            "mesh shapes"
        );

        // A hundred and twenty-eight bytes, which is exactly what every Vulkan implementation
        // guarantees: the view-projection, the light, and the three vectors the outline's pixel
        // measurement is made of. Anything more would need a limit asked of the device.
        layout = device.CreatePipelineLayout(
            new([], [new(ShaderStage.Vertex | ShaderStage.Fragment, 0, Marshal.SizeOf<Constants>())], "mesh instances")
        );

        surfaces = Pipeline(
            shaders,
            output,
            PrimitiveTopology.TriangleList,
            DepthStencilState.Default,
            "mesh instances"
        );

        // ⚠ Segments rather than a fill mode, which is what makes a wireframe view cost no device
        // feature. `FillMode.Wireframe` needs `fillModeNonSolid`, which is optional in Vulkan and
        // absent on most tiled GPUs — a view mode that drew nothing on a phone. The edges are index
        // data beside the triangles, so this pipeline reads the same buffers.
        //
        // Depth is tested and not written: in a wireframe view nothing fills depth, and a wire that
        // wrote it would hide the wires behind it — which is a solid object drawn in lines rather than
        // a wireframe.
        wires = Pipeline(
            shaders,
            output,
            PrimitiveTopology.LineList,
            DepthStencilState.TestOnly,
            "mesh instance wires"
        );

        Resize(instanceCapacity);
    }

    /// <summary>Puts one shape's geometry on the device, to be drawn by any number of instances.</summary>
    /// <param name="mesh">The shape. Its indices are triangles.</param>
    /// <param name="shape">Where it went.</param>
    /// <returns>False when the buffers are too full or too fragmented to hold it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The bytes are staged and not yet on the device when this returns.</b>
    ///         <see cref="Flush" /> records the copies, and a frame that registers a shape and draws
    ///         it without flushing in between draws whatever was at those offsets before.
    ///     </para>
    ///     <para>
    ///         A mesh with no normals is given <c>+Y</c> everywhere rather than being refused. A
    ///         block-out primitive always has them; something imported might not, and a flat-lit shape
    ///         is a shape you can still see and select.
    ///     </para>
    /// </remarks>
    public bool TryRegister(MeshData mesh, out MeshShapeGeometry shape) {
        ArgumentNullException.ThrowIfNull(mesh);
        ObjectDisposedException.ThrowIf(disposed, this);

        shape = default;

        if (mesh.VertexCount == 0) {
            return false;
        }

        staging.Clear();
        indices.Clear();

        var normals = mesh.Normals.Length == mesh.Positions.Length;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            staging.Add(new(mesh.Positions[index], normals ? mesh.Normals[index] : Vector3.UnitY));
        }

        var triangles = mesh.Indices.Length / 3 * 3;

        for (var index = 0; index < triangles; index++) {
            indices.Add((uint) mesh.Indices[index]);
        }

        Edges(mesh, triangles);

        if (!geometry.TryAllocate(staging.Count, indices.Count, out var slice)) {
            return false;
        }

        geometry.Write(
            slice,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(staging)),
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices))
        );

        shape = new(slice, triangles, indices.Count - triangles);
        return true;
    }

    /// <summary>Gives one shape's space back.</summary>
    /// <param name="shape">What <see cref="TryRegister" /> handed out.</param>
    public void Release(in MeshShapeGeometry shape) {
        ObjectDisposedException.ThrowIf(disposed, this);
        geometry.Free(shape.Slice);
    }

    /// <summary>Records the copies every shape registered since the last flush needs.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <returns>How many copies were recorded, which is zero on a frame that registered nothing.</returns>
    public int Flush(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        return geometry.Flush(commands);
    }

    /// <summary>Writes a frame's instances into the next region of the ring.</summary>
    /// <param name="frame">One instance per entity, grouped so that each batch's run is contiguous.</param>
    /// <param name="batches">Which run of them is which shape.</param>
    /// <remarks>
    ///     ⚠ <b>A batch naming instances the truncation dropped is dropped whole.</b> Every instance a
    ///     draw reads has to be in the region, so the cut is made from the batches as well as from the
    ///     instances — the same forward scan <see cref="MeshRenderer.Upload" /> makes over indices, and
    ///     for the same reason: a caller builds a frame shape by shape, so what an overflow costs is
    ///     the tail.
    /// </remarks>
    public void Upload(ReadOnlySpan<MeshInstance> frame, ReadOnlySpan<MeshInstanceBatch> batches) {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Advanced here rather than in `Record`, for the reason `LineRenderer.Upload` gives: the
        // region moved on to is the one used `slots` frames ago, which the device has finished with.
        slot = (slot + 1) % slots;

        drawn.Clear();

        Triangles = 0;
        Segments = 0;

        var fits = Math.Min(frame.Length, InstanceCapacity);
        var count = 0;

        foreach (var batch in batches) {
            if (batch.First < 0 || batch.Count <= 0 || batch.First + batch.Count > fits) {
                continue;
            }

            if (!batch.IsDrawable) {
                continue;
            }

            drawn.Add(batch);
            count = Math.Max(count, batch.First + batch.Count);

            if (batch.Edges) {
                Segments += batch.Geometry.SegmentCount * batch.Count;
            } else {
                Triangles += batch.Geometry.TriangleCount * batch.Count;
            }
        }

        Dropped = frame.Length - count;
        Count = count;

        if (count > 0) {
            device.Write(instances, (long) slot * instanceCapacity, MemoryMarshal.AsBytes(frame[..count]));
        }
    }

    /// <summary>Draws what the last upload wrote.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="view">The camera, whose numbers the outline is measured with.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The buffers are rebound after the second pipeline, not bound once for both.</b> A
    ///         vertex binding outlives a pipeline change in Vulkan and does not in every backend that
    ///         emulates one — the GL device sets its attribute divisors as part of binding — so three
    ///         redundant calls a frame buy the two paths drawing the same picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A batch reaches its own instances through the draw's <c>firstInstance</c></b>, which
    ///         is what keeps the instance buffer bound once for the whole frame — the same field
    ///         <c>InstancingRenderFeature</c> and the transform records use. It is free in Vulkan and
    ///         needs <c>glDrawElementsInstancedBaseVertexBaseInstance</c> in GL, so the GL device
    ///         refuses it below Core 4.5 with a message that names the reason rather than drawing the
    ///         first shape's entities as every shape's.
    ///     </para>
    /// </remarks>
    public void Record(ICommandList commands, in MeshInstanceView view) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (drawn.Count == 0 || Count == 0) {
            return;
        }

        var constants = new Constants(
            view.ViewProjection,
            new(LightDirection, Ambient),
            new(view.Position, view.Orthographic ? 1f : 0f),
            new(view.Forward, view.NearPlane),
            new(view.PixelScale, 0f, 0f, 0f)
        );

        Draws += Record(commands, surfaces, constants, edges: false);
        Draws += Record(commands, wires, constants, edges: true);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(surfaces);
        device.Destroy(wires);
        device.Destroy(layout);

        geometry.Dispose();

        if (instances.IsValid) {
            device.Destroy(instances);
        }
    }

    int Record(ICommandList commands, PipelineHandle pipeline, in Constants constants, bool edges) {
        var issued = 0;

        foreach (var batch in drawn) {
            if (batch.Edges != edges) {
                continue;
            }

            if (issued == 0) {
                Bind(commands, pipeline, constants);
            }

            commands.DrawIndexed(
                batch.IndexCount,
                batch.Count,
                batch.FirstIndex,
                batch.Geometry.Slice.BaseVertex,
                batch.First
            );

            issued++;
        }

        return issued;
    }

    void Bind(ICommandList commands, PipelineHandle pipeline, in Constants constants) {
        commands.BindPipeline(pipeline);

        commands.PushConstants(
            ShaderStage.Vertex | ShaderStage.Fragment,
            0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<Constants>(in constants))
        );

        commands.BindVertexBuffer(0, geometry.Vertices);
        commands.BindVertexBuffer(1, instances, (long) slot * instanceCapacity);
        commands.BindIndexBuffer(geometry.Indices, geometry.IndexFormat);
    }

    /// <summary>A shape's unique edges, appended to the triangles as a second index range.</summary>
    /// <remarks>
    ///     ⚠ <b>Every edge of every triangle, deduplicated by the index pair rather than by
    ///     position.</b> A cube is twenty-four vertices — three per corner, so that its faces have
    ///     hard normals — and two faces meeting at an edge name it with two different pairs of
    ///     indices, so that edge is drawn twice, exactly on top of itself. Deduplicating by position
    ///     instead would mean hashing floats, which is a worse trade for a debug view. The diagonal a
    ///     quad's two triangles share survives the deduplication and is drawn, which is what
    ///     <c>FillMode.Wireframe</c> would also draw.
    /// </remarks>
    void Edges(MeshData mesh, int triangles) {
        seen.Clear();

        for (var index = 0; index + 2 < triangles; index += 3) {
            Edge(mesh.Indices[index], mesh.Indices[index + 1]);
            Edge(mesh.Indices[index + 1], mesh.Indices[index + 2]);
            Edge(mesh.Indices[index + 2], mesh.Indices[index]);
        }

        void Edge(int from, int to) {
            if (from == to) {
                return;
            }

            var key = from < to ? (from, to) : (to, from);

            if (seen.Add(key)) {
                indices.Add((uint) key.Item1);
                indices.Add((uint) key.Item2);
            }
        }
    }

    /// <summary>One of the two pipelines, which differ in their topology and in what they do with
    /// depth.</summary>
    PipelineHandle Pipeline(
        MeshInstanceShaders shaders,
        RenderOutput output,
        PrimitiveTopology topology,
        DepthStencilState depth,
        string name
    ) =>
        device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [
                    new(
                        output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Rgba8UNorm,
                        BlendState.PremultipliedAlpha
                    )
                ],
                [
                    // The shape, once per vertex.
                    new(
                        Marshal.SizeOf<MeshShapeVertex>(),
                        [
                            new(shaders.Locations[0], VertexFormat.Float32X3, 0),
                            new(shaders.Locations[1], VertexFormat.Float32X3, 12)
                        ]
                    ),

                    // ⚠ The entity, once per *instance*, and the step mode is the whole mechanism. A
                    // second buffer at the vertex rate would hand every vertex of the first shape a
                    // different entity's transform — which draws the scene as one exploded object.
                    //
                    // The three normal rows are read as `Float32X3` out of a matrix whose rows are
                    // sixteen bytes apart, so the fourth lane of each is skipped rather than packed
                    // against: a direction has no translation and there is nothing to put there.
                    //
                    // The last two are the material — the shading lanes at 160 and the emission at 176.
                    // Per instance for the reason `MeshInstance` gives: a material that lived anywhere
                    // else would be one draw per material.
                    new(
                        Marshal.SizeOf<MeshInstance>(),
                        [
                            new(shaders.Locations[2], VertexFormat.Float32X4, 0),
                            new(shaders.Locations[3], VertexFormat.Float32X4, 16),
                            new(shaders.Locations[4], VertexFormat.Float32X4, 32),
                            new(shaders.Locations[5], VertexFormat.Float32X4, 48),
                            new(shaders.Locations[6], VertexFormat.Float32X3, 64),
                            new(shaders.Locations[7], VertexFormat.Float32X3, 80),
                            new(shaders.Locations[8], VertexFormat.Float32X3, 96),
                            new(shaders.Locations[9], VertexFormat.Float32X4, 128),
                            new(shaders.Locations[10], VertexFormat.Float32X4, 144),
                            new(shaders.Locations[11], VertexFormat.Float32X4, 160),
                            new(shaders.Locations[12], VertexFormat.Float32X4, 176)
                        ],
                        VertexStepMode.Instance
                    )
                ],
                topology,

                // Two-sided for the reason `MeshRenderer` gives: which winding is front depends on the
                // projection and on whatever produced the geometry, and a viewport that drew nothing
                // when one of them disagreed is a bad way to find that out.
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: depth,
                DepthFormat: output.DepthFormat,
                SampleCount: output.SampleCount,
                Name: name
            )
        );

    void Resize(int count) {
        instanceCapacity = (long) count * Marshal.SizeOf<MeshInstance>();

        // Host-visible and written in place, like the line and mesh rings and unlike the geometry: an
        // instance is a fact about this frame that is read once and thrown away, so a staging copy
        // would add a transfer and a barrier to save nothing.
        instances = device.CreateBuffer(
            new(instanceCapacity * slots, BufferUsage.Vertex, MemoryAccess.HostUpload, "mesh instances")
        );
    }

    /// <summary>The push-constant block, which is the shader's declaration order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Constants(
        Matrix4x4 ViewProjection,
        Vector4 Light,
        Vector4 Eye,
        Vector4 View,
        Vector4 Pixels
    );
}
