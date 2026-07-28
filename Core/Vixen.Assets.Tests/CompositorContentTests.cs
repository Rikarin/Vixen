// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>
///     A compositor loaded by address, out of a bundle, and built into a frame.
/// </summary>
/// <remarks>
///     <para>
///         The last step of doc 06's third idea. That the frame is a serialisable record graph was
///         asserted in the renderer's own tests; that it survives the baked binary form was asserted
///         there too. What neither could reach is the step between: a content build writes a chunk, a
///         catalog gives it an address, and a game asks for it by that address without knowing which
///         bundle it is in or whether it is downloaded yet.
///     </para>
///     <para>
///         It can only be asserted here, because the renderer does not reference the content system
///         and should not — which is also why the claim was open for so long. Nothing was missing;
///         nothing had put the two halves in one room.
///     </para>
/// </remarks>
public class CompositorContentTests {
    /// <summary>The frame this test writes, loads and builds.</summary>
    /// <remarks>
    ///     Built as objects rather than parsed from YAML, because the editor's format is not what a
    ///     shipping game reads — the point of the exercise is the chunk.
    /// </remarks>
    static GraphicsCompositorAsset Frame => new() {
        Version = CompositorBuilder.SupportedVersion,
        Stages = [new() { Name = "Opaque" }],
        Resources = [new() { Name = "SceneColour" }],
        Game = new SequenceAsset {
            Name = "Frame",
            Children = [
                new RenderPassAsset {
                    Name = "Main",
                    ColourTargets = ["SceneColour"],
                    Children = [new SingleStageAsset { Name = "Draw", View = "Camera", Stage = "Opaque" }]
                },
                new BloomAsset { Name = "Bloom", Source = "SceneColour", Output = "BloomResult", Levels = 3 }
            ]
        }
    };

    /// <summary>A catalog and a bundle holding one compositor, as a content build would leave them.</summary>
    static AssetManager Published(string address, GraphicsCompositorAsset asset) {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var id = new ObjectDatabase(backend).Write(asset);

        var bundle = new BundleWriter();
        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new(address, id, "Main", ContentProvider.Local, [], [], 0)],
            [new("Main", "", default, 0, 0, CompressionMethod.Lz4, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }

    /// <summary>
    ///     A compositor is content: written to a bundle, asked for by address, and still a frame.
    /// </summary>
    [Fact]
    public void ACompositorLoadsByAddress() {
        var assets = Published("compositors/forward", Frame);

        var handle = assets.Load<GraphicsCompositorAsset>("compositors/forward", TestContext.Current.CancellationToken);
        var loaded = handle.Result;

        Assert.Equal(CompositorBuilder.SupportedVersion, loaded.Version);

        var sequence = Assert.IsType<SequenceAsset>(loaded.Game);

        Assert.Equal("Main", sequence.Children[0].Name);
        Assert.Equal(3, Assert.IsType<BloomAsset>(sequence.Children[1]).Levels);
    }

    /// <summary>
    ///     And the thing that came out of the bundle builds a running compositor.
    /// </summary>
    /// <remarks>
    ///     The half that makes the first test worth having. A record graph that deserialises and
    ///     cannot be built is a document that loads and does nothing, which is the failure this whole
    ///     arrangement is supposed to make impossible.
    /// </remarks>
    [Fact]
    public void AnAddressedCompositorBuildsAFrame() {
        var assets = Published("compositors/forward", Frame);

        var handle = assets.Load<GraphicsCompositorAsset>("compositors/forward", TestContext.Current.CancellationToken);

        using var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        builder.Views["Camera"] = new("Camera");

        // The bloom node's kind is defined downstream of the builder, so the document names something
        // the builder cannot know — and whoever defines it supplies the factory. Registering it is
        // what a host does once; without it the build throws naming the kind, rather than producing a
        // frame quietly missing a pass.
        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(handle.Result);
        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);

        Assert.Equal(2, sequence.Children.Count);
        Assert.IsType<RenderPassRenderer>(sequence.Children[0]);
        Assert.IsType<BloomRenderer>(sequence.Children[1]);

        // The stage the document declared, created by the build and reachable by the name it used.
        Assert.True(builder.Stages.ContainsKey("Opaque"));
    }

    /// <summary>An address the catalog does not have fails before anything is built.</summary>
    [Fact]
    public void AnUnknownCompositorAddressFails() {
        var assets = Published("compositors/forward", Frame);

        Assert.ThrowsAny<Exception>(() => assets.Load<GraphicsCompositorAsset>("compositors/deferred", TestContext.Current.CancellationToken));
    }
}
