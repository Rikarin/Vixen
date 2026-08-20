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
/// <param name="Slot">
///     Which of the atlas's <see cref="PunctualShadowRenderer.Capacity" /> slots it occupies.
/// </param>
/// <remarks>
///     ⚠ <see cref="Slot" /> is not the tile's position in <see cref="PunctualShadowRenderer.Tiles" />
///     and has not been since the tiles could be <em>kept</em>. A cached lamp holds the slot it was
///     drawn into for as long as it does not move, so the packing is no longer dense in list order
///     and every viewport, scissor and record index has to come from this rather than from a loop
///     counter. It is carried rather than derived because <see cref="Offset" /> is the same fact in
///     fractions and two derivations of one quantity is how an atlas comes to be read a tile along.
/// </remarks>
public readonly record struct ShadowTile(
    int Light,
    CubeFace? Face,
    Matrix4x4 ViewProjection,
    Vector2 Scale,
    Vector2 Offset,
    int Slot = 0
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
    /// <summary>Everything a tile's depth is a function of, except the casters.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The cache's key, and it is the light itself rather than an identity the host
    ///         supplies.</strong> A punctual light's shadow projection is decided entirely by these
    ///         six values — a spot's frustum <em>is</em> its cone, a point's is six of them — so two
    ///         frames in which they are bit-identical are two frames whose tile would rasterise the
    ///         same depth. That makes "the lamp has not moved" the same statement as "the key is
    ///         unchanged", with nothing to keep in step and no field added to
    ///         <see cref="RenderLight" /> for a host to forget to fill.
    ///     </para>
    ///     <para>
    ///         ⚠ Compared bit-for-bit, not within a tolerance. A tolerance would let a lamp drift
    ///         arbitrarily far one epsilon at a time while its shadow stayed where it started, which
    ///         is the one failure a shadow cache must not have. A light that moves at all is a light
    ///         whose tile is redrawn.
    ///     </para>
    /// </remarks>
    readonly record struct TileKey(
        LightKind Kind,
        Vector3 Position,
        Vector3 Direction,
        float Range,
        float OuterAngle,
        float NearPlane
    );

    /// <summary>What one slot of the atlas currently holds, from whenever it was last drawn.</summary>
    struct Resident {
        /// <summary>The light the depth in this slot was rasterised for.</summary>
        public TileKey Key;

        /// <summary>Which cube face of it, or zero for a spot light.</summary>
        public int Face;

        /// <summary>The casters that were drawn into it — see <see cref="CasterHash" />.</summary>
        public ulong Casters;

        /// <summary>What <see cref="CasterVersion" /> was when it was drawn.</summary>
        public int Version;

        /// <summary>Whether anything has ever been drawn here.</summary>
        public bool Drawn;
    }

    readonly List<RenderView> views = [];
    readonly List<ShadowTile> tiles = [];

    // Where each light key lived at the end of last frame, rebuilt from `resident` every Collect
    // rather than carried, so a slot handed to a different light cannot leave a home pointing at it.
    readonly Dictionary<TileKey, int> homes = [];
    // ⚠ 1280 rather than the default 256, and it is load-bearing rather than tidy: a tile's index
    // carries the ring's own offset (see Collect), so the region stride has to be a whole number of
    // records. 1280 is the least common multiple of the eighty-byte record and the 256-byte binding
    // alignment, so it satisfies both whatever the capacity grows to. Left at 256 the base is a
    // fraction, truncates, and every tile is read one or two records along — a shadow from the wrong
    // face of the wrong lamp.
    readonly UploadBuffer<PunctualShadowTileData> records = new("PunctualShadows.Tiles") { Alignment = 1280 };
    PunctualShadowTileData[] staged = [];

    // All indexed by slot, all sized to Capacity, all reset whenever the atlas's shape changes.
    Resident[] resident = [];
    int[] slotLight = [];
    int[] slotFace = [];

    // This frame's key per slot, snapshotted in Collect. Read by Build and by the pass body, which
    // runs at graph-execution time — reaching back into `Lights` from there would be reading a list
    // the host is free to have refilled by then.
    TileKey[] slotKey = [];
    bool[] slotUsed = [];
    bool[] claimed = [];
    ulong[] casterHashes = [];
    bool[] tileStale = [];
    bool[] placed = [];
    int residency = -1;
    int residencyResolution = -1;

    // The cache's own atlas, on TemporalAntialiasingRenderer's terms: a node that keeps pixels
    // between frames owns them, because the graph's pool exists to recycle memory whose lifetime
    // ends inside one.
    TextureHandle cacheTexture;
    TextureViewHandle cacheView;
    TextureDescription cacheDescription;
    Int2 cacheSize;
    bool cacheBuilt;
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

    /// <summary>
    ///     Whether a tile whose lamp and whose casters have not moved may be kept between frames.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The whole cache is this one switch, and off is the path this node always
    ///         had</strong> — one pass, a cleared atlas, every tile of every lamp rasterised again.
    ///         Turning it on makes the node <em>own</em> its atlas: the depth in a tile is about the
    ///         world rather than about a frame, and a graph-declared resource is pooled memory whose
    ///         contents are undefined the moment the last pass that read it ended.
    ///     </para>
    ///     <para>
    ///         <strong>Why this is not <see cref="ShadowMapRenderer.StaticCasterStage" />'s
    ///         shape.</strong> A cascade is fitted to the camera, so its projection moves whenever
    ///         the player does and only the <em>static half</em> of its content is worth keeping —
    ///         which is why that cache needs a second stage, a second atlas and a full-atlas copy
    ///         every frame to put the two halves back together. A punctual light has a position of
    ///         its own and its frustum does not know the camera exists, so the whole tile is
    ///         cacheable and there is nothing to copy: a lamp that does not move keeps the texels it
    ///         already has, and a mover near it invalidates the tiles it is actually in rather than
    ///         being separated out by stage. Sample 13's atlas is 2816² of <c>Depth32Float</c>, so
    ///         the copy this avoids would have been 31 MB read and 31 MB written every frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The atlas the document declares under <see cref="Atlas" /> is replaced, not
    ///         written.</b> The node publishes its own texture into the frame under that name before
    ///         any shading pass resolves it, so a reader sees the cache. The declaration is still
    ///         read, and its extent still checked against <see cref="AtlasSize" />, because a
    ///         document that disagrees with the node about the atlas's shape is a mistake worth
    ///         reporting whichever texture ends up being drawn into.
    ///     </para>
    /// </remarks>
    public bool Cached { get; set; }

    /// <summary>
    ///     Bumped by the host when something changed about a caster that this cannot see.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The escape hatch, and it exists because detection has a horizon.
    ///         <see cref="CasterHash" /> asks the render system which objects a tile draws and where
    ///         they are, so a caster that moved, appeared, disappeared or left the lamp's frustum
    ///         invalidates the tile by itself. What it cannot see is a change with no effect on an
    ///         object's bounds or its transform: a skinned mesh animating inside a fixed bounding
    ///         sphere, an alpha-masked material whose cutout was edited, a mesh swapped underneath a
    ///         render object. Bumping this redraws every tile once.
    ///     </para>
    ///     <para>
    ///         <see cref="ShadowMapRenderer.StaticVersion" />'s twin, and deliberately the *second*
    ///         line of defence rather than the first: that one is a claim the scene makes about
    ///         which objects never move, and a host that gets it wrong gets a shadow where an object
    ///         used to be. This one is only ever needed for changes that leave an object exactly
    ///         where it was.
    ///     </para>
    /// </remarks>
    public int CasterVersion { get; set; }

    /// <summary>
    ///     Where per-object world matrices live, so a caster that only turns still invalidates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without it the cache is blind to rotation</b>, and that is a real picture rather
    ///         than a theoretical one: a bounding sphere is invariant under rotation about its own
    ///         centre, so a fan, a turntable or a door on its hinge would keep a shadow frozen in the
    ///         pose it was first drawn in while everything else in the frame turned. With it, the
    ///         sixteen floats go into the tile's hash and the tile is redrawn the frame the matrix
    ///         changes.
    ///     </para>
    ///     <para>
    ///         <c>TransformRenderFeature.World</c>, normally — <c>CompositorBuilder</c> finds it by
    ///         walking the system's features, the way <c>EnableGpuDriven</c> does, because a key is
    ///         registered against the object store and a node has no other way to reach one. Null
    ///         leaves the hash reading bounds alone, which is what a frame with no transform feature
    ///         in it has anyway; the node says so through <c>Degraded</c> rather than silently.
    ///     </para>
    /// </remarks>
    public RenderDataKey<Matrix4x4>? CasterTransforms { get; set; }

    /// <summary>How many tiles were rasterised this frame.</summary>
    /// <remarks>
    ///     The number the cache is judged by, beside <see cref="TilesKept" />: "it caches" is
    ///     otherwise a claim nothing can check. Equal to <see cref="Tiles" />' count on every frame
    ///     with <see cref="Cached" /> off, which is the honest way to read the uncached path.
    /// </remarks>
    public int TilesDrawn { get; private set; }

    /// <summary>How many tiles this frame reused the depth already in the atlas.</summary>
    public int TilesKept { get; private set; }

    /// <summary>How many tiles have been rasterised since this node was created.</summary>
    /// <remarks>
    ///     <see cref="ShadowMapRenderer.StaticRebuilds" />' twin, for a profiler and for a test that
    ///     runs several frames and wants one number at the end of them.
    /// </remarks>
    public int TileRedraws { get; private set; }

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

    /// <summary>The record the ring currently stands on — where the next write lands.</summary>
    /// <remarks>
    ///     For tests to hold against <see cref="TileBase" /> after a Collect: the two are one region
    ///     or every index this frame wrote addresses records nothing filled. That is not a
    ///     hypothetical — an extra <c>Begin</c> between the base being taken and the data being
    ///     added is exactly how every lamp in a level went silently unshadowed.
    /// </remarks>
    internal int RingBase => (int)(records.Offset / Unsafe.SizeOf<PunctualShadowTileData>());

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

        var capacity = Capacity;
        var side = Math.Max(TilesPerSide, 1);
        var scale = new Vector2(1f / side, 1f / side);

        Reserve(capacity);
        Pack(capacity);

        Degrade(
            (Cached, CasterTransforms) switch {
                (true, _) when Device is null => "caching is on with no Device, so there is no atlas "
                    + "to keep tiles in and every tile is redrawn",
                (true, null) => "caching is on with no CasterTransforms key, so a caster that turns "
                    + "in place keeps the shadow it was first drawn with",
                _ => null
            }
        );

        // Slot order rather than light order, because a retained lamp keeps the slot it was drawn
        // into and the packing is therefore no longer dense. Tiles come out ascending by slot, which
        // is the order Build records them in and the order a test can read.
        for (var slot = 0; slot < capacity; slot++) {
            if (!slotUsed[slot]) {
                continue;
            }

            var index = slotLight[slot];
            var face = slotFace[slot];
            var light = Lights[index];
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

            slotKey[slot] = KeyOf(light);

            tiles.Add(
                new(index, light.Kind == LightKind.Point ? (CubeFace)face : null, projection, scale, offset, slot)
            );

            while (views.Count <= slot) {
                views.Add(new($"{Name}[{views.Count}]"));
            }

            var view = views[slot];
            view.ViewProjection = projection;
            view.Position = light.Position;
            view.MaximumDistance = 0f;

            // Every allocated tile, kept or not. Culling is what produces the list a tile's hash is
            // taken over, so a view this node stopped collecting for is a view whose casters it can
            // no longer be sure of — the cache would then be keeping a picture it cannot check. It
            // is also the cost the cache does *not* remove: caching saves the recording and the
            // rasterisation, not the culling.
            compositor.Use(view, CasterStage);
        }

        Upload(capacity);
        Publish();
    }

    /// <summary>Sizes the per-slot arrays, and forgets everything when the atlas changes shape.</summary>
    /// <remarks>
    ///     A resolution or a tile count that moved makes every cached texel meaningless: the slot a
    ///     lamp held is a different rectangle of a different texture, and the depth in it was
    ///     rasterised through a viewport that no longer exists. Resetting is the only honest answer,
    ///     and it costs one frame of full redraw on a knob nothing turns per frame.
    /// </remarks>
    void Reserve(int capacity) {
        if (residency == capacity && residencyResolution == Resolution) {
            Array.Clear(claimed, 0, capacity);
            Array.Clear(slotUsed, 0, capacity);

            return;
        }

        resident = new Resident[capacity];
        slotLight = new int[capacity];
        slotFace = new int[capacity];
        slotKey = new TileKey[capacity];
        slotUsed = new bool[capacity];
        claimed = new bool[capacity];
        casterHashes = new ulong[capacity];
        tileStale = new bool[capacity];
        residency = capacity;
        residencyResolution = Resolution;
    }

    /// <summary>Decides which lamp gets which slots, keeping a lamp where it was whenever it can.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Two passes, and the order between them is the cache.</strong> The first hands
    ///         every lamp whose key is still resident the run of slots it already owns; only then
    ///         does the second fill the gaps with whatever is left. Doing it the other way round —
    ///         packing densely and then asking what survived — is the same allocation with the
    ///         retention removed, because the first new lamp in the list would take slot zero and
    ///         shift every lamp behind it one along.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Retention can fragment where dense packing cannot</b>, and a lamp dropped for
    ///         want of a <em>contiguous</em> run while the atlas has room is a shadow that
    ///         disappeared for a bookkeeping reason. So the whole frame is repacked densely the
    ///         moment that happens, which invalidates every tile once and then holds: the dense
    ///         packing is the arrangement retention starts from again next frame.
    ///     </para>
    /// </remarks>
    void Pack(int capacity) {
        if (placed.Length < Lights.Count) {
            placed = new bool[Math.Max(Lights.Count, 8)];
        }

        if (Cached && TryPack(capacity, retain: true)) {
            return;
        }

        // The dense fallback, which is also the whole of the uncached path. Every slot is forgotten
        // first, because a lamp landing on a slot some other lamp's depth is in must not be told the
        // depth is its own.
        if (Cached) {
            Array.Clear(resident, 0, capacity);
        }

        Array.Clear(claimed, 0, capacity);
        Array.Clear(slotUsed, 0, capacity);
        DroppedLights = 0;
        TryPack(capacity, retain: false);
    }

    /// <summary>One packing attempt. False when it fragmented and has to be redone densely.</summary>
    bool TryPack(int capacity, bool retain) {
        Array.Clear(placed, 0, Lights.Count);

        // ⚠ Cleared before anything is packed and written back only by Claim, so that a light this
        // node skipped or dropped is unshadowed *because this frame said so* rather than because it
        // happened to be unshadowed last frame too. A stale index survives the light it was packed
        // for and shadows a lamp with the tile a different lamp now occupies.
        for (var index = 0; index < Lights.Count; index++) {
            var light = Lights[index];

            if (light.ShadowTile == 0) {
                continue;
            }

            light.ShadowTile = 0;
            Lights[index] = light;
        }

        var occupied = 0;

        if (retain) {
            homes.Clear();

            for (var slot = 0; slot < capacity; slot++) {
                if (resident[slot] is { Drawn: true, Face: 0 }) {
                    homes[resident[slot].Key] = slot;
                }
            }

            // Every lamp that can stay where it is, before anything is allowed to move.
            for (var index = 0; index < Lights.Count; index++) {
                var light = Lights[index];
                var needed = ShadowProjections.TileCount(light.Kind);
                var key = KeyOf(light);

                if (needed == 0 || !homes.TryGetValue(key, out var slot)) {
                    continue;
                }

                if (!Free(slot, needed, capacity) || !Holds(slot, needed, key)) {
                    continue;
                }

                Claim(index, slot, needed);
                occupied += needed;
            }
        }

        for (var index = 0; index < Lights.Count; index++) {
            var needed = ShadowProjections.TileCount(Lights[index].Kind);

            if (needed == 0 || placed[index]) {
                continue;
            }

            var slot = Run(needed, capacity);

            if (slot < 0) {
                // All of a light's faces or none of them. A point light with four of its six faces
                // rendered is worse than one with none: the two missing directions are lit as though
                // nothing occludes them, which reads as light leaking through walls.
                if (retain && occupied + needed <= capacity) {
                    return false;
                }

                DroppedLights++;

                continue;
            }

            Claim(index, slot, needed);
            occupied += needed;
        }

        return true;
    }

    /// <summary>Marks a run of slots as this light's, and writes its index back into the light.</summary>
    void Claim(int index, int slot, int needed) {
        var light = Lights[index];

        for (var face = 0; face < needed; face++) {
            claimed[slot + face] = true;
            slotUsed[slot + face] = true;
            slotLight[slot + face] = index;
            slotFace[slot + face] = face;
        }

        placed[index] = true;

        // Counted from one, so that the zero every uninitialised record already holds means "no
        // tile" instead of meaning tile zero. See <see cref="RenderLight.ShadowTile" />.
        light.ShadowTile = TileBase + slot + 1;
        Lights[index] = light;
    }

    /// <summary>The lowest run of unclaimed slots that is long enough, or −1.</summary>
    int Run(int needed, int capacity) {
        for (var slot = 0; slot + needed <= capacity; slot++) {
            if (Free(slot, needed, capacity)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Whether a run of slots is unclaimed and inside the atlas.</summary>
    bool Free(int slot, int needed, int capacity) {
        if (slot < 0 || slot + needed > capacity) {
            return false;
        }

        for (var face = 0; face < needed; face++) {
            if (claimed[slot + face]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a run still holds exactly this light's faces, in order.</summary>
    bool Holds(int slot, int needed, TileKey key) {
        for (var face = 0; face < needed; face++) {
            ref var slotResident = ref resident[slot + face];

            if (!slotResident.Drawn || slotResident.Face != face || slotResident.Key != key) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Everything a lamp's tile depth is a function of, except the casters.</summary>
    TileKey KeyOf(in RenderLight light) =>
        new(light.Kind, light.Position, light.Direction, light.Range, light.OuterAngle, NearPlane);

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
    void Upload(int capacity) {
        // `Begin` is Collect's, not this method's — the region has to be chosen before the indices
        // that carry it are written.
        if (Device is null) {
            return;
        }

        if (staged.Length < capacity) {
            staged = new PunctualShadowTileData[capacity];
        }

        // ⚠ **Indexed by slot, not by position in `tiles`.** A light's `ShadowTile` is its slot plus
        // the ring base, and with the cache on a retained lamp keeps a slot that may sit anywhere in
        // the atlas — so writing the records densely would have every kept lamp read the record of
        // whichever lamp happened to be that many tiles into this frame's list. Dense packing made
        // the two indices the same number for as long as nothing was kept, which is exactly the kind
        // of agreement that holds until the day it does not.
        var high = 0;

        Array.Clear(staged, 0, capacity);

        for (var i = 0; i < tiles.Count; i++) {
            var tile = tiles[i];

            staged[tile.Slot] = new() {
                ViewProjection = ShadowProjections.Tile(tile.ViewProjection, tile.Scale, tile.Offset),
                Scale = tile.Scale,
                Offset = tile.Offset
            };

            high = Math.Max(high, tile.Slot + 1);
        }

        // No `Begin` here — Collect already advanced the ring and derived `TileBase` from the
        // region it landed on. A second `Begin` moves to the *next* region, so every index written
        // above points at memory this upload never touches: with two frames in flight that is a
        // region nothing ever writes, and every lamp is silently unshadowed from frame two onward.
        records.Add(staged.AsSpan(0, Math.Max(high, 1)));
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
            // Clamped and nearest, on the cascades' terms: clamped because a fragment outside a
            // tile's frustum is rejected before it samples anyway and a wrapped lookup would read
            // the far side of the atlas, nearest because the tap compares *after* sampling — a
            // linear sampler blends four depths into a value no surface wrote, and linear filtering
            // of Depth32Float is optional in Vulkan besides. The PCF kernel supplies the softness.
            parameters.Set(ParameterKeys.New<SamplerHandle>($"{prefix}.atlasSampler"), samplers.PointClamp);
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Atlas.Length == 0) {
            return;
        }

        // ⚠ Zero lamps still clears the atlas, and the clear is load-bearing twice over. First for
        // the graph: whoever shades declares a *read* of this atlas by name — the Standard Frame's
        // Main pass does, and a hand-authored document's publish line is the same statement — and a
        // read with no producer is refused at compile, which turned "a scene with no point lights
        // yet" into a crash on frame one. Second for the picture: an atlas left unwritten holds
        // whatever was in that memory, and a cleared depth is the one value the lookup reads as
        // "unshadowed", which is the true answer for a frame with no lamps in it.

        var atlas = frame.Texture(ToString(), Atlas);
        var format = frame.FormatOf(ToString(), Atlas);
        var side = Math.Max(TilesPerSide, 1);

        // The declared extent has to be this node's own arithmetic, on ShadowMapRenderer's exact
        // terms: a tile viewport outside the texture is an empty scissor that rasterizes nothing
        // and says nothing, while the lookup's folded rectangles go on addressing the extent the
        // node believes in. The document writes the number out because a graph resource has to
        // declare one; this is what keeps the two from drifting apart silently.
        var declared = frame.Graph.DescribeTexture(atlas);
        var required = AtlasSize;

        if (declared.Width != required.X || declared.Height != required.Y) {
            throw new CompositorBindingException(
                $"'{ToString()}' packs {Capacity} tile(s) at {Resolution} texels into a "
                + $"{required.X}x{required.Y} atlas, but '{Atlas}' is declared "
                + $"{declared.Width}x{declared.Height}. The resource's extent must be "
                + "tilesPerSide x resolution on each side, or tiles fall outside the texture."
            );
        }

        if (!Cached || Device is null) {
            TilesDrawn = tiles.Count;
            TilesKept = 0;
            TilesMoved = 0;
            TilesDisturbed = 0;
            TileRedraws += tiles.Count;

            frame.Graph.AddPass(
                ToString(),
                pass => {
                    pass.DepthAttachment(atlas);

                    pass.Execute(
                        graphContext => {
                            var context = frame.Context(graphContext.CommandList);
                            var previous = context.Output;
                            context.Output = new([], format);

                            for (var i = 0; i < tiles.Count; i++) {
                                var slot = tiles[i].Slot;
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

            return;
        }

        Cache(compositor, frame, format, side, required);
    }

    /// <summary>Redraws the tiles that went stale, and leaves the rest exactly as they are.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>One render pass per redrawn tile, and the reason is the clear.</strong> A
    ///         <c>LoadAction.Clear</c> is confined by the pass's <em>render area</em> — a scissor
    ///         confines draws, and the load op runs before any draw — and a render area is a per-pass
    ///         fact. One pass over the whole atlas would therefore wipe every kept tile in it, which
    ///         is the one thing this exists not to do. <c>VirtualShadowRenderer.Record</c> learned the
    ///         same lesson the expensive way and this is its arrangement.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The graph pass declares a write and no attachment.</b> The write is what gives the
    ///         shading pass's read a producer — a read with no producer is refused at compile — and
    ///         what places the barriers into and out of <c>DepthStencilWrite</c>. The absent
    ///         attachment is what lets the body open its own passes with their own render areas,
    ///         which the graph's pass builder has no way to describe. <c>SideEffect</c> is then
    ///         required rather than tidy: a frame in which every tile was kept records nothing at
    ///         all, and a pass that writes nothing visible is culled.
    ///     </para>
    /// </remarks>
    void Cache(GraphicsCompositor compositor, CompositorFrame frame, PixelFormat format, int side, Int2 required) {
        Allocate(Device!, required, format);

        // Imported every frame, and the entry state is the whole difference between the first frame
        // and the rest: nothing has been written yet, so the contents are `Undefined` and a backend
        // may discard them. Claiming `ShaderRead` on frame one is a transition from a layout the
        // texture is not in, which is a validation error on Vulkan and undefined behaviour without
        // the layer.
        var cache = frame.Graph.ImportTexture(
            cacheTexture,
            cacheView,
            cacheDescription,
            cacheBuilt ? ResourceState.ShaderRead : ResourceState.Undefined,
            ResourceState.ShaderRead
        );

        // ⚠ **Published under the document's own name, replacing what it declared.** A shading pass
        // resolves `Atlas` by name and must reach the texture the tiles are actually kept in; the
        // declaration stays in the document because that is where an extent is written down, and it
        // has already been checked against this node's arithmetic above. The transient it named is
        // then read by nobody and the graph drops it.
        frame.Add(Atlas, cache, cacheDescription);

        TilesDrawn = 0;
        TilesKept = 0;
        TilesMoved = 0;
        TilesDisturbed = 0;

        for (var i = 0; i < tiles.Count; i++) {
            var slot = tiles[i].Slot;
            var hash = CasterHash(compositor.System, views[slot]);

            casterHashes[slot] = hash;
            tileStale[slot] = Stale(slot, hash);

            if (tileStale[slot]) {
                TilesDrawn++;
            } else {
                TilesKept++;
            }
        }

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Writes(cache, ResourceState.DepthStencilWrite);
                pass.SideEffect();

                pass.Execute(
                    graphContext => {
                        var list = graphContext.CommandList;
                        var view = graphContext.View(cache);
                        var context = frame.Context(list);
                        var previous = context.Output;

                        // No colour attachment at all. A shadow pass writes depth and nothing else,
                        // and a bound colour target would be bandwidth spent on a value nobody reads.
                        context.Output = new([], format);

                        for (var i = 0; i < tiles.Count; i++) {
                            var slot = tiles[i].Slot;

                            if (!tileStale[slot]) {
                                continue;
                            }

                            var rect = new ScissorRect(
                                slot % side * Resolution,
                                slot / side * Resolution,
                                Resolution,
                                Resolution
                            );

                            list.BeginRenderPass(
                                new(
                                    [],
                                    new(view, LoadAction.Clear, StoreAction.Store, 0f),
                                    $"{this}.Tile{slot}",
                                    rect
                                )
                            );

                            list.SetViewport(new(rect.X, rect.Y, rect.Width, rect.Height));

                            // The scissor still matters for the draws — it confines a caster whose
                            // triangle crosses the tile edge, which the render area does not.
                            list.SetScissor(rect);

                            context.ViewConstants = Constants;
                            compositor.System.Record(views[slot], CasterStage, context);

                            list.EndRenderPass();

                            resident[slot] = new() {
                                Key = slotKey[slot],
                                Face = slotFace[slot],
                                Casters = casterHashes[slot],
                                Version = CasterVersion,
                                Drawn = true
                            };

                            TileRedraws++;
                        }

                        context.Output = previous;

                        // Set here rather than beside the allocation, because what makes the next
                        // frame's `ShaderRead` entry state true is that this pass ran — the graph
                        // transitions the texture whether or not a tile was drawn into it.
                        cacheBuilt = true;
                    }
                );
            }
        );
    }

    /// <summary>Whether a slot's depth still describes what this frame would draw into it.</summary>
    /// <remarks>
    ///     <strong>The invalidation rule, in one place and stated whole.</strong> A tile is kept when
    ///     the slot has been drawn at least once, holds this lamp's key and this face, was drawn from
    ///     the same casters in the same places, and was drawn at the current
    ///     <see cref="CasterVersion" />. Anything else is a redraw. A rule that never fires is a
    ///     stale shadow; one that fires on everything is the uncached path with more code.
    /// </remarks>
    bool Stale(int slot, ulong hash) {
        ref var slotResident = ref resident[slot];

        if (!slotResident.Drawn) {
            return true;
        }

        if (slotResident.Face != slotFace[slot] || slotResident.Key != slotKey[slot]) {
            TilesMoved++;

            return true;
        }

        if (slotResident.Casters != hash) {
            TilesDisturbed++;

            return true;
        }

        return slotResident.Version != CasterVersion;
    }

    /// <summary>How many of this frame's redraws were the lamp itself moving.</summary>
    /// <remarks>
    ///     Beside <see cref="TilesDisturbed" /> because a cache that saves nothing saves nothing for
    ///     one of two reasons, and they have different fixes: a lamp whose position is recomputed to a
    ///     different float every frame never keeps anything however still the scene is, and a tile
    ///     whose caster list churns is a scene problem rather than a lighting one. Without the split
    ///     the only visible symptom of either is <see cref="TilesDrawn" /> refusing to fall.
    /// </remarks>
    public int TilesMoved { get; private set; }

    /// <summary>How many were something in the tile changing.</summary>
    public int TilesDisturbed { get; private set; }

    /// <summary>What this tile would rasterise, as one number.</summary>
    /// <remarks>
    ///     <para>
    ///         Taken over the render system's own sorted list for the tile's view, narrowed to the
    ///         entries whose bounds actually meet the tile's frustum, which is what makes this
    ///         <em>detection</em> rather than a claim: a caster that moved, one that was added or
    ///         removed, and one that entered or left this lamp's frustum all change the set or the
    ///         bounds in it. The order is the sort's, which is deterministic, so two frames that draw
    ///         the same thing hash the same.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounds are invariant under rotation</b>, which is why the world matrix goes in
    ///         too when <see cref="CasterTransforms" /> says where to find one. Without that a caster
    ///         turning on the spot never invalidates anything.
    ///     </para>
    /// </remarks>
    ulong CasterHash(RenderSystem system, RenderView view) {
        var nodes = system.Nodes(view, CasterStage);
        var frustum = view.Frustum;
        var transforms = CasterTransforms is { } key ? system.Objects.Data.Data(key) : default;
        var hash = 14695981039346656037UL;
        var counted = 0UL;

        for (var i = 0; i < nodes.Count; i++) {
            var id = nodes[i].Object;

            ref var caster = ref system.Objects[id];

            // ⚠ **Tested again here, and the reason is that the list is a *superset* rather than the
            // answer.** With GPU culling and no readback the host is handed the conservative set —
            // everything that could be visible — because the real answer never leaves the device;
            // sample 13 runs that way and every tile's list is therefore the whole level. Hashing it
            // makes one lamp's cache depend on a caster sixty metres away, so a walking player
            // invalidates all 108 tiles and the cache saves exactly nothing while every counter says
            // it is working. Re-testing costs one sphere against six planes per entry of a list this
            // loop already walks, and it is the difference between a cache and a very thorough way
            // of drawing the same thing.
            if (frustum.Contains(caster.Bounds) == ContainmentType.Disjoint) {
                continue;
            }

            counted++;

            Fold(ref hash, (ulong)(uint)id.Index);
            Fold(ref hash, BitConverter.SingleToUInt32Bits(caster.Bounds.Center.X));
            Fold(ref hash, BitConverter.SingleToUInt32Bits(caster.Bounds.Center.Y));
            Fold(ref hash, BitConverter.SingleToUInt32Bits(caster.Bounds.Center.Z));
            Fold(ref hash, BitConverter.SingleToUInt32Bits(caster.Bounds.Radius));

            if (transforms.IsEmpty || id.Index >= transforms.Length) {
                continue;
            }

            foreach (var element in MemoryMarshal.Cast<Matrix4x4, float>(transforms.Slice(id.Index, 1))) {
                Fold(ref hash, BitConverter.SingleToUInt32Bits(element));
            }
        }

        Fold(ref hash, counted);

        return hash;
    }

    /// <summary>FNV-1a, one value at a time.</summary>
    static void Fold(ref ulong hash, ulong value) => hash = (hash ^ value) * 1099511628211UL;

    /// <summary>Creates the cache's atlas, or recreates it when its shape changed.</summary>
    void Allocate(IGraphicsDevice device, Int2 size, PixelFormat format) {
        if (cacheSize == size && cacheTexture.IsValid && cacheDescription.Format == format) {
            return;
        }

        Release(device);

        cacheDescription = new(
            format,
            Math.Max(size.X, 1),
            Math.Max(size.Y, 1),
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: $"{this}.Cache"
        );

        cacheTexture = device.CreateTexture(cacheDescription);
        cacheView = device.CreateTextureView(cacheTexture);
        cacheSize = size;
        cacheBuilt = false;

        // A new texture holds nothing, so nothing in it may be claimed as a lamp's.
        Array.Clear(resident);
    }

    void Release(IGraphicsDevice device) {
        if (cacheView.IsValid) {
            device.Destroy(cacheView);
        }

        if (cacheTexture.IsValid) {
            device.Destroy(cacheTexture);
        }

        cacheView = default;
        cacheTexture = default;
        cacheSize = default;
        cacheBuilt = false;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (Device is { } device) {
            Release(device);
        }

        records.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "PunctualShadows" : Name;
}
