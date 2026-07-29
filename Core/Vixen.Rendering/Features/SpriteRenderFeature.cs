// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Rendering.Sprites;

namespace Vixen.Rendering.Features;

/// <summary>Where one sprite's quads ended up in this frame's vertex buffer.</summary>
/// <remarks>
///     The same shape as <see cref="ParticleDraw" /> and for the same reason: the geometry is a run
///     inside one buffer everybody shares, so what is recorded is where the run is rather than what
///     it is in.
/// </remarks>
public struct SpriteDraw {
    /// <summary>The first vertex of this sprite's run in the shared buffer.</summary>
    public int FirstVertex;

    /// <summary>How many quads it took: one, nine, or the many a tiled fill repeats.</summary>
    public int QuadCount;

    /// <summary>Whether there is anything to draw.</summary>
    public readonly bool IsDrawable => QuadCount > 0;
}

/// <summary>
///     Draws sprites: textured quads in their own plane, cut into nine when they have a border.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 06 § Geometry and materials, the row that says sprites share the UI batcher.</b>
///         What is shared is the arithmetic and the shape of the answer, not the assembly: the nine
///         pairs of rectangles come from <c>NineSlice</c> in <c>Vixen.Core.Mathematics</c>, which is
///         also what <c>UiGeometryBuilder</c> cuts a panel's background with, and both sides expand
///         a batch of quads into one buffer and draw it in one call. They cannot share code beyond
///         that, because <c>Vixen.Ui</c> describes a frame without a device and this describes a
///         device without an element tree.
///     </para>
///     <para>
///         <b>Local space, and that is the whole difference from the particle feature.</b> A sprite's
///         quads are built around its pivot with no camera in them, so the geometry is the same for
///         every view that draws it — one expansion a frame rather than one a view — and where the
///         sprite <i>is</i> comes from <see cref="TransformRenderFeature" /> pushing a matrix, the
///         same way a mesh's does. A camera-facing sprite is therefore not this feature's job: that is
///         a billboard, it has to be built against a view, and asking one object to answer "where am
///         I" twice is how a shadow pass ends up drawing a different scene from the camera.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt every frame, including the sprites that did not move.</b> A still sprite has
///         the same nine quads it had last frame and this writes them again — which is the cost the
///         particle feature pays for a reason that does not apply here, and it is a deliberate
///         holdover rather than an oversight: caching needs a version per object and an invalidation
///         on every field of <see cref="SpriteAppearance" />, and the write is thirty-six vertices.
///         What would make it worth doing is a scene of tens of thousands of static sprites — a tile
///         map — which is the case that wants one batched mesh rather than one object each anyway.
///     </para>
/// </remarks>
public sealed class SpriteRenderFeature : RootRenderFeature, IDisposable {
    readonly List<Sprite> sprites = [];
    readonly UploadBuffer<SpriteVertex> vertices = new("Sprite vertices", BufferUsage.Vertex);

    SpriteVertex[] scratch = [];
    BufferHandle indices;
    int indexCapacity;
    int widest;
    bool disposed;

    /// <inheritdoc />
    public override string Name => "Sprite";

    /// <summary>Where each sprite's run is. One entry per object.</summary>
    public RenderDataKey<SpriteDraw> Draws { get; private set; }

    /// <summary>
    ///     Each object's sprite, as one more than its index in <see cref="Known" />. Zero for none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One more than the index, so that the zero every array arrives at means "no
    ///     sprite".</b> A per-object array is native memory the store zeroes when it grows, so an
    ///     unbiased index would make every object that has never been given a sprite draw the first
    ///     one somebody registered — which is a scene full of whatever sprite happened to be loaded
    ///     first, appearing at the origin of everything.
    /// </remarks>
    public RenderDataKey<int> SpriteIndices { get; private set; }

    /// <summary>How each object's sprite is drawn.</summary>
    public RenderDataKey<SpriteAppearance> Appearances { get; private set; }

    /// <summary>The distinct sprites this feature has been given.</summary>
    public IReadOnlyList<Sprite> Known => sprites;

    /// <summary>The device the buffers live on. Set before the first frame that draws.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where pipelines come from. Set before the first frame that draws.</summary>
    public PipelineCache? Pipelines { get; set; }

    /// <summary>How a pipeline is described for a given effect, stage and output.</summary>
    public IPipelineDescriber? Describer { get; set; }

    /// <summary>
    ///     Which vertex layout <see cref="SpriteVertex" /> is, as an index into the describer's table.
    /// </summary>
    /// <remarks>
    ///     One by default, which is the same slot <see cref="ParticleRenderFeature" /> takes — the two
    ///     structs have the same three attributes in the same order, so a project that has described
    ///     one has described the other. A project that gives sprites a layout of their own says so
    ///     here rather than by changing either feature.
    /// </remarks>
    public int VertexLayout { get; set; } = 1;

