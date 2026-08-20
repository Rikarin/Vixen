// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Keeping a lamp's shadow tile between frames — docs/overview.md § 1.9, "only the directional
///     cascades are cached".
/// </summary>
/// <remarks>
///     <para>
///         <strong>The invalidation rule is the whole subject, so every test here is one clause of
///         it.</strong> A tile is kept when the slot has been drawn, holds this lamp's key and this
///         face, was drawn from the same casters in the same places, and was drawn at the current
///         <see cref="PunctualShadowRenderer.CasterVersion" />. A cache that never invalidates is a
///         stale shadow; one that invalidates on everything is the uncached path with more code, and
///         <see cref="PunctualShadowRenderer.TilesDrawn" /> is what tells the two apart.
///     </para>
///     <para>
///         The shape differs from <see cref="ShadowCacheTests" /> on purpose and the reason is
///         geometric rather than stylistic: a cascade is fitted to the camera, so its projection
///         moves when the player does and only its static half is worth keeping. A punctual light
///         carries its own frustum and does not know the camera exists, so the <em>whole</em> tile is
///         cacheable — no second stage, no second atlas, and no full-atlas copy per frame.
///     </para>
/// </remarks>
public class PunctualShadowCacheTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() { Key = key, Stages = [new(ShaderStage.Vertex, [1, 2, 3, 4], "main")] };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Caster { get; init; }
        public required PunctualShadowRenderer Shadows { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required RenderDataKey<Matrix4x4> World { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Shadows.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }

    /// <summary>
    ///     A visibility group that answers "everything, everywhere", which is not a fiction.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It is what a GPU-culled frame with no readback hands the host.</b> The real answer
    ///     never leaves the device — <c>GpuDrawArguments</c> turns the bits into indirect draws
    ///     there — so the CPU work lists are the <em>conservative</em> set, and sample 13 runs
    ///     exactly this way: every tile's caster list is the whole level. A cache that trusted the
    ///     list would make one lamp's tile depend on a caster sixty metres away, and a walking player
    ///     would invalidate all 108 tiles while every counter reported the cache working.
    /// </remarks>
    sealed class SeesEverything : IVisibilityGroup {
        ulong[][] words = [];
        int[] counts = [];

        public int ViewCount => words.Length;

        public bool IsVisible(int viewIndex, RenderObjectId id) =>
            (uint)viewIndex < (uint)words.Length
            && id.Index >= 0
            && id.Index >> 6 < words[viewIndex].Length
            && (words[viewIndex][id.Index >> 6] & (1UL << (id.Index & 63))) != 0;

        public ReadOnlySpan<ulong> Words(int viewIndex) =>
            (uint)viewIndex < (uint)words.Length ? words[viewIndex] : [];

        public void Hide(int viewIndex, RenderObjectId id) {
            if (!IsVisible(viewIndex, id)) {
                return;
            }

            words[viewIndex][id.Index >> 6] &= ~(1UL << (id.Index & 63));
            counts[viewIndex]--;
        }

        public int VisibleCount(int viewIndex) => (uint)viewIndex < (uint)counts.Length ? counts[viewIndex] : 0;

        public void Cull(RenderObjectStore store, IReadOnlyList<RenderView> views, JobScheduler? scheduler = null) {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(views);

            words = new ulong[views.Count][];
            counts = new int[views.Count];

            for (var v = 0; v < views.Count; v++) {
                words[v] = new ulong[((store.Count + 63) / 64) + 1];

                for (var i = 0; i < store.Count; i++) {
                    ref var candidate = ref store[new(i)];

                    // The stage mask and nothing else. No frustum, which is the whole point.
                    if (!candidate.IsAlive || !candidate.Stages.Intersects(views[v].Stages)) {
                        continue;
                    }

                    words[v][i >> 6] |= 1UL << (i & 63);
                    counts[v]++;
                }
            }
        }

        public void Dispose() { }
    }

    Harness Build(bool cached = true, bool transforms = true, int tilesPerSide = 4, bool conservative = false) {
        var system = new RenderSystem();

        if (conservative) {
            system.Visibility = new SeesEverything();
        }

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        // The key a `TransformRenderFeature` would have registered, registered directly: what the
        // node wants is somewhere to read a world matrix from, and a whole feature would drag a
        // device, a permutation and an upload ring into a test about invalidation.
        var world = system.Objects.Data.Register<Matrix4x4>();
        var caster = system.AddStage(new("ShadowCaster"));

        var shadows = new PunctualShadowRenderer {
            Name = "Punctual",
            CasterStage = caster,
            Atlas = "PunctualAtlas",
            Resolution = 256,
            TilesPerSide = tilesPerSide,
            Device = device,
            Cached = cached,
            CasterTransforms = transforms ? world : null
        };

        var size = shadows.AtlasSize;

        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            size.X,
            size.Y,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: "PunctualAtlas"
        );

        var texture = device.CreateTexture(description);
        var compositor = new GraphicsCompositor(system) { Game = shadows, FrameSize = size };

        compositor.Imports["PunctualAtlas"] = new(texture, device.CreateTextureView(texture), description);

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Caster = caster,
            Shadows = shadows,
            Meshes = meshes,
            Materials = materials,
            World = world,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static RenderObjectId AddCaster(Harness h, Vector3 at, float radius = 2f) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(at, radius), Stages = h.Caster.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.System.Objects.Data.Data(h.World)[id.Index] = Matrix4x4.FromTranslation(at);
        h.Materials.Assign(h.System, id, new("DepthOnly"));

        return id;
    }

    /// <summary>A spot light pointing down −Z from where it stands.</summary>
    static RenderLight Spot(Vector3 at) =>
        RenderLight.Spot(at, new(0f, 0f, -1f), 40f, 0.3f, 0.5f, new Color3(1f));

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    int PassesNamed(string name) =>
        device.Recorder!
            .OfKind(RecordedCommandKind.BeginRenderPass)
            .Count(command => string.Equals(command.Text, name, StringComparison.Ordinal));

    int TilePasses() =>
        device.Recorder!
            .OfKind(RecordedCommandKind.BeginRenderPass)
            .Count(command => command.Text?.StartsWith("Punctual.Tile", StringComparison.Ordinal) == true);

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- What is kept -------------------------------------------------------

    /// <summary>
    ///     A scene where nothing moves rasterises every tile once and keeps them thereafter.
    /// </summary>
    /// <remarks>
    ///     Seven tiles — a spot is one and a point is six — drawn on the first frame and on no other.
    ///     This is the number the whole thing is judged by: without it "it caches" is a claim nothing
    ///     can check.
    /// </remarks>
    [Fact]
    public void A_settled_scene_draws_every_tile_once() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(RenderLight.Point(new(0f, 0f, -8f), 20f, new(1f)));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        Assert.Equal(7, h.Shadows.TilesDrawn);
        Assert.Equal(0, h.Shadows.TilesKept);

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);
        Assert.Equal(7, h.Shadows.TilesKept);
        Assert.Equal(7, h.Shadows.TileRedraws);
    }

    /// <summary>And each redrawn tile is a render pass of its own, because the clear is.</summary>
    /// <remarks>
    ///     ⚠ A <c>LoadAction.Clear</c> is confined by the pass's <em>render area</em> and never by the
    ///     scissor, and a render area is a per-pass fact. One pass over the atlas with a viewport per
    ///     tile — which is what the uncached path does — would wipe every kept tile the moment it
    ///     began. Three frames and seven passes is the assertion that it does not.
    /// </remarks>
    [Fact]
    public void Only_the_tiles_that_went_stale_open_a_pass() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(RenderLight.Point(new(0f, 0f, -8f), 20f, new(1f)));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);
        Frame(h);

        Assert.Equal(7, TilePasses());

        // And the uncached node's single whole-atlas pass is not among them.
        Assert.Equal(0, PassesNamed("Punctual"));

        // Every one of them confined to its own tile, and to a rectangle the size of one. A pass
        // that said nothing here would clear the whole atlas, and the seven above would leave one
        // tile holding depth and six holding the clear value — which reads as six lamps that stopped
        // occluding anything, with every counter still saying the tile was drawn.
        foreach (var command in device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass)) {
            Assert.Equal(((long)256 << 32) | 256, command.E);
            Assert.Equal(0, command.C % 256);
            Assert.Equal(0, command.D % 256);
        }
    }

    // --- What invalidates ---------------------------------------------------

    /// <summary>A lamp that moved redraws its own tiles and nobody else's.</summary>
    [Fact]
    public void A_moved_lamp_redraws_only_its_own_tiles() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(Spot(new(100f, 0f, 0f)));
        AddCaster(h, new(0f, 0f, -10f));
        AddCaster(h, new(100f, 0f, -10f));

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);

        h.Shadows.Lights[0] = Spot(new(0f, 1f, 0f));
        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
        Assert.Equal(1, h.Shadows.TilesKept);
    }

    /// <summary>A caster that moved redraws the tiles it is in and nobody else's.</summary>
    /// <remarks>
    ///     Detection rather than a claim, and this is what it buys over the cascades' arrangement: no
    ///     stage says which objects move, because the render system's own culled list for the tile's
    ///     view already says which objects are in it and where.
    /// </remarks>
    [Fact]
    public void A_moved_caster_redraws_only_the_tiles_it_is_in() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(Spot(new(100f, 0f, 0f)));
        var mover = AddCaster(h, new(0f, 0f, -10f));
        AddCaster(h, new(100f, 0f, -10f));

        Frame(h);

        // Verify the instrument before believing what it says: two tiles, one caster drawn into
        // each. If culling put both casters in one tile — or neither in either — every assertion
        // below would pass for a reason that has nothing to do with the cache.
        Assert.Equal(2, h.Shadows.TilesDrawn);
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));

        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));

        h.System.Objects[mover].Bounds = new(new(0f, 0f, -12f), 2f);
        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
        Assert.Equal(1, h.Shadows.TilesKept);

        // One more draw, not two: the lamp a hundred metres away kept its tile and recorded nothing.
        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>A caster that appears in a lamp's frustum redraws that lamp's tile.</summary>
    /// <remarks>
    ///     The third case the rule has to cover, and the one a light-and-caster comparison alone
    ///     would miss: neither the lamp nor any object that was already there changed, and the tile's
    ///     content did.
    /// </remarks>
    [Fact]
    public void A_caster_that_appears_redraws_the_tile_it_appears_in() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(Spot(new(100f, 0f, 0f)));
        AddCaster(h, new(100f, 0f, -10f));

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);

        AddCaster(h, new(0f, 0f, -10f));
        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
        Assert.Equal(1, h.Shadows.TilesKept);
    }

    /// <summary>
    ///     And a conservative cull does not make everything invalidate everything.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The measurement that nearly closed this feature as worthless.</b> Sample 13 culls
    ///         on the device without a readback, so the CPU work list for every one of its 108 lamp
    ///         tiles is the whole level — and a cache that hashed that list redrew all 108 tiles every
    ///         frame a player walked, saving nothing while <c>TilesDisturbed</c> reported 108 and
    ///         every other counter said the cache was fine.
    ///     </para>
    ///     <para>
    ///         The fix is to re-test the list against the tile's own frustum, which is what this
    ///         asserts: two lamps a hundred metres apart, a caster moving at one of them, and the
    ///         other one's tile kept — with a visibility group that put both casters in both lists.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_conservative_cull_does_not_make_every_mover_invalidate_every_tile() {
        using var h = Build(conservative: true);
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(Spot(new(100f, 0f, 0f)));
        var mover = AddCaster(h, new(0f, 0f, -10f));
        AddCaster(h, new(100f, 0f, -10f));

        Frame(h);

        // Verify the instrument: both casters really are in both tiles' lists.
        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.Draw));

        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);

        h.System.Objects[mover].Bounds = new(new(0f, 0f, -12f), 2f);
        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
        Assert.Equal(1, h.Shadows.TilesKept);
    }

    /// <summary>A caster that only turns redraws too, because its matrix is in the hash.</summary>
    /// <remarks>
    ///     ⚠ A bounding sphere is invariant under rotation about its own centre, so bounds alone
    ///     cannot see a fan, a turntable or a door on its hinge — and a cache that cannot see them
    ///     freezes their shadows in the pose they were first drawn in.
    /// </remarks>
    [Fact]
    public void A_caster_that_only_turns_redraws_its_tile() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        var turner = AddCaster(h, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);

        h.System.Objects.Data.Data(h.World)[turner.Index] =
            Matrix4x4.FromRotationY(1f) * Matrix4x4.FromTranslation(new(0f, 0f, -10f));

        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
    }

    /// <summary>Without the transform key it cannot, which is exactly what the key is for.</summary>
    /// <remarks>
    ///     Asserted rather than left as a remark, because the node reports it through
    ///     <c>Degraded</c> and a bargain nothing tests is a bargain nobody knows they made.
    /// </remarks>
    [Fact]
    public void Without_the_transform_key_a_turn_is_not_seen() {
        using var h = Build(transforms: false);
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        var turner = AddCaster(h, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);

        h.System.Objects.Data.Data(h.World)[turner.Index] =
            Matrix4x4.FromRotationY(1f) * Matrix4x4.FromTranslation(new(0f, 0f, -10f));

        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);
        Assert.NotNull(h.Shadows.Degraded);
    }

    /// <summary>And the version is the escape hatch for everything detection cannot see.</summary>
    [Fact]
    public void Bumping_the_caster_version_redraws_everything() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(RenderLight.Point(new(0f, 0f, -8f), 20f, new(1f)));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);

        h.Shadows.CasterVersion++;
        Frame(h);

        Assert.Equal(7, h.Shadows.TilesDrawn);

        Frame(h);

        Assert.Equal(0, h.Shadows.TilesDrawn);
    }

    // --- What the slots have to do ------------------------------------------

    /// <summary>
    ///     A lamp keeps the slot it was drawn into even when the light list is reordered.
    /// </summary>
    /// <remarks>
    ///     Retention is what makes a cache possible at all: pack densely in list order and the first
    ///     new lamp shifts every lamp behind it one slot along, which invalidates the whole atlas for
    ///     a reason that has nothing to do with the scene.
    /// </remarks>
    [Fact]
    public void A_lamp_keeps_its_slot_when_the_list_is_reordered() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(Spot(new(100f, 0f, 0f)));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        var first = h.Shadows.Tiles.Single(tile => tile.Light == 0).Slot;
        var second = h.Shadows.Tiles.Single(tile => tile.Light == 1).Slot;

        Assert.NotEqual(first, second);

        (h.Shadows.Lights[0], h.Shadows.Lights[1]) = (h.Shadows.Lights[1], h.Shadows.Lights[0]);
        Frame(h);

        // The lamps swapped places in the list; neither moved in the world, so neither moved in the
        // atlas and neither was redrawn.
        Assert.Equal(second, h.Shadows.Tiles.Single(tile => tile.Light == 0).Slot);
        Assert.Equal(first, h.Shadows.Tiles.Single(tile => tile.Light == 1).Slot);
        Assert.Equal(0, h.Shadows.TilesDrawn);
    }

    /// <summary>
    ///     And the index a light publishes is its slot, not its position in the tile list.
    /// </summary>
    /// <remarks>
    ///     ⚠ The two were the same number for as long as the packing was dense, which is exactly the
    ///     kind of agreement that holds until the day something is kept. A light reading the record
    ///     of whichever lamp happened to be that many tiles into the list is a lamp shadowed by
    ///     somebody else's geometry — plausible, and invisible.
    /// </remarks>
    [Fact]
    public void A_lights_published_index_addresses_its_slot() {
        using var h = Build();
        h.Shadows.Lights.Add(RenderLight.Point(new(0f, 0f, -8f), 20f, new(1f)));
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        // Drop the point light. Its six slots free up; the spot keeps the seventh it already had.
        h.Shadows.Lights.RemoveAt(0);
        Frame(h);

        var tile = h.Shadows.Tiles.Single();

        Assert.Equal(6, tile.Slot);
        Assert.Equal(h.Shadows.TileBase + tile.Slot + 1, h.Shadows.Lights[0].ShadowTile);
        Assert.Equal(0, h.Shadows.TilesDrawn);
    }

    /// <summary>A lamp with no room is dropped whole, exactly as it always was.</summary>
    [Fact]
    public void A_lamp_that_does_not_fit_is_dropped_whole() {
        using var h = Build(tilesPerSide: 2);
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.Lights.Add(RenderLight.Point(new(0f, 0f, -8f), 20f, new(1f)));

        Frame(h);

        Assert.Equal(1, h.Shadows.DroppedLights);
        Assert.Single(h.Shadows.Tiles);
        Assert.Equal(0, h.Shadows.Lights[1].ShadowTile);
    }

    // --- The uncached path --------------------------------------------------

    /// <summary>With the cache off the frame is exactly what it always was.</summary>
    [Fact]
    public void The_uncached_path_draws_every_tile_every_frame() {
        using var h = Build(cached: false);
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);
        Frame(h);

        Assert.Equal(1, h.Shadows.TilesDrawn);
        Assert.Equal(0, h.Shadows.TilesKept);
        Assert.Equal(3, h.Shadows.TileRedraws);
        Assert.Equal(3, PassesNamed("Punctual"));
        Assert.Equal(0, TilePasses());
    }

    /// <summary>
    ///     A document whose atlas disagrees with the node's arithmetic is still refused by name.
    /// </summary>
    /// <remarks>
    ///     The cache owns its own texture, so the declaration is no longer what is drawn into — which
    ///     would have been a fine reason to stop checking it, and a bad one. A document and a node
    ///     that disagree about the atlas's shape disagree about something, and the extent is the only
    ///     place the disagreement is visible.
    /// </remarks>
    [Fact]
    public void A_document_that_disagrees_about_the_extent_is_still_refused() {
        using var h = Build();
        h.Shadows.Lights.Add(Spot(Vector3.Zero));
        h.Shadows.TilesPerSide = 3;

        Assert.Throws<CompositorBindingException>(() => Frame(h));
    }
}
