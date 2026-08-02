// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     One tile exactly as <c>PunctualShadowTile</c> in <c>PunctualShadows.rvn</c> declares it.
/// </summary>
/// <remarks>
///     Eighty bytes: a matrix and two pairs. The matrix already has the tile folded into it — see
///     <see cref="ShadowProjections.Tile" /> — so a shading pass does one multiply and no per-tile
///     arithmetic, and the scale and the offset are carried anyway because they are what lets the
///     filter clamp its taps inside the tile they belong to.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct PunctualShadowTileData {
    /// <summary>World to this tile's place in the atlas.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>How much of the atlas the tile is, as a fraction.</summary>
    public Vector2 Scale;

    /// <summary>Where it starts in the atlas, as a fraction.</summary>
    public Vector2 Offset;
}

/// <summary>One tile of the punctual shadow atlas: what it renders, and where it lives in it.</summary>
/// <param name="Light">Which light in the renderer's list.</param>
/// <param name="Face">Which cube face, or <see langword="null" /> for a spot light's single tile.</param>
/// <param name="ViewProjection">World to this tile's clip space.</param>
/// <param name="Scale">How much of the atlas the tile is, as a fraction.</param>
/// <param name="Offset">Where it starts in the atlas, as a fraction.</param>
public readonly record struct ShadowTile(
    int Light,
    CubeFace? Face,
    Matrix4x4 ViewProjection,
    Vector2 Scale,
    Vector2 Offset
);

/// <summary>
///     Renders spot and point light shadows into one atlas.
/// </summary>
/// <remarks>
///     <para>
///         The same idea as <see cref="ShadowMapRenderer" /> — a shadow map is a view, and an atlas
///         is one pass with a viewport per tile — applied to lights that have a position. What
///         differs is that nothing has to be fitted or stabilised: a spot light's shadow frustum
///         <em>is</em> its cone and a point light's is six of them, so the whole projection question
///         that cascades spend two hundred lines on does not arise.
///     </para>
///     <para>
///         <strong>A point light is six tiles and a spot light is one.</strong> That ratio is the
///         real cost of the two and it is worth seeing: six times the culling, six times the draws,
///         six times the atlas. It is also why the atlas is allocated in tile units rather than per
///         light — the alternative reserves a cube's worth of space for every light in case it turns
///         out to be one.
///     </para>
///     <para>
///         <strong>When the atlas runs out, lights are dropped and counted.</strong> Not silently:
///         <see cref="DroppedLights" /> is what turns "some shadows disappeared in the big fight"
///         into a number a profiler can show. Dropping the ones furthest down the list is arbitrary
///         and deliberate — importance ordering belongs to whoever fills the list, which is the only
///         place that knows what the scene is about.
///     </para>
/// </remarks>
public sealed class PunctualShadowRenderer : SceneRenderer, IDisposable {
    readonly List<RenderView> views = [];
    readonly List<ShadowTile> tiles = [];
    // ⚠ 1280 rather than the default 256, and it is load-bearing rather than tidy: a tile's index
    // carries the ring's own offset (see Collect), so the region stride has to be a whole number of
    // records. 1280 is the least common multiple of the eighty-byte record and the 256-byte binding
    // alignment, so it satisfies both whatever the capacity grows to. Left at 256 the base is a
    // fraction, truncates, and every tile is read one or two records along — a shadow from the wrong
    // face of the wrong lamp.
    readonly UploadBuffer<PunctualShadowTileData> records = new("PunctualShadows.Tiles") { Alignment = 1280 };
    PunctualShadowTileData[] staged = [];
    bool disposed;

    /// <summary>The stage that draws depth-only casters.</summary>
    public required RenderStage CasterStage { get; init; }

    /// <summary>The name of the atlas to render into.</summary>
    public string Atlas { get; set; } = string.Empty;

