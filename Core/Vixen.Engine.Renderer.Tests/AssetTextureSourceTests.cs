// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Imaging;
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
    [Fact]
    public void ATextureIsViewableOnlyOnceItsCopyIsRecorded() {
        using var source = new AssetTextureSource(device, Content());

        Assert.True(Settles(source));

        // Decoded and created, and deliberately still not answerable: nothing has copied the pixels.
        Assert.False(source.TryGet(Bark, out _));
        Assert.Equal(0, source.Loaded);

        var commands = device.BeginCommandList();

        source.Update(commands);
        commands.Finish();

        Assert.True(source.TryGet(Bark, out var view));
        Assert.True(view.IsValid);
        Assert.Equal(1, source.Loaded);
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
    ///         Two frames, because that is what it takes: the first records the copy and the second
    ///         paints. A material is drawable throughout, sampling the table's fallback until then.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMaterialsTextureLandsInItsParametersUnderTheNameItsFeatureSamples() {
        var assets = Content();

        using var textures = new AssetTextureSource(device, assets);
        using var materials = new AssetMaterialSource(assets, textures);

        Material material = null!;

        for (var attempt = 0; attempt < 200 && !materials.TryGet(Hero, out material); attempt++) {
            Thread.Sleep(5);
        }

        Assert.NotNull(material);
        Assert.Equal(1, materials.Unpainted);

        var key = ParameterKeys.New<TextureViewHandle>("baseColorMap");

        Assert.False(material.Parameters.Has(key));

        // Twice, because the first pass starts the texture and records its copy and the second is what
        // can answer with a view.
        for (var frame = 0; frame < 2; frame++) {
            var commands = device.BeginCommandList();

            materials.Update(commands);
            commands.Finish();

            Thread.Sleep(20);
        }

        var last = device.BeginCommandList();

        materials.Update(last);
        last.Finish();

        Assert.Equal(0, materials.Unpainted);
        Assert.True(material.Parameters.Has(key));
        Assert.True(material.Parameters.Get(key).IsValid);
    }

    static readonly AssetReference Hero = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>Asks until the decode lands, which is what a frame does by asking again next frame.</summary>
    static bool Settles(AssetTextureSource source) {
        for (var attempt = 0; attempt < 200; attempt++) {
            source.TryGet(Bark, out _);

            if (source.Requested > 0 && source.Failed == 0) {
                Thread.Sleep(20);
                source.TryGet(Bark, out _);
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

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