    /// <summary>How many quads the last frame expanded, across every sprite.</summary>
    public int LastQuadCount { get; private set; }

    /// <summary>Attaches a sprite to an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="sprite">The sprite, or null to detach.</param>
    /// <exception cref="InvalidOperationException">The feature has not been added to a system yet.</exception>
    /// <remarks>
    ///     The same registry the particle feature keeps for effects: distinct sprites are held once
    ///     and the object carries a number, so two hundred pieces of grass showing the same sprite
    ///     cost two hundred integers rather than two hundred references for the store to trace.
    /// </remarks>
    public void SetSprite(RenderObjectId id, Sprite? sprite) {
        var data = Require().Objects.Data.Data(SpriteIndices);

        if (sprite is null) {
            data[id.Index] = 0;

            return;
        }

        var slot = sprites.IndexOf(sprite);

        if (slot < 0) {
            slot = sprites.Count;
            sprites.Add(sprite);
        }

        data[id.Index] = slot + 1;
    }

    /// <summary>Says how an object's sprite is drawn.</summary>
    /// <param name="id">The object.</param>
    /// <param name="appearance">The tint, the size, the fill and the sort group.</param>
    /// <exception cref="InvalidOperationException">The feature has not been added to a system yet.</exception>
    public void SetAppearance(RenderObjectId id, in SpriteAppearance appearance) =>
        Require().Objects.Data.Data(Appearances)[id.Index] = appearance;

    /// <summary>The sprite an object is showing, or null.</summary>
    /// <param name="id">The object.</param>
    /// <returns>The sprite.</returns>
    /// <exception cref="InvalidOperationException">The feature has not been added to a system yet.</exception>
    public Sprite? SpriteOf(RenderObjectId id) {
        var slot = Require().Objects.Data.Data(SpriteIndices)[id.Index] - 1;

        return (uint)slot < (uint)sprites.Count ? sprites[slot] : null;
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        Draws = system.Objects.Data.Register<SpriteDraw>();
        SpriteIndices = system.Objects.Data.Register<int>();
        Appearances = system.Objects.Data.Register<SpriteAppearance>();

        vertices.Device = Device;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     In <c>Prepare</c> rather than <c>Extract</c> because the expansion is geometry and the
    ///     store is what holds the sprite by then — but unlike the particle feature, nothing here
    ///     needs a camera, so the only reason it is not in extraction is that it wants the visible
    ///     set rather than the changed one.
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        LastQuadCount = 0;
        widest = 0;

        var draws = system.Objects.Data.Data(Draws);
        var slots = system.Objects.Data.Data(SpriteIndices);
        var appearances = system.Objects.Data.Data(Appearances);

        for (var index = 0; index < system.Objects.Count; index++) {
            draws[index] = default;
        }

        vertices.Device ??= Device;
        vertices.Begin();

        for (var index = 0; index < system.Objects.Count; index++) {
            var id = new RenderObjectId(index);

            if (!system.Objects[id].IsAlive || system.Objects[id].FeatureIndex != Index) {
                continue;
            }

            var slot = slots[index] - 1;

            if ((uint)slot >= (uint)sprites.Count) {
                continue;
            }

            draws[index] = Expand(sprites[slot], appearances[index]);
            LastQuadCount += draws[index].QuadCount;

            // ⚠ The widest single sprite, not the total. Every draw reads the shared index pattern
            // from its own vertex offset, so what the buffer has to cover is the largest run any one
            // object needs — summing them would size it by the whole frame and allocate a pattern
            // that is mostly indices nothing ever reads.
            widest = Math.Max(widest, draws[index].QuadCount);
        }

        vertices.Upload();

        EnsureIndices();
    }

    /// <summary>One sprite's quads in the shared vertex buffer.</summary>
    SpriteDraw Expand(Sprite sprite, in SpriteAppearance appearance) {
        var quads = SpriteGeometry.QuadsFor(sprite, appearance);

        if (quads == 0) {
            return default;
        }

        var wanted = quads * SpriteGeometry.VerticesPerQuad;

        if (scratch.Length < wanted) {
            scratch = new SpriteVertex[Math.Max(wanted, scratch.Length * 2)];
        }

        var written = SpriteGeometry.Build(sprite, appearance, scratch.AsSpan(0, wanted));

        if (written == 0) {
            return default;
        }

        return new() {
            FirstVertex = vertices.Add(scratch.AsSpan(0, written * SpriteGeometry.VerticesPerQuad)),
            QuadCount = written
        };
    }