    /// <summary>
    ///     The lights to shadow. Directional lights are skipped — they are the cascades'.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Normally the lighting feature's own list, shared rather than copied.</b> This node
    ///         writes <see cref="RenderLight.ShadowTile" /> back into every entry, and the feature
    ///         reads it a phase later when it flattens the same lights to the GPU — so two lists is
    ///         two sets of indices, one of which addresses an atlas that was packed from the other.
    ///         <c>CompositorBuilder</c> hands over the feature's list for exactly that reason.
    ///     </para>
    ///     <para>
    ///         The ordering is the compositor's and not an assumption: collect runs before the render
    ///         system's phases (see <see cref="GraphicsCompositor.Build" />), which is where the
    ///         feature uploads. A node that packed its atlas after that would publish indices one
    ///         frame late, which is a shadow that lags its light by a frame and nothing else.
    ///     </para>
    /// </remarks>
    public IList<RenderLight> Lights { get; set; } = [];

    /// <summary>The per-view block to bind before each tile's casters.</summary>
    /// <remarks>
    ///     A tile is a view, and this is how it tells a caster which projection it is being drawn for.
    ///     Without it the caster pass binds no set 1 at all and every draw in it is refused — the
    ///     same hole <see cref="ShadowMapRenderer.Constants" /> was added to close, one node along.
    /// </remarks>
    public ViewConstants? Constants { get; set; }

    /// <summary>
    ///     Where to publish what a shading pass needs to read the atlas, or null to publish nothing.
    /// </summary>
    /// <remarks>
    ///     <see cref="ShadowMapRenderer.Scene" />'s terms exactly: the tile buffer, the sampler, the
    ///     texel size and the two biases — everything about the atlas the atlas does not carry. The
    ///     <em>texture</em> is not written here, because it is a frame resource and its barrier
    ///     belongs to whoever declared it read.
    /// </remarks>
    public ParameterCollection? Scene { get; set; }

    /// <summary>The compose-slot prefix the tile bindings are written under.</summary>
    /// <remarks>
    ///     The pass, then the shader filling its slot —
    ///     <c>ForwardPlus.PunctualShadowAtlas</c> — because a composed slot's bindings are named for
    ///     what fills it and not for the shader that declared it. The same string
    ///     <see cref="Materials.MaterialCompiler.PunctualShadowShader" /> is half of, and getting it
    ///     wrong writes bindings nothing reads while leaving bindings nothing writes.
    /// </remarks>
    public string ShaderName { get; set; } = "ForwardPlus.PunctualShadowAtlas";

    /// <summary>Any further prefixes the same atlas is published under.</summary>
    /// <remarks>
    ///     One atlas, more than one consumer: a frame that resolves a visibility buffer shades the
    ///     same lights through <c>VisibilityResolve.PunctualShadowAtlas</c>, and a resolve shadowing
    ///     its lights differently from a forward draw is the divergence <c>ClusteredShading</c> exists
    ///     to prevent.
    /// </remarks>
    public IList<string> Passes { get; } = [];

    /// <summary>Where the tile buffer lives. Without one, nothing is uploaded and nothing published.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where the atlas's sampler comes from. Without one, no sampler is published.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>How far the depth comparison is nudged, in depth units.</summary>
    /// <remarks>
    ///     Three times the cascades', and the reason is the projection rather than the resolution: a
    ///     punctual light's frustum is perspective, so its depth is distributed hyperbolically and a
    ///     constant bias in depth units is worth far less far from the light than near it.
    /// </remarks>
    public float ConstantBias { get; set; } = 0.0015f;

    /// <summary>How much more of that a surface gets as it turns away from the light.</summary>
    public float SlopeBias { get; set; } = 0.004f;

    /// <summary>One tile's side in texels.</summary>
    public int Resolution { get; set; } = 512;

    /// <summary>How many tiles the atlas is across, so it holds this squared.</summary>
    public int TilesPerSide { get; set; } = 4;

    /// <summary>The near plane every punctual shadow projection uses.</summary>
    public float NearPlane { get; set; } = 0.05f;

    /// <summary>The tiles this frame allocated.</summary>
    public IReadOnlyList<ShadowTile> Tiles => tiles;

