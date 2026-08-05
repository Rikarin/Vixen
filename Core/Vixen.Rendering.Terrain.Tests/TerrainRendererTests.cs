// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.Terrain;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     The terrain renderer, against a recording device — [docs/plan/31 § T2].
/// </summary>
/// <remarks>
///     What a headless device can say: how many draws, how many instances, what was bound and how
///     many bytes moved. What it cannot say is whether the result looks like ground, which is the
///     golden image and needs a GPU.
/// </remarks>
public sealed class TerrainRendererTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static TerrainDescription Shape(int tiles = 2) =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    TerrainRenderer Build(TerrainMap terrain, int gridQuads = 8) =>
        new(
            device,
            terrain,
            new(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "terrain.vs"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "terrain.fs")
            ),
            new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float),
            TerrainLodRanges.Default with { NearRange = 32f },
            gridQuads
        );

    static TerrainView View(Vector3 at) {
        var view = Matrix4x4.LookAt(at, at + new Vector3(1f, -0.5f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI * 0.9f, 1f, 0.1f, 100_000f);

        return new(view * projection, at, new(view * projection));
    }

    ICommandList Record() => device.BeginCommandList();

    /// <summary>Draws a frame into a throwaway target, and submits it.</summary>
    /// <remarks>
    ///     ⚠ <b>The submit is what makes the recorder see anything.</b> A null command list buffers
    ///     its own calls and flushes them into the <c>CommandRecorder</c> at submit rather than as it
    ///     records — which is deliberate, because lists record on their own threads. A test that
    ///     asserted before submitting would be asserting on an empty stream and would pass for a
    ///     renderer that drew nothing.
    /// </remarks>
    void Draw(TerrainRenderer renderer) {
        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget))
        );

        var commands = device.BeginCommandList();
        commands.BeginRenderPass(new([new(target)], name: "terrain"));
        renderer.Record(commands);
        commands.EndRenderPass();
        commands.Finish();

        device.GraphicsQueue.Submit([commands]);
    }

    [Fact]
    public void ATerrainIsOneDrawHoweverManyPatchesItTakes() {
        var terrain = new TerrainMap(Shape(tiles: 2));
        using var renderer = Build(terrain);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.True(patches > 1, $"only {patches} patches were selected.");

        device.Recorder!.Clear();
        Draw(renderer);

        var draw = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(1, renderer.Draws);
        Assert.Equal(TerrainGridPatch.IndexCount(8), (int)draw.A);
        Assert.Equal(patches, (int)draw.B);
    }

    [Fact]
    public void ItBindsNoVertexBufferBecauseTheLatticeIsTheVertexIndex() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();
        Draw(renderer);

        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
    }

    [Fact]
    public void ItBindsOneDescriptorSetForTheWholeTerrain() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();
        Draw(renderer);

        var bind = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));
        Assert.Equal((long)DescriptorSetSlot.PerMaterial, bind.A);
    }

    [Fact]
    public void ATerrainNothingCanSeeDrawsNothing() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        // Looking away from the terrain, from a long way off.
        var at = new Vector3(-10_000f, 500f, -10_000f);
        var view = Matrix4x4.LookAt(at, at - new Vector3(1f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 8f, 1f, 0.1f, 500f);

        var commands = Record();
        Assert.Equal(0, renderer.Upload(commands, new(view * projection, at, new(view * projection))));

        device.Recorder!.Clear();
        Draw(renderer);

        Assert.Equal(0, renderer.Draws);
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>
    ///     A frame in which nothing was sculpted copies no heights.
    /// </summary>
    /// <remarks>
    ///     The claim that makes editing affordable. Uploading the whole heightmap per frame would put
    ///     a 33 MB transfer on a 4 km² terrain behind every mouse move.
    /// </remarks>
    [Fact]
    public void AFrameThatChangedNothingUploadsNoHeights() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        var commands = Record();

        // The first frame stages the whole thing, because nothing has been uploaded yet.
        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        Assert.True(renderer.UploadedBytes > 0);

        renderer.Upload(commands, View(new(11f, 40f, 11f)));
        Assert.Equal(0, renderer.UploadedBytes);
    }

    /// <summary>
    ///     A stroke on one tile moves that tile's rows, not the terrain's.
    /// </summary>
    [Fact]
    public void AStrokeOnOneTileUploadsAFractionOfTheHeightmap() {
        var terrain = new TerrainMap(Shape(tiles: 4));
        var layer = terrain.AddLayer("Sculpt");
        using var renderer = Build(terrain);

        var commands = Record();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        var whole = renderer.UploadedBytes;
        Assert.True(whole > 0);

        TerrainSculpt.Sculpt(
            terrain, layer,
            TerrainBrush.Default with { Radius = 3f, Strength = 1f },
            new(new(8f, 8f)), 10f
        );

        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.True(renderer.UploadedBytes > 0, "the stroke moved nothing.");
        Assert.True(
            renderer.UploadedBytes < whole / 2,
            $"a one-tile stroke moved {renderer.UploadedBytes} of {whole} bytes."
        );
    }

    /// <summary>A stroke on one tile copies that tile's blocks and nobody else's.</summary>
    /// <remarks>
    ///     ⚠ <b>The block, not a band of rows — which is the whole of what the split buys.</b> The
    ///     packed layout made a rectangle's copy a full-width band, because a buffer-to-texture copy
    ///     reads its source with one row pitch. A block is contiguous, so the copy count is the dirty
    ///     tiles times the chain rather than the whole world's width times its height.
    /// </remarks>
    [Fact]
    public void AStrokeOnOneTileCopiesOnlyThatTilesBlocks() {
        var terrain = new TerrainMap(Shape(tiles: 4));
        var layer = terrain.AddLayer("Sculpt");
        using var renderer = Build(terrain);

        // ⚠ A command list each, because the recorder only sees a list when it is submitted — one
        // list holding both uploads replays the first frame's whole-terrain copy along with the
        // stroke's, and the count then says nothing about either.
        var first = Record();

        renderer.Upload(first, View(new(10f, 40f, 10f)));
        first.Finish();
        device.GraphicsQueue.Submit([first]);

        TerrainSculpt.Sculpt(
            terrain, layer,
            TerrainBrush.Default with { Radius = 3f, Strength = 1f },
            new(new(8f, 8f)), 10f
        );

        device.Recorder!.Clear();

        var commands = Record();

        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var copies = device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture);
        var atlas = new TerrainAtlas(terrain.Description);

        // One tile, every level, and the weightmaps the layout declares — and nothing for the
        // fifteen tiles the stroke did not touch.
        Assert.True(copies > 0, "the stroke copied nothing.");
        Assert.True(
            copies <= (atlas.LevelCount * (1 + TerrainRenderer.MaxWeightMaps)) + 1,
            $"a one-tile stroke made {copies} copies, which is more than one tile's chain and mask."
        );
    }

    /// <summary>Every level of the chain is uploaded, not only level 0.</summary>
    /// <remarks>
    ///     ⚠ <b>A chain whose top is fresh and whose tail is a frame old</b> draws the sculpted tile
    ///     correctly up close and as it was from a distance — which reads as the edit not having taken
    ///     until you walk towards it.
    /// </remarks>
    [Fact]
    public void TheWholeChainIsUploaded() {
        var terrain = new TerrainMap(Shape(tiles: 1));
        using var renderer = Build(terrain);

        var commands = Record();

        device.Recorder!.Clear();
        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var atlas = new TerrainAtlas(terrain.Description);
        var copies = device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture);

        Assert.True(atlas.LevelCount > 1, "the fixture has one level, so it proves nothing.");

        // The chain, plus the tile's hole mask, plus the two one-texel layer defaults the first frame
        // fills — a sampled texture cannot be host-written, so those need a command list and the
        // constructor has none.
        Assert.Equal(atlas.LevelCount + 1 + 2, copies);
    }

    // --- The layer textures ------------------------------------------------

    /// <summary>A renderer with no texture source still draws.</summary>
    /// <remarks>
    ///     ⚠ <b>Every layer slot is bound a default at construction</b>, because a descriptor array
    ///     with a hole in it is undefined behaviour on most drivers and a terrain gains and loses
    ///     layers while the renderer is alive. A terrain with no source draws its weights in white,
    ///     which is what a freshly created layer should look like.
    /// </remarks>
    [Fact]
    public void ARendererWithNoTextureSourceResolvesNothingAndStillDraws() {
        var terrain = new TerrainMap(Shape());

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        using var renderer = Build(terrain);
        var commands = Record();

        renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.Null(renderer.Textures);
        Assert.Equal(0, renderer.ResolvedTextures);
        Assert.True(renderer.PatchCount > 0);
    }

    /// <summary>A source that answers is asked for every layer that names a texture.</summary>
    [Fact]
    public void EveryLayerThatNamesATextureIsResolved() {
        var terrain = new TerrainMap(Shape());

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass", Surface = "T/grass-s" });
        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Rock") with { Albedo = "T/rock" });

        // Named but not assigned: the third layer must not be asked for anything.
        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Sand"));

        var source = new Source(device);

        using var renderer = Build(terrain);

        renderer.Textures = source;

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));

        Assert.Equal(3, renderer.ResolvedTextures);
        Assert.Equal(["T/grass", "T/grass-s", "T/rock"], source.Asked.Order());
    }

    /// <summary>A reference the source has not loaded yet gets the default, and is asked again.</summary>
    /// <remarks>
    ///     ⚠ <b>The frame a layer is assigned is the frame its texture is not resident.</b> A
    ///     renderer that asked once would show the default for ever; one that blocked would drop
    ///     whatever the load took. Asking again next frame is the only one of the three that is both
    ///     correct and free.
    /// </remarks>
    [Fact]
    public void AReferenceThatArrivesLateIsPickedUpOnTheNextFrame() {
        var terrain = new TerrainMap(Shape());

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        var source = new Source(device) { Answers = false };

        using var renderer = Build(terrain);

        renderer.Textures = source;

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));
        Assert.Equal(0, renderer.ResolvedTextures);

        source.Answers = true;

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));
        Assert.Equal(1, renderer.ResolvedTextures);
    }

    /// <summary>And a set the renderer resized keeps the textures it had resolved.</summary>
    /// <remarks>
    ///     ⚠ <b>A resized set is a new set.</b> Growing the patch buffer creates descriptor sets that
    ///     have never been written, so a renderer that only bound the layers when they changed would
    ///     silently revert every layer to its default the first time a view selected more patches
    ///     than the buffer held.
    /// </remarks>
    [Fact]
    public void ResizingTheBufferKeepsTheResolvedTextures() {
        var terrain = new TerrainMap(Shape(tiles: 4));

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        var source = new Source(device);

        using var renderer = Build(terrain);

        renderer.Textures = source;

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));
        Assert.Equal(1, renderer.ResolvedTextures);

        // A second frame after a resize would rebind; this asserts the resolve survived it rather
        // than that it happened again.
        renderer.Upload(Record(), View(new(10f, 400f, 10f)));
        Assert.Equal(1, renderer.ResolvedTextures);
    }

    /// <summary>A layer rebind writes the set this frame draws from, and consecutive frames differ.</summary>
    /// <remarks>
    ///     ⚠ <b>A descriptor set a submitted frame is reading may not be updated at all.</b> Without
    ///     <c>VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT</c> — which this layout does not declare —
    ///     writing the whole ring when a layer texture changes is a validation error per set the GPU
    ///     still holds, and on a streaming source it is one every frame the residency moves. What
    ///     makes it correct is writing the slot <c>Upload</c> just advanced to, which is the slot
    ///     <c>Record</c> is about to bind and the one no submitted frame can be holding.
    /// </remarks>
    [Fact]
    public void ALayerRebindWritesTheSetThisFrameBinds() {
        var terrain = new TerrainMap(Shape());

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        using var renderer = Build(terrain);

        // A streamer's mip swap answers with a different view for the same reference, which is what
        // makes the rebind happen every frame rather than once.
        renderer.Textures = new Source(device) { Swaps = true };

        var written = new long[3];
        var bound = new long[3];

        for (var frame = 0; frame < written.Length; frame++) {
            renderer.Upload(Record(), View(new(10f, 40f, 10f)));

            device.Recorder!.Clear();
            Draw(renderer);

            written[frame] = (long)renderer.LastLayerRebind.Value.Packed;
            bound[frame] = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet)).B;

            Assert.Equal(bound[frame], written[frame]);
        }

        Assert.NotEqual(written[0], written[1]);
        Assert.NotEqual(written[1], written[2]);
    }

    /// <summary>A change is paid one set per upload, and a settled terrain pays nothing.</summary>
    /// <remarks>
    ///     The other half of the ring: a texture that arrived owes every slot a rebind, so the slots
    ///     behind this one are written as their own uploads come round rather than all at once. A
    ///     renderer that never cleared the debt would write a set a frame for ever, and one that
    ///     cleared it after the first slot would draw yesterday's texture on every other frame.
    /// </remarks>
    [Fact]
    public void ARebindIsOwedToEverySlotAndPaidOnePerUpload() {
        var terrain = new TerrainMap(Shape());

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        using var renderer = Build(terrain);

        renderer.Textures = new Source(device);

        // The ring is FramesInFlight × the uploads one frame may make, and the null device's is two.
        var slots = device.FramesInFlight * 4;

        for (var upload = 0; upload < slots; upload++) {
            var before = device.DescriptorWrites;

            renderer.Upload(Record(), View(new(10f, 40f, 10f)));

            Assert.Equal(TerrainRenderer.MaxLayers * 2, device.DescriptorWrites - before);
        }

        var settled = device.DescriptorWrites;

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));

        Assert.Equal(settled, device.DescriptorWrites);
    }

    /// <summary>A texture source that answers with a real view for anything it is asked.</summary>
    sealed class Source(NullDevice device) : ITerrainTextures {
        readonly Dictionary<string, TextureViewHandle> views = [];

        /// <summary>Every reference it was asked for, in order.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>Whether it has the bytes yet.</summary>
        public bool Answers { get; set; } = true;

        /// <summary>Whether the same reference answers with a new view each time, as a mip swap does.</summary>
        public bool Swaps { get; set; }

        public TextureViewHandle Resolve(string reference) {
            Asked.Add(reference);

            if (!Answers) {
                return default;
            }

            if (Swaps) {
                return device.CreateTextureView(
                    device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: reference))
                );
            }

            if (!views.TryGetValue(reference, out var view)) {
                view = device.CreateTextureView(
                    device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: reference))
                );

                views[reference] = view;
            }

            return view;
        }
    }

    // --- Staging, the ring and the barriers --------------------------------

    /// <summary>Every copy of a frame reads its own bytes of staging.</summary>
    /// <remarks>
    ///     ⚠ <b>A recorded copy reads its source at submit, not at record.</b> One staging buffer
    ///     rewritten at offset zero per tile hands every recorded copy the last tile's data — the
    ///     first full upload then replicates one tile's heights into every block, and all four
    ///     weightmaps get the fourth map's channels. Distinct source offsets are the whole fix, so
    ///     this asserts no two copies of the frame share a source location.
    /// </remarks>
    [Fact]
    public void EveryCopyOfAFrameReadsADistinctStagingOffset() {
        var terrain = new TerrainMap(Shape(tiles: 2));

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        using var renderer = Build(terrain);

        device.Recorder!.Clear();

        var commands = Record();

        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var copies = device.Recorder.Commands
            .Where(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture)
            .Select(entry => (Buffer: entry.A, Offset: entry.B))
            .ToList();

        Assert.True(copies.Count > 4, $"only {copies.Count} copies prove nothing about offsets.");
        Assert.Equal(copies.Count, copies.Distinct().Count());
    }

    /// <summary>The four weightmaps are copied from four different stagings of the chain.</summary>
    /// <remarks>
    ///     The map loop packs each weightmap's channels into the same CPU array in turn, so all four
    ///     copies reading one staging offset is all four textures holding map 3.
    /// </remarks>
    [Fact]
    public void EachWeightMapIsStagedApart() {
        var terrain = new TerrainMap(Shape(tiles: 1));

        terrain.Weights.AddLayer(TerrainLayerDescription.Of("Grass") with { Albedo = "T/grass" });

        using var renderer = Build(terrain);

        device.Recorder!.Clear();

        var commands = Record();

        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // Level-0 weight copies land on four destinations; each must read a distinct source offset.
        var atlas = new TerrainAtlas(terrain.Description);
        var copies = device.Recorder.Commands
            .Where(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture)
            .GroupBy(entry => entry.C)
            .Where(group => group.Count() == atlas.LevelCount)
            .ToList();

        Assert.True(copies.Count >= TerrainRenderer.MaxWeightMaps + 1, "the weightmaps were not all copied.");

        var starts = copies.Select(group => (group.First().A, group.First().B)).ToList();

        Assert.Equal(starts.Count, starts.Distinct().Count());
    }

    /// <summary>Two Uploads in one frame draw from two descriptor sets.</summary>
    /// <remarks>
    ///     ⚠ <b>An editor is not one view.</b> The constants and the records ride the slot ring, so
    ///     two panes uploading before either draws must land in different slots — one shared
    ///     constants buffer would hand both panes the second pane's ViewProjection.
    /// </remarks>
    [Fact]
    public void EachUploadOfAFrameDrawsFromItsOwnSlot() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();
        Draw(renderer);

        var first = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));

        renderer.Upload(Record(), View(new(50f, 40f, 50f)));

        device.Recorder.Clear();
        Draw(renderer);

        var second = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindDescriptorSet));

        Assert.NotEqual(first.B, second.B);
    }

    /// <summary>A frame's copies are fenced into the copy state and back out of it.</summary>
    /// <remarks>
    ///     ⚠ The terrain's textures are named into a descriptor set rather than read through the
    ///     render graph, so nothing else transitions them: a copy into an untransitioned texture and
    ///     a draw sampling one left in the copy state are both validation errors on a real device,
    ///     which the recorder cannot raise — so the shape of the stream is asserted instead.
    /// </remarks>
    [Fact]
    public void TheCopiesAreBracketedByBarriers() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        device.Recorder!.Clear();

        var commands = Record();

        renderer.Upload(commands, View(new(10f, 40f, 10f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var stream = device.Recorder.Commands.ToList();
        var copies = stream.Where(entry => entry.Kind == RecordedCommandKind.CopyBufferToTexture).ToList();
        var barriers = stream.Where(entry => entry.Kind == RecordedCommandKind.Barrier).ToList();

        Assert.NotEmpty(copies);
        Assert.Equal(2, barriers.Count);

        Assert.True(barriers[0].Sequence < copies.Min(entry => entry.Sequence), "the copies began before the barrier in.");
        Assert.True(barriers[1].Sequence > copies.Max(entry => entry.Sequence), "the barrier out came before the last copy.");
        Assert.True(barriers.All(entry => entry.B > 0), "a barrier group carried no textures.");
    }

    /// <summary>And a frame with nothing to copy records no barriers at all.</summary>
    [Fact]
    public void AFrameWithNoCopiesRecordsNoBarriers() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain);

        renderer.Upload(Record(), View(new(10f, 40f, 10f)));

        device.Recorder!.Clear();

        var commands = Record();

        renderer.Upload(commands, View(new(11f, 40f, 11f)));
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.Barrier));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.CopyBufferToTexture));
    }

    [Fact]
    public void TheTriangleCountIsThePatchesTimesTheLattice() {
        var terrain = new TerrainMap(Shape());
        using var renderer = Build(terrain, gridQuads: 8);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(10f, 40f, 10f)));

        Assert.Equal(patches * 8 * 8 * 2, renderer.Triangles);
    }

    [Fact]
    public void TheRecordBufferGrowsRatherThanDroppingPatches() {
        var terrain = new TerrainMap(Shape(tiles: 4));
        using var renderer = Build(terrain, gridQuads: 8);

        var commands = Record();
        var patches = renderer.Upload(commands, View(new(120f, 20f, 120f)));

        Assert.True(renderer.Capacity >= patches);
        Assert.Equal(patches, renderer.PatchCount);
    }

    [Fact]
    public void AShaderlessRendererIsRefused() {
        var terrain = new TerrainMap(Shape());

        Assert.Throws<ArgumentException>(
            () => new TerrainRenderer(
                device,
                terrain,
                default,
                new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float),
                TerrainLodRanges.Default
            )
        );
    }

    [Fact]
    public void DisposingTwiceIsHarmless() {
        var terrain = new TerrainMap(Shape());
        var renderer = Build(terrain);

        renderer.Dispose();
        renderer.Dispose();
    }
}