    /// <inheritdoc />
    protected internal override void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) {
        if (Pipelines is null || Describer is null || context.Stage is null) {
            return;
        }

        if (!vertices.Buffer.IsValid || !indices.IsValid) {
            return;
        }

        var stage = context.Stage;
        var output = context.Output;
        var draws = system.Objects.Data.Data(Draws);
        var materials = SubFeatures.OfType<MaterialRenderFeature>().FirstOrDefault();

        var boundPipeline = default(PipelineHandle);
        var boundDescriptors = default(DescriptorSetHandle);
        var boundGeometry = false;
        var boundView = false;

        foreach (var node in nodes) {
            var draw = draws[node.Object.Index];

            if (!draw.IsDrawable) {
                continue;
            }

            if (materials?.EffectOf(system, node.Object, stage) is not { } effect) {
                continue;
            }

            var key = new PipelineKey(effect, stage.Index, VertexLayout, output);

            if (!Pipelines.TryGet(key, out var pipeline)) {
                pipeline = Pipelines.GetOrCreate(key, () => Describer.Describe(effect, stage, output, VertexLayout));
            }

            if (pipeline != boundPipeline) {
                context.CommandList.BindPipeline(pipeline);
                boundPipeline = pipeline;
            }

            if (!boundView && context.ViewConstants is { } view && context.View is { } from) {
                boundView = view.Bind(context.CommandList, from);
            }

            if (materials.DescriptorsOf(system, node.Object, stage) is { IsValid: true } descriptors
                && descriptors != boundDescriptors) {
                context.CommandList.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors);
                boundDescriptors = descriptors;
            }

            // Where the sprite is. The quads are around the pivot and nothing else knows where that
            // is in the world, so a sprite with no transform sub-feature draws at the origin — which
            // is the same bargain a mesh makes and the reason neither feature has a matrix of its own.
            context.Effect = effect;

            foreach (var subFeature in SubFeatures) {
                if (subFeature is IDrawSubFeature contributor) {
                    contributor.Draw(system, context, node);
                }
            }

            // Once for every sprite in the frame. They are all runs of one buffer at one offset, and
            // the run is reached through the draw call's vertex offset instead — the same arrangement
            // that makes a hundred particle effects a hundred draws and one binding.
            if (!boundGeometry) {
                context.CommandList.BindVertexBuffer(0, vertices.Buffer, vertices.Offset);
                context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt32);
                boundGeometry = true;
            }

            context.CommandList.DrawIndexed(
                draw.QuadCount * SpriteGeometry.IndicesPerQuad,
                1,
                0,
                draw.FirstVertex,
                0
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The appearance's own group, and the stage this is drawn in should sort
    ///     <c>ByGroup</c>.</b> Sprites overlap and are blended, so what is in front is what was drawn
    ///     last — a decision an artist makes and a depth buffer cannot make for them. A stage that
    ///     sorted front-to-back would order a 2D scene by the distance of quads that are all the same
    ///     distance away, which is to say by object id.
    /// </remarks>
    protected internal override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) {
        var slot = system.Objects.Data.Data(SpriteIndices)[id.Index] - 1;

        return (uint)slot < (uint)sprites.Count
            ? system.Objects.Data.Data(Appearances)[id.Index].SortGroup
            : system.Objects[id].SortGroup;
    }

    /// <summary>Frees the buffers.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        vertices.Dispose();

        if (indices.IsValid) {
            Device?.Destroy(indices);
            indices = default;
        }
    }

    /// <summary>The system this feature belongs to, or a message saying it belongs to none.</summary>
    RenderSystem Require() =>
        System
        ?? throw new InvalidOperationException(
            "The feature has to be added to a RenderSystem before it can be given sprites."
        );

    /// <summary>Builds the quad index pattern, once, for the widest sprite the frame held.</summary>
    /// <remarks>
    ///     Device memory written when the high-water mark rises and never again, because the pattern
    ///     never depends on anything but the count — unlike the vertices, which are rewritten every
    ///     frame because they are different every frame.
    /// </remarks>
    void EnsureIndices() {
        if (Device is null || widest <= indexCapacity) {
            return;
        }

        if (indices.IsValid) {
            Device.Destroy(indices);
        }

        indexCapacity = Math.Max(widest, Math.Max(indexCapacity * 2, 64));

        var pattern = new uint[indexCapacity * SpriteGeometry.IndicesPerQuad];
        SpriteGeometry.WriteQuadIndices(pattern, indexCapacity);

        indices = Device.CreateBuffer(
            new((long)pattern.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "Sprite indices")
        );

        Device.Write(indices, 0, MemoryMarshal.AsBytes(pattern.AsSpan()));
    }
}