    /// <summary>Which record of the tile buffer this frame's tiles start at.</summary>
    /// <remarks>
    ///     The ring's own offset, in records, already folded into every
    ///     <see cref="RenderLight.ShadowTile" /> this frame wrote — so a shading pass needs no base of
    ///     its own. Exposed for the same reason
    ///     <see cref="Features.ForwardLightingRenderFeature.RecordBase" /> is: it is the number that
    ///     makes "the index addresses the whole buffer, not this frame's slice" checkable, and the
    ///     failure it guards against reads a shadow from one to three frames ago.
    /// </remarks>
    public int TileBase { get; private set; }

    /// <summary>The views the tiles are drawn from.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>How many lights did not fit in the atlas this frame.</summary>
    public int DroppedLights { get; private set; }

    /// <summary>How many tiles the atlas holds.</summary>
    public int Capacity => Math.Max(TilesPerSide, 1) * Math.Max(TilesPerSide, 1);

    /// <summary>The atlas's size in texels, for whoever creates the texture.</summary>
    public Int2 AtlasSize => new(Math.Max(TilesPerSide, 1) * Resolution, Math.Max(TilesPerSide, 1) * Resolution);

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        tiles.Clear();
        DroppedLights = 0;

        // ⚠ **Before anything is packed, because the ring's region is part of a tile's index.** The
        // buffer holds one region per frame in flight and the whole thing is bound — a shading pass
        // has a handle a host named and nowhere to put an offset, which is the argument
        // `ClusteredShading.objectBase` and `transformBase` are both there for. So the base is folded
        // into the number instead: a light's `ShadowTile` addresses the whole buffer, not this
        // frame's slice of it. Indexing from zero would read whichever region some other frame was
        // writing, which is a shadow from one to three frames ago — plausible while standing still
        // and wrong exactly while moving.
        records.Device = Device;
        records.Begin();

        TileBase = (int)(records.Offset / Unsafe.SizeOf<PunctualShadowTileData>());

        var side = Math.Max(TilesPerSide, 1);
        var scale = new Vector2(1f / side, 1f / side);

        for (var index = 0; index < Lights.Count; index++) {
            var light = Lights[index];
            var needed = ShadowProjections.TileCount(light.Kind);

            // ⚠ Cleared before anything else and written back on every path below, so that a light
            // this node skipped or dropped is unshadowed *because this frame said so* rather than
            // because it happened to be unshadowed last frame too. A stale index survives the light
            // it was packed for and shadows a lamp with the tile a different lamp now occupies.
            light.ShadowTile = 0;

            if (needed == 0) {
                Lights[index] = light;
                continue;
            }

            // All of a light's faces or none of them. A point light with four of its six faces
            // rendered is worse than one with none: the two missing directions are lit as though
            // nothing occludes them, which reads as light leaking through walls.
            if (tiles.Count + needed > Capacity) {
                DroppedLights++;
                Lights[index] = light;
                continue;
            }

            // Counted from one, so that the zero every uninitialised record already holds means "no
            // tile" instead of meaning tile zero. See <see cref="RenderLight.ShadowTile" />.
            light.ShadowTile = TileBase + tiles.Count + 1;
            Lights[index] = light;

            for (var face = 0; face < needed; face++) {
                var slot = tiles.Count;
                var offset = new Vector2(slot % side * scale.X, slot / side * scale.Y);

                var projection = light.Kind == LightKind.Point
                    ? ShadowProjections.Cube(light.Position, (CubeFace)face, light.Range, NearPlane)
                    : ShadowProjections.Spot(
                        light.Position,
                        light.Direction,
                        light.OuterAngle,
                        light.Range,
                        NearPlane
                    );

                tiles.Add(
                    new(index, light.Kind == LightKind.Point ? (CubeFace)face : null, projection, scale, offset)
                );

                while (views.Count <= slot) {
                    views.Add(new($"{Name}[{views.Count}]"));
                }

                var view = views[slot];
                view.ViewProjection = projection;
                view.Position = light.Position;
                view.MaximumDistance = 0f;

                compositor.Use(view, CasterStage);
            }
        }

