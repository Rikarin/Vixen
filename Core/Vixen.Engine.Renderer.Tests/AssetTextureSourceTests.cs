// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A material's texture reaches the device and the material's own parameters.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a bindless material was missing on the far side.</b> A feature carries the name of a
///         map, the material carries which texture that is, and the shader reads a <c>uint</c> — and
///         until this existed nothing turned the reference into anything a table could hold a slot for,
///         so every textured material in every project sampled the fallback.
///     </para>
///     <para>
///         Over the null device rather than a real one on purpose: what is being asserted is the
///         <em>route</em> — bundle to decode to upload to view to parameter — and the pixels landing
///         correctly is <c>BindlessSamplingDeviceTests</c>'s claim to make on hardware.
///     </para>
/// </remarks>
public sealed class AssetTextureSourceTests : IDisposable {
    readonly NullDevice device = new(new());

    static readonly AssetReference Bark = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>
    ///     A texture is read, decoded, uploaded and viewable — and not one moment before its copy is
    ///     recorded.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ordering is the assertion.</b> A view handed out before the copy was on a list is a
    ///     material sampling undefined memory for a frame, which on one backend is black and on another
    ///     is whatever the last frame left there. So the ask before <see cref="AssetTextureSource.Update" />
    ///     has to be false even once the bytes are decoded, and it is the <c>Update</c> that makes it
    ///     true.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Asked and updated in a loop rather than after a sleep, and that is the assertion rather
    ///     than a concession to a slow machine.</b> The decode is off-thread, so "has it landed yet" has
    ///     no answer a test can wait a fixed number of milliseconds for — a run that slept long enough
    ///     on an idle laptop asserts nothing on a loaded CI runner except that the runner was slow. What
    ///     is actually being claimed holds on every iteration and needs no timing at all: the ask before
    ///     an <see cref="AssetTextureSource.Update" /> is false <em>every</em> time, including the last
    ///     one, and it is that update which makes the next ask true.
    /// </remarks>
    [Fact]
    public void ATextureIsViewableOnlyOnceItsCopyIsRecorded() {
        using var source = new AssetTextureSource(device, Content());

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience) {
            // Decoded or not, and deliberately not answerable either way: nothing has copied the pixels.
            Assert.False(source.TryGet(Bark, out _));
            Assert.Equal(0, source.Failed);

            var commands = device.BeginCommandList();

            source.Update(commands);
            commands.Finish();

            if (source.TryGet(Bark, out var view)) {
                Assert.True(view.IsValid);
                Assert.Equal(1, source.Loaded);

                return;
            }

            Assert.Equal(0, source.Loaded);
            Thread.Sleep(5);
        }

        Assert.Fail($"the decode never landed in {Patience}");
    }

    /// <summary>A reference this build shipped nothing for is counted, not thrown for.</summary>
    [Fact]
    public void AReferenceNothingShippedIsCountedAsFailed() {
        using var source = new AssetTextureSource(device, Content());

        Assert.False(source.TryGet(new(new AssetId(Guid.NewGuid()), SubAssetId.Main), out _));
        Assert.Equal(1, source.Failed);
    }

    /// <summary>
    ///     The whole route: a material's texture ends up in the material's own parameters, under the
    ///     name its feature samples it by.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The name is what makes this a join rather than a coincidence.</b>
    ///         <c>MaterialRenderFeature.TextureIndices</c> pairs the shader's composed <c>uint</c> —
    ///         <c>ForwardPlus.CompositeSurface.TexturedMetalRoughnessSurface.baseColorIndex</c> — with
    ///         the material's own <c>baseColorMap</c>, and reads the view out of the material under
    ///         exactly that key. A source that set it under any other name would upload every texture in
    ///         the level and hand the slots to nobody.
    ///     </para>
    ///     <para>
    ///         Several frames, because that is what it takes: one reads the material, one records the
    ///         texture's copy and one paints. A material is drawable throughout, sampling the table's
    ///         fallback until then — which is why the loop below is a loop over frames rather than a
    ///         sleep long enough for two off-thread reads on whichever machine happens to run it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMaterialsTextureLandsInItsParametersUnderTheNameItsFeatureSamples() {
        var assets = Content();

        using var textures = new AssetTextureSource(device, assets);
        using var materials = new AssetMaterialSource(assets, textures);

        Material material = null!;
        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience && !materials.TryGet(Hero, out material)) {
            Thread.Sleep(5);
        }

        Assert.NotNull(material);
        Assert.Equal(1, materials.Unpainted);

        var key = ParameterKeys.New<TextureViewHandle>("baseColorMap");

        Assert.False(material.Parameters.Has(key));

        waited.Restart();

        while (waited.Elapsed < Patience) {
            var commands = device.BeginCommandList();

            materials.Update(commands);
            commands.Finish();

            if (material.Parameters.Has(key)) {
                Assert.True(material.Parameters.Get(key).IsValid);
                Assert.Equal(0, materials.Unpainted);

                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the material was never painted in {Patience}");
    }

    static readonly AssetReference Hero = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>
    ///     How long a decode off the thread pool is given before the test calls it a failure rather than
    ///     a slow machine.
    /// </summary>
    /// <remarks>
    ///     Generous on purpose. Nothing here waits the whole of it in the ordinary case — each loop
    ///     returns on the frame the work lands — so the only run that pays this is one that was going to
    ///     fail anyway, and a bound tight enough to be worth shortening is one that fails on a busy CI
    ///     runner for no reason.
    /// </remarks>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A content manager holding one KTX2 texture and one material that samples it.</summary>
    static AssetManager Content() {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        // Written exactly as TextureImporter writes it: the container is the artefact, so a change to
        // how one is packed breaks this rather than being found in a game.
        var texture = database.WriteRaw(
            ContentHash.TypeId(typeof(TextureData)),
            [],
            Ktx2.Write(Pixels()),
            CompressionMethod.None
        );

        var material = database.Write(
            new MaterialContent {
                Features = [new TexturedMetalRoughnessFeature()],
                Textures = [new("baseColorMap", Bark)]
            }
        );

        var bundle = new BundleWriter();
        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [
                new("bark", texture, "Main", ContentProvider.Local, [], [], 0, Reference: Bark),
                new("hero", material, "Main", ContentProvider.Local, [], [], 0, Reference: Hero)
            ],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }

    /// <summary>Four pixels, which is enough to be a texture and small enough to read in a failure.</summary>
    static TextureData Pixels() {
        var data = new TextureData(PixelFormat.Rgba8UNorm, 2, 2);

        data.PixelSpan().Fill(0x7f);

        return data;
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
