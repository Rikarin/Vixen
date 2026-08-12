// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Ecs;
using Vixen.Engine.Renderer;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     A water component's references become a curve and a sea state, through the content manager a
///     game has — [docs/plan/35 § D6].
/// </summary>
/// <remarks>
///     <para>
///         <b>Over a real <see cref="AssetManager" /> and a real bundle</b>, on
///         <c>AssetTerrainSourceTests</c>' terms, and for the same reason: the interesting part is the
///         join. The chunks are written the way the build writes them — each as its serialized record
///         under its own type id — so a change to either content format breaks this rather than being
///         discovered in a game as dry ground.
///     </para>
///     <para>
///         ⚠ <b>The spline half exists because a <c>.vxspline</c> could not be read at all.</b>
///         <c>SplineAsset.Points</c> was a getter-only <c>IReadOnlyList</c>, and both serialisers skip
///         a member they cannot write to — so every curve round-tripped to a name, a closed flag and
///         no points. Nothing caught it, because everything downstream asks <c>CanBuild</c> and draws
///         nothing when the answer is no: a road that never appeared and a lake that never appeared,
///         with no error anywhere. This is the test that holds the fix.
///     </para>
/// </remarks>
public sealed class AssetWaterSourceTests {
    const string SplineAddress = "Assets/Water/Lake.vxspline";
    const string WavesAddress = "Assets/Water/NorthSea.vxwaves";