        Upload();
        Publish();
    }

    /// <summary>Puts this frame's tiles where a shading pass can index them.</summary>
    /// <remarks>
    ///     <para>
    ///         One entry rather than none when nothing is shadowed, for the reason the light buffer
    ///         keeps one: the buffer is a <em>binding</em> of the frame's set, a set is written wholly
    ///         or not at all, and a frame whose lights all missed the atlas would otherwise fail to
    ///         bind set 0 and draw nothing. Nobody reads it — every light's index is negative.
    ///     </para>
    ///     <para>
    ///         The matrices have their tile folded in here rather than in the shader, so a lookup is
    ///         one multiply. <see cref="ShadowProjections.Tile" /> says why that has to happen
    ///         somewhere.
    ///     </para>
    /// </remarks>
    void Upload() {
        // `Begin` is Collect's, not this method's — the region has to be chosen before the indices
        // that carry it are written.
        if (Device is null) {
            return;
        }

        if (staged.Length < Math.Max(tiles.Count, 1)) {
            staged = new PunctualShadowTileData[Math.Max(tiles.Count, Capacity)];
        }

        for (var i = 0; i < tiles.Count; i++) {
            var tile = tiles[i];

            staged[i] = new() {
                ViewProjection = ShadowProjections.Tile(tile.ViewProjection, tile.Scale, tile.Offset),
                Scale = tile.Scale,
                Offset = tile.Offset
            };
        }

        if (tiles.Count == 0) {
            staged[0] = default;
        }

        records.Device = Device;
        records.Begin();
        records.Add(staged.AsSpan(0, Math.Max(tiles.Count, 1)));
        records.Upload();
    }

    /// <summary>Hands a shading pass everything about the atlas that the atlas does not carry.</summary>
    void Publish() {
        if (Scene is not { } parameters) {
            return;
        }

        var atlas = AtlasSize;
        var texel = new Vector2(1f / Math.Max(atlas.X, 1), 1f / Math.Max(atlas.Y, 1));

        Write(parameters, ShaderName, texel);

        foreach (var pass in Passes) {
            Write(parameters, pass, texel);
        }
    }

    /// <summary>The same five names under one prefix.</summary>
    void Write(ParameterCollection parameters, string prefix, Vector2 texel) {
        if (records.Buffer.IsValid) {
            parameters.Set(ParameterKeys.New<BufferHandle>($"{prefix}.tiles"), records.Buffer);
        }

        parameters.Set(ParameterKeys.New<Vector2>($"{prefix}.atlasTexelSize"), texel);
        parameters.Set(ParameterKeys.New<float>($"{prefix}.constantBias"), ConstantBias);
        parameters.Set(ParameterKeys.New<float>($"{prefix}.slopeBias"), SlopeBias);

        if (Samplers is { } samplers) {
            // Clamped and linear, on the cascades' terms: clamped because a fragment outside a tile's
            // frustum is rejected before it samples anyway and a wrapped lookup would read the far
            // side of the atlas, linear because the filter takes its own taps and wants each one
            // filtered.
            parameters.Set(ParameterKeys.New<SamplerHandle>($"{prefix}.atlasSampler"), samplers.LinearClamp);
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (tiles.Count == 0 || Atlas.Length == 0) {
            return;
        }

        var atlas = frame.Texture(ToString(), Atlas);
        var format = frame.FormatOf(ToString(), Atlas);
        var side = Math.Max(TilesPerSide, 1);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.DepthAttachment(atlas);

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;
                        context.Output = new([], format);

                        for (var slot = 0; slot < tiles.Count; slot++) {
                            var x = slot % side * Resolution;
                            var y = slot / side * Resolution;

                            graphContext.CommandList.SetViewport(new(x, y, Resolution, Resolution));

                            // The scissor is what makes one atlas safe: a caster whose triangle
                            // crosses a tile edge would otherwise write into the neighbouring tile,
                            // which is another light's shadow or another face of this one.
                            graphContext.CommandList.SetScissor(new(x, y, Resolution, Resolution));

                            // ⚠ Per tile, inside the loop. It is the only thing that differs between
                            // them, and without it the caster pass binds no set 1 at all — every draw
                            // in it refused, and an atlas that stays at its clear value.
                            context.ViewConstants = Constants;
                            compositor.System.Record(views[slot], CasterStage, context);
                        }

                        context.Output = previous;
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
        records.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "PunctualShadows" : Name;
}
