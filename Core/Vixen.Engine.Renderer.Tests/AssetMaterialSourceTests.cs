// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Renderer;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     A material reference becomes a compiled material, through the content manager a game has.
/// </summary>
/// <remarks>
///     <para>
///         <b>Over a real <see cref="AssetManager" /> and a real bundle</b>, because the interesting
///         part is the join rather than the compile: <see cref="MaterialCompiler" /> already had
///         tests, and what did not exist was anything that turned an address into a document and a
///         document into a material. A fake manager would assert the half that already worked.
///     </para>
///     <para>
///         The chunk is written the way <c>MaterialImporter</c> writes it — <c>Serializer</c> over a
///         <see cref="MaterialContent" /> — so a change to the content format breaks this rather than
///         being discovered in a game.
///     </para>
/// </remarks>
public sealed class AssetMaterialSourceTests {
    static readonly AssetReference Hero = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>The join: a reference in, a material out, compiled from what the build wrote.</summary>
    /// <remarks>
    ///     The <c>metalness</c> assertion is what makes this a test of the <em>document</em> rather than
    ///     of the compiler's defaults: an implementation that ignored the chunk and compiled an empty
    ///     descriptor would produce a material with no such parameter at all.
    /// </remarks>
    [Fact]
    public void AMaterialReferenceCompilesToTheMaterialTheBuildWrote() {
        using var source = new AssetMaterialSource(
            Content(
                new MaterialContent {
                    Shader = "ForwardPlus",
                    Features = [new MetalRoughnessFeature { Metalness = 1f, Roughness = 0.125f }]
                }
            )
        );

        // Loading is asynchronous, so the first ask starts it and the answer arrives afterwards. That
        // is the protocol rather than an inconvenience — see IMaterialSource.
        Settles(source, out var material);

        Assert.Equal("ForwardPlus", material.ShaderName);

        var key = ParameterKeys.New<float>("ForwardPlus.CompositeSurface.MetalRoughnessSurface.roughness");

        Assert.True(material.Parameters.Has(key));
        Assert.Equal(0.125f, material.Parameters.Get(key));
    }

    /// <summary>Two entities naming one material get one material.</summary>
    /// <remarks>
    ///     The economy <c>MaterialRenderFeature</c> is built on: one material is one descriptor set, one
    ///     uniform block and one resolved variant. A source that compiled per ask would be correct and
    ///     would multiply every per-material cost in the frame by the instance count.
    /// </remarks>
    [Fact]
    public void OneReferenceIsOneMaterialHoweverOftenItIsAsked() {
        using var source = new AssetMaterialSource(Content(new()));

        Settles(source, out var first);

        Assert.True(source.TryGet(Hero, out var second));

        Assert.Same(first, second);
        Assert.Equal(1, source.Requested);
        Assert.Equal(1, source.Loaded);
    }

    /// <summary>A reference this build shipped nothing for is counted, not thrown for.</summary>
    /// <remarks>
    ///     ⚠ A frame that threw would take the level with it for one entity pointing at something
    ///     deleted. What it looks like instead is an object drawn in the host's material, and the number
    ///     is the only thing that says why.
    /// </remarks>
    [Fact]
    public void AReferenceNothingShippedIsCountedAsFailed() {
        using var source = new AssetMaterialSource(Content(new()));

        Assert.False(source.TryGet(new(new AssetId(Guid.NewGuid()), SubAssetId.Main), out _));
        Assert.Equal(1, source.Failed);
    }

    /// <summary>"Never" and "not yet" are the same <see cref="AssetMaterialSource.TryGet" /> answer,
    /// and <see cref="AssetMaterialSource.Refused" /> is what separates them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The distinction is a mesh that draws against a mesh that never does.</b>
    ///         <c>IMaterialSource</c> is two-valued and <c>MeshExtractionSystem.Painted</c> reads its
    ///         false as "ask again next frame", so a reference this refused takes its geometry off
    ///         screen for the life of the process — silently, because every counter in the frame stays
    ///         healthy and the object is simply never added.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted against the <em>same</em> reference over the <em>same</em>
    ///         source: false while the bundle has not arrived, and still false once it has. A test that
    ///         only checked the missing reference would pass against a predicate that answered "yes"
    ///         to everything that was not already compiled, which is exactly the mistake that would
    ///         make a host substitute a fallback for a texture still on the wire.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AReferenceStillComingIsNotOneThisHasRefused() {
        Arriving arriving = null!;

        using var source = new AssetMaterialSource(Content(new(), local => arriving = new(local)));