    /// <summary>The join this file exists for: a spline reference in, the authored curve out.</summary>
    [Fact]
    public void ASplineReferenceBecomesTheCurveTheBuildWrote() {
        var source = new AssetWaterSource(Content(Lake(), null));

        Assert.True(Settles(() => source.SplineFor(SplineAddress, Matrix4x4.Identity) is not null));

        var curve = source.SplineFor(SplineAddress, Matrix4x4.Identity)!;

        Assert.True(curve.IsClosed);
        Assert.Equal(4, curve.Points.Length);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>
    ///     ⚠ The curve arrives in world space, and a tangent takes the rotation without the
    ///     translation.
    /// </summary>
    /// <remarks>
    ///     The fold rasterises a body where its spline <em>is</em>, so a source answering in the
    ///     asset's own space would put every lake at the origin however the entity was placed. And a
    ///     tangent transformed as a position points at wherever the entity happens to be, which folds
    ///     the curve into a knot — invisible in the numbers and unmistakable in a picture, which is
    ///     the wrong order to find it in.
    /// </remarks>
    [Fact]
    public void ACurveIsBuiltAtThePlacementItIsAskedFor() {
        var source = new AssetWaterSource(Content(Lake(), null));
        var moved = Matrix4x4.FromTranslation(new(100f, 5f, -40f));

        Assert.True(Settles(() => source.SplineFor(SplineAddress, moved) is not null));

        var placed = source.SplineFor(SplineAddress, moved)!;
        var origin = source.SplineFor(SplineAddress, Matrix4x4.Identity)!;

        Assert.Equal(100f, placed.Points[0].Position.X - origin.Points[0].Position.X, 3);
        Assert.Equal(5f, placed.Points[0].Position.Y - origin.Points[0].Position.Y, 3);

        // The tangents moved with the rotation and not with the offset — identical, because this
        // placement has none.
        Assert.Equal(origin.Points[0].TangentOut.X, placed.Points[0].TangentOut.X, 3);
        Assert.Equal(origin.Points[0].TangentOut.Z, placed.Points[0].TangentOut.Z, 3);
    }

    /// <summary>And the same curve back at the same placement, rather than a rebuild per frame.</summary>
    /// <remarks>
    ///     A <see cref="Spline" /> precomputes an arc-length table in its constructor, and the fold
    ///     asks once a frame per body. Rebuilding every frame is the cost <c>SplineAsset</c> exists as
    ///     a separate type to avoid, and it is invisible in a picture.
    /// </remarks>
    [Fact]
    public void ACurveIsBuiltOncePerPlacement() {
        var source = new AssetWaterSource(Content(Lake(), null));

        Assert.True(Settles(() => source.SplineFor(SplineAddress, Matrix4x4.Identity) is not null));

        var first = source.SplineFor(SplineAddress, Matrix4x4.Identity);

        Assert.Same(first, source.SplineFor(SplineAddress, Matrix4x4.Identity));
        Assert.NotSame(first, source.SplineFor(SplineAddress, Matrix4x4.FromTranslation(new(1f, 0f, 0f))));
    }

    /// <summary>The sea-state join: a .vxwaves reference in, the authored spectrum out.</summary>
    [Fact]
    public void AWavesReferenceBecomesTheSpectrumTheBuildWrote() {
        var north = WaterWaveSpectrum.Default with { WindSpeed = 18f, Seed = 7u, Count = WaterWaveCount.ThirtyTwo };
        var source = new AssetWaterSource(Content(Lake(), north));

        Assert.True(Settles(() => source.SpectrumFor(WavesAddress) is not null));

        var loaded = source.SpectrumFor(WavesAddress)!.Value;

        Assert.Equal(18f, loaded.WindSpeed);
        Assert.Equal(7u, loaded.Seed);
        Assert.Equal(WaterWaveCount.ThirtyTwo, loaded.Count);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>A reference this build shipped nothing for is counted, not thrown for.</summary>
    [Fact]
    public void AReferenceNothingShippedIsCountedAsFailed() {
        var source = new AssetWaterSource(Content(Lake(), null));

        // The reads start on the first ask and their failures land with the tasks, so keep asking —
        // which is what a frame does — until both are settled rather than asserting a race.
        Assert.True(
            Settles(
                () => source.SplineFor("Assets/Water/Gone.vxspline", Matrix4x4.Identity) is null
                    && source.SpectrumFor("Assets/Water/Gone.vxwaves") is null
                    && source.Failed == 2
            )
        );
    }

    /// <summary>
    ///     ⚠ A lake named in a scene appears in a running game, over the real asynchronous source.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The end-to-end half of the retry, and the reason this belongs beside the source
    ///         rather than beside the fold.</b> Every spline source in
    ///         <c>WaterZoneSystemTests</c> is a literal that answers on the first ask; this one starts
    ///         a <see cref="Task" /> and polls it, which is what a game has and is what made the fold's
    ///         cached failure permanent — the first fold's ask <em>is</em> the ask that starts the
    ///         read, so it cannot have landed, and a fold that remembered that answer never asked
    ///         again.
    ///     </para>
    ///     <para>
    ///         The zone and the body are the ones a scene carries, so what is being asserted is the
    ///         whole path: a component naming a <c>.vxspline</c>, a source that has not got it yet, and
    ///         a field with water in it a few folds later.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALakeWhoseSplineIsStillLoadingAppearsOnceItLands() {
        using var world = new World();

        var source = new AssetWaterSource(Content(Lake(), null));
        var view = new RenderView("Camera");
        var system = new WaterZoneSystem(view) { Splines = source, Ground = new FlatWaterGround(-10f) };

        var zone = world.Create();

        world.Add(zone, WaterZoneComponent.Default);
        world.Add(zone, new WorldTransform { Value = Matrix4x4.Identity });

        var body = world.Create();

        world.Add(body, WaterBodyComponent.Default with { Spline = SplineAddress, SurfaceHeight = 2f });
        world.Add(body, new WorldTransform { Value = Matrix4x4.Identity });

        // The first fold is the one that starts the read, so it is answered with null — which used to
        // be the answer for the rest of the run.
        system.Fold(world);

        Assert.Equal(0, system.BodyCount);
        Assert.Equal(1, system.UnresolvedBodies);

        Assert.True(
            Settles(
                () => {
                    system.Fold(world);

                    return system.BodyCount == 1;
                }
            )
        );

        Assert.Equal(0, system.UnresolvedBodies);
        Assert.Equal(0, source.Failed);
        Assert.True(system.States[zone].Field!.Sample(Vector2.Zero).Coverage > 0.9f);
    }

    /// <summary>Asks until the load lands, which is what the fold does by asking next frame.</summary>
    static bool Settles(Func<bool> landed) {
        for (var attempt = 0; attempt < 200; attempt++) {
            if (landed()) {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    /// <summary>A closed square, which is the smallest thing a lake can be.</summary>
    static SplineAsset Lake() =>
        SplineAsset.Through(
            "Lake",
            [new(-20f, 2f, -20f), new(20f, 2f, -20f), new(20f, 2f, 20f), new(-20f, 2f, 20f)],
            closed: true
        );

    /// <summary>A content manager holding the chunks, written the way the build writes them.</summary>
    static AssetManager Content(SplineAsset spline, WaterWaveSpectrum? waves) {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        // TerrainAssetImporter's exact spelling: the record's own type id over its serialized bytes,
        // which is what lets the source hand the payload to the serializer.
        var splineId = database.WriteRaw(ContentHash.TypeId(typeof(SplineAsset)), [], Serializer.ToBytes(spline));

        var entries = new List<CatalogEntry> {
            new(SplineAddress, splineId, "Main", ContentProvider.Local, [], [], 0)
        };

        if (waves is { } spectrum) {
            var asset = new WaterWavesAsset { Name = "NorthSea", Spectrum = spectrum };
            var wavesId = database.WriteRaw(ContentHash.TypeId(typeof(WaterWavesAsset)), [], Serializer.ToBytes(asset));

            entries.Add(new(WavesAddress, wavesId, "Main", ContentProvider.Local, [], [], 0));
        }

        var bundle = new BundleWriter();

        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [.. entries],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }
}