        var missing = new AssetReference(new AssetId(Guid.NewGuid()), SubAssetId.Main);

        // Nothing has been asked about, so nothing has been given up on.
        Assert.False(source.Refused(Hero));
        Assert.False(source.Refused(missing));

        // Asked and unanswerable: the catalog has no address under that identity at all.
        Assert.False(source.TryGet(missing, out _));
        Assert.True(source.Refused(missing));

        // Asked and on its way: the same false from TryGet, and the opposite answer from this.
        Assert.False(source.TryGet(Hero, out _));
        Assert.False(source.Refused(Hero));

        arriving.Arrive();
        Settles(source, out _);

        Assert.False(source.Refused(Hero));
        Assert.True(source.Refused(missing));
    }

    /// <summary>
    ///     A material's textures are recorded as owed, and not waited for.
    /// </summary>
    /// <remarks>
    ///     <b>The decision this class makes that nothing else can.</b> A material is answered as soon as
    ///     it compiles, with its texture parameters unset, because holding it back would hold a whole
    ///     level's geometry off screen for its slowest texture — and the index a feature reads stays
    ///     zero, which is the table's fallback and a defined thing to sample.
    /// </remarks>
    [Fact]
    public void ATexturedMaterialIsAnsweredBeforeItsTexturesArrive() {
        using var source = new AssetMaterialSource(
            Content(
                new MaterialContent {
                    Features = [new TexturedMetalRoughnessFeature()],
                    Textures = [new("baseColorMap", new(new AssetId(Guid.NewGuid()), SubAssetId.Main))]
                }
            )
        );

        Settles(source, out _);

        Assert.Equal(1, source.Unpainted);
    }

    /// <summary>
    ///     A project that sets no <see cref="AssetMaterialSource.Permutations" /> still compiles the
    ///     shadow term, because a permutation nobody sets takes the shader's declared default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The claim this pins down is one this file used to make and that was never true.</b>
    ///         The remarks on <see cref="AssetMaterialSource.Permutations" /> said a null collection
    ///         compiled "with every permutation off", and therefore that a level rendering four
    ///         cascades drew with a shader that had no shadow term in it. Off is not what unset means:
    ///         <c>EffectKey.From</c> falls through to <c>PermutationKey.DefaultValue</c> for a key the
    ///         collection does not carry, and that default is the <c>.rvn</c>'s —
    ///         <c>ClusteredShading.rvn</c> declares <c>UseShadows: bool = true</c> and
    ///         <c>CascadeCount: int = 4</c>, and has since the file was written.
    ///     </para>
    ///     <para>
    ///         Asserted through the generated keys rather than by name, so a shader that renames a
    ///         permutation breaks this rather than silently passing on a key nothing selects by. The
    ///         key list is the same <c>UsedPermutationKeys</c> that <c>MaterialRenderFeature.KeysFor</c>
    ///         hands <c>EffectKey.From</c>, which is what makes this the variant a draw resolves to and
    ///         not a rehearsal of one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AProjectThatSetsNoPermutationsStillCompilesTheShadowTerm() {
        using var source = new AssetMaterialSource(
            Content(new MaterialContent { Shader = "ForwardPlus", Features = [new MetalRoughnessFeature()] })
        );

        // The state under test, and the one WorldRenderer.Mount leaves a project in.
        Assert.Null(source.Permutations);
        Settles(source, out var material);

        var key = EffectKey.From(
            material.ShaderName,
            material.Parameters,
            ForwardPlusKeys.UsedPermutationKeys,
            material.Composition
        );

        var values = key.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        Assert.Equal("true", values[ForwardPlusKeys.UseShadows.Name]);
        Assert.Equal("4", values[ForwardPlusKeys.CascadeCount.Name]);
    }

    /// <summary>What a null <see cref="AssetMaterialSource.Permutations" /> does cost.</summary>
    /// <remarks>
    ///     The other half of the correction above, and the reason the property is still worth setting:
    ///     the permutations whose declared default is <em>off</em> stay off, so a project with a
    ///     reflection probe or an irradiance field gets materials compiled not to read either. That is
    ///     a real difference and a small one — a missing specular reflection rather than a scene with
    ///     no shadows — and it is what a guard should be sized for.
    /// </remarks>
    [Fact]
    public void APermutationWhoseDefaultIsOffIsWhatANullCollectionCosts() {
        var permutations = new ParameterCollection();

        permutations.Set(ForwardPlusKeys.UseReflectionProbe, true);

        using var without = new AssetMaterialSource(Content(new() { Shader = "ForwardPlus" }));
        using var with = new AssetMaterialSource(Content(new() { Shader = "ForwardPlus" })) {
            Permutations = permutations
        };

        Settles(without, out var plain);
        Settles(with, out var told);

        Assert.False(plain.Parameters.Get(ForwardPlusKeys.UseReflectionProbe));
        Assert.True(told.Parameters.Get(ForwardPlusKeys.UseReflectionProbe));
    }

    /// <summary>
    ///     A material whose bundle has not arrived is counted as reading, and is waited for rather
    ///     than given up on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The measurement this exists to correct.</b>
    ///         <see cref="AssetMaterialSource.Reading" /> was landed with a note saying it is never
    ///         non-zero — probed with two hundred pool workers blocked, <c>TryGet</c> answered on the
    ///         first ask in 26 ms with the highest reading ever seen at zero — and a predicate that
    ///         is never true is worth less than the flake it replaced. That measurement was right and
    ///         its conclusion was about the fixture rather than about the source: nothing in
    ///         <c>AssetManager.LoadRootAsync</c> is a <see cref="Task.Run(Action)" />, so it yields
    ///         only where one of its awaits does, and over a <c>MemoryFileProvider</c> none of them
    ///         does.
    ///     </para>
    ///     <para>
    ///         <b>One of them does over a bundle that is not here yet</b>, which is
    ///         <c>MountFor</c>'s <c>IBundleSource.OpenAsync</c> — the call
    ///         <see cref="RemoteBundleSource" /> answers by downloading. So the counter is
    ///         load-bearing for a game that ships an expansion pack in a bundle of its own, and
    ///         deleting it would have been asserting that this source cannot starve, which is false
    ///         of every project whose content is not all local. Modelled here with a source that
    ///         answers when it is told to, because what matters is that the await does not complete
    ///         synchronously and not how far the bytes travelled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sixteen asks with the bundle held back, and the count still one.</b> A single
    ///         reading would be satisfied by a load that had not started; the point of the loop is
    ///         that the settle above would wait here for ever rather than giving up, which is exactly
    ///         what it should do while the work exists.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMaterialWhoseBundleHasNotArrivedIsCountedAsReading() {
        Arriving arriving = null!;

        using var source = new AssetMaterialSource(Content(new(), local => arriving = new(local)));

        // The ask is what starts the load, and the bundle it needs is not here.
        Assert.False(source.TryGet(Hero, out _));
        Assert.Equal(1, source.Reading);

        for (var attempt = 0; attempt < 16; attempt++) {
            Assert.False(source.TryGet(Hero, out _));
            Thread.Sleep(1);
        }

        Assert.Equal(1, source.Reading);
        Assert.Equal(0, source.Loaded);

        arriving.Arrive();

        Settles(source, out var material);

        Assert.Equal(0, source.Reading);
        Assert.Equal(1, source.Loaded);
        Assert.NotNull(material);
    }

    /// <summary>Asks until the load lands, or until nothing is left that could make it land.</summary>
    /// <param name="source">The source being asked, whose outstanding load decides when to give up.</param>
    /// <param name="material">The material it compiled.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No deadline, for the reason <c>AssetWaterSourceTests.Settles</c> now gives at
    ///         length.</b> The load is off the frame's thread, <c>build.sh Test</c> runs every test
    ///         project at once, and a work item queued into a saturated pool waits on .NET's thread
    ///         injection — about two threads a second — so the delay is a property of how many
    ///         workers the whole host has blocked. Thirty seconds was a guess about somebody else's
    ///         scheduler, and raising the number is the remedy that already failed once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The predicate is the handle's status, not "is there a material yet".</b>
    ///         <see cref="AssetMaterialSource.Reading" /> falls to zero when the document has
    ///         arrived, which is before <c>Compile</c> has run — so a document that arrives and
    ///         will not compile is given up on and reported, where waiting on "no material yet"
    ///         would wait on it for ever and turn a defect into a hang.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     A graph-authored material's textures reach the host's pairing, which nothing did before.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The failure is silent and draws.</b> <c>WorldRenderer.Paired</c> can name only the
    ///         hand-written features, because a graph's slots are data; unpaired, the index stays at
    ///         zero, zero is a valid slot holding the table's fallback, and the material samples a real
    ///         texture that is not its own. No device reports anything — <a
    ///         href="https://github.com/Rikarin/Vixen/issues/493">#493</a>.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted, because they are two names for different things: the key is
    ///         the shader's composed <c>uint</c>, under the path the compiler chose, and the value is
    ///         what the material calls its texture. A pairing with the two swapped is as silent as none.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AGraphAuthoredMaterialsTexturesReachThePairing() {
        using var materials = new MaterialRenderFeature();

        using var source = new AssetMaterialSource(
            Content(
                new MaterialContent {
                    Shader = "ForwardPlus",
                    Features = [
                        new GraphSurfaceFeature {
                            Shader = "BarkGraphSurface",
                            Maps = [new("bark", "albedoIndex")]
                        }
                    ]
                }
            )
        ) {
            Materials = materials
        };

        Settles(source, out _);

        var slot = ParameterKeys.New<uint>("ForwardPlus.CompositeSurface.BarkGraphSurface.albedoIndex");

        Assert.True(
            materials.TextureIndices.TryGetValue(slot, out var texture),
            "nothing paired the graph's slot, so its index stays zero and it samples the table's fallback"
        );

        Assert.Equal("bark", texture.Name);

        // And the compiler wrote the parameter the pairing names, which is what makes the key real
        // rather than a string this test and the source agree on.
        Assert.True(source.TryGet(Hero, out var material));
        Assert.True(material.Parameters.Has(slot));
    }

    /// <summary>And a host that gave it no feature is not an error, which is the non-bindless path.</summary>
    /// <remarks>
    ///     A project on GL, on WebGL2 or on MoltenVK below argument-buffer tier 2 has no table, so
    ///     <c>WorldRenderer</c> builds none and hands none here. Compiling has to go on working —
    ///     ADR-011's fork, and the same one <c>TexturedMetalRoughnessFeature</c> describes.
    /// </remarks>
    [Fact]
    public void AGraphMaterialCompilesWithNoPairingToAddTo() {
        using var source = new AssetMaterialSource(
            Content(
                new MaterialContent {
                    Shader = "ForwardPlus",
                    Features = [
                        new GraphSurfaceFeature {
                            Shader = "BarkGraphSurface",
                            Maps = [new("bark", "albedoIndex")]
                        }
                    ]
                }
            )
        );

        Settles(source, out var material);

        Assert.Equal("ForwardPlus", material.ShaderName);
    }

    static void Settles(AssetMaterialSource source, out Material material) {
        Material found = null!;

        Settling.Until(
            () => source.TryGet(Hero, out found!),
            () => found is not null,
            () => source.Reading > 0,
            "the material never compiled"
        );

        material = found;
    }

    /// <summary>A content manager holding one material at <see cref="Hero" />.</summary>
    static AssetManager Content(MaterialContent material) => Content(material, local => local);

    /// <summary>The same, with the bundle source it reads through wrapped.</summary>
    /// <param name="material">The document to write.</param>
    /// <param name="around">
    ///     What to put between the manager and the bundles — the seam a project fills with
    ///     <see cref="RoutedBundleSource" />, and the only place in a load that can fail to complete
    ///     synchronously over local content.
    /// </param>
    static AssetManager Content(MaterialContent material, Func<IBundleSource, IBundleSource> around) {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        var id = database.Write(material);
        var bundle = new BundleWriter();

        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new("hero", id, "Main", ContentProvider.Local, [], [], 0, Reference: Hero)],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, around(new LocalBundleSource(files, new("/bundles"))));
    }

    /// <summary>A bundle that is not here yet, and is here when it is told to be.</summary>
    /// <remarks>
    ///     ⚠ <b>The point is the incomplete await and not the delay.</b>
    ///     <c>AssetManager.LoadRootAsync</c> is a plain <c>async Task</c>, so it runs on the caller's
    ///     thread until one of its awaits does not complete synchronously; over local content in
    ///     memory none of them does, and the handle is <c>Loaded</c> before <c>LoadAsync</c> has
    ///     returned. <see cref="RemoteBundleSource" /> is the shipped source for which this one does
    ///     not, and this stands in for it without a socket.
    /// </remarks>
    sealed class Arriving(IBundleSource inner) : IBundleSource {
        readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the bundle land.</summary>
        public void Arrive() => arrived.TrySetResult();

        /// <inheritdoc />
        public bool IsAvailable(CatalogBundle bundle) => inner.IsAvailable(bundle);

        /// <inheritdoc />
        public async ValueTask<IOdbBackend> OpenAsync(
            CatalogBundle bundle,
            CancellationToken cancellationToken = default
        ) {
            await arrived.Task.ConfigureAwait(false);

            return await inner.OpenAsync(bundle, cancellationToken).ConfigureAwait(false);
        }
    }
}
