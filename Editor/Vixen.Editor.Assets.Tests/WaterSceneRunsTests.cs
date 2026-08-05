// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>A scene with water in it, run — a lake, a sea state, and a crate that floats on both.</summary>
/// <remarks>
///     <para>
///         <b>The fixture the whole stack never had.</b> <c>SceneWaterComponentTests</c> proved a
///         <c>.vxscene</c> can carry the four components; <c>BuoyancySystemTests</c> proved the solver
///         is right against an analytic displacement. Between them sat everything that is only true
///         when the pieces are assembled: no scene in this repository named a water component, no
///         <c>.vxwaves</c> existed anywhere in it, and no test had ever asked <c>WaterZoneSystem</c>
///         — the thing a game actually holds — where the water was. Every one of those is silent when
///         it is wrong.
///     </para>
///     <para>
///         ⚠ <b><c>WaterZoneSystem</c> is the <c>IWaterSurface</c> here, and that is the point of the
///         fixture.</b> Every buoyancy test before this handed the solver a lake built by hand in the
///         test file. This hands it the fold — a component, a spline resolved by name, a field
///         rasterised over ground, a query cached per zone — which is the object a running game
///         passes and the one where a wiring mistake lives.
///     </para>
///     <para>
///         ⚠ <b>The collider and the rigid body are authored in code and the water is not.</b> A
///         <c>Collider</c> carries a <see cref="ShapeId" />, which names a shape in a physics scene's
///         catalogue rather than anything a file can hold on its own — so what the scene proves is
///         that the *water* half survives a file, which is the half that had never been tried.
///     </para>
///     <para>
///         ⚠ <b>The clock is advanced by hand, and forgetting to is the trap.</b> There is one water
///         clock — doc 35 § D2 — and in a game <c>WaterClockSystem</c> is its only writer, in
///         <c>EarlyUpdate</c>, before the solver reads it. A host that forgets it gets a still sea
///         that looks entirely convincing; this test makes the swell the thing being measured, so
///         forgetting would fail rather than flatten.
///     </para>
/// </remarks>
public sealed class WaterSceneRunsTests {
    const float Step = 1f / 60f;

    /// <summary>Where the committed sea state is, relative to the test's output directory.</summary>
    /// <remarks>
    ///     ⚠ <b>A real file rather than an inline spectrum, because there were zero of them.</b> The
    ///     <c>.vxwaves</c> importer is tested, the runtime source that reads one is tested, the editor
    ///     has a Create ▸ entry that writes one — and no file of the kind existed in the tree, so
    ///     nothing had ever round-tripped the format the three agree on.
    /// </remarks>
    const string WavesFile = "Assets/Water/Pond.vxwaves";

    /// <summary>A pond, a sea state it names, and a dinghy floating in the middle of it.</summary>
    const string Pond = """
                        version: 1
                        name: Pond
                        roots:
                          - id: 0000000000000000000000000000a1a1
                            name: Bay
                            position: 0 0 0
                            components:
                              - !WaterZoneComponent
                                extent: 128
                                resolution: 129
                                precision: Full
                                scrollThreshold: 0.25
                                attenuationDepth: 4
                                waveAsset: water/pond
                          - id: 0000000000000000000000000000b2b2
                            name: Pond
                            position: 0 0 0
                            components:
                              - !WaterBodyComponent
                                kind: Lake
                                spline: water/pond
                                surfaceHeight: 0
                                priority: 1
                                shoreFalloff: 2
                                bedRamp: 4
                                depth: 12
                          - id: 0000000000000000000000000000c3c3
                            name: Dinghy
                            position: 0 6 0
                            components:
                              - !BuoyancyBody
                                # ⚠ Four, because one sphere cannot pitch or roll — and authored in
                                # the file, because "a body with no pontoons floats nowhere and is
                                # not an error" is exactly the shape of defect this fixture exists
                                # to catch: an empty list is what a component nobody filled in holds.
                                pontoons:
                                  - offset: -0.7 -0.2 -0.9
                                    radius: 0.45
                                  - offset: 0.7 -0.2 -0.9
                                    radius: 0.45
                                  - offset: 0.7 -0.2 0.9
                                    radius: 0.45
                                  - offset: -0.7 -0.2 0.9
                                    radius: 0.45
                                coefficient: 1.4
                                damping: 3
                                quadraticDamping: 0.4
                                flowDrag: 2
                              - !BuoyancyState
                                wet: 0
                        """;

    public WaterSceneRunsTests() {
        // The module initializers, on `SceneWaterComponentTests`' terms: a type named only inside a
        // YAML string is a type whose declaring assembly may never have been loaded.
        _ = WaterZoneComponent.Default;
        _ = BuoyancyBody.Default;
    }

    /// <summary>
    ///     The whole path: a scene file, a fold, a physics step, and a crate at the waterline.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not "the crate is above zero".</b> A crate resting on nothing at all is above zero,
    ///     and so is one the solver pushed to the moon. What is asserted is that it is where the
    ///     surface the *fold* reports puts it, to within the swell — which is a number this test never
    ///     writes down and the fold has to produce.
    /// </remarks>
    [Fact]
    public void ACrateAuthoredInASceneFloatsOnTheWaterAuthoredBesideIt() {
        using var world = new World();

        var created = Instantiate(world);
        var dinghy = created[2];

        using var physics = new PhysicsScene(world);

        // The rigid body, which is what a scene cannot yet carry on its own — see the class remarks.
        world.Add(dinghy, Collider.Of(physics.Shapes.Sphere(0.75f)));
        world.Add(dinghy, RigidBody.Dynamic() with { Mass = 400f, AllowSleeping = false });

        var zones = Fold(world);
        var buoyancy = new BuoyancySystem(physics, zones);

        for (var index = 0; index < 900; index++) {
            // ⚠ Where `WaterClockSystem` would be, in `EarlyUpdate` — before anything reads it.
            zones.WaterTime = index * Step;

            physics.Synchronize(Step);
            buoyancy.Step(world);
            physics.Step(Step);
            physics.Writeback();
        }

        var state = world.Read<BuoyancyState>(dinghy);
        var settled = world.Read<LocalTransform>(dinghy).Position;

        Assert.True(state.IsFloating, "the crate authored in the scene never touched the water");

        var query = zones.QueryAt(new(settled.X, settled.Z));

        Assert.NotNull(query);

        // Within a hull radius of the surface the fold reports: the crate is *at* the waterline
        // rather than sunk to the bed twelve metres down or hovering above the swell.
        var surface = query.Height(new(settled.X, settled.Z), zones.WaterTime);

        Assert.InRange(settled.Y, surface - 0.75f, surface + 0.75f);
    }

    /// <summary>The zone's <c>waveAsset</c> resolves to the committed file, and the sea is not flat.</summary>
    /// <remarks>
    ///     ⚠ <b><c>UnresolvedWaves</c> is asserted to be zero, and it is the only evidence there
    ///     is.</b> A zone whose <c>.vxwaves</c> did not load falls back to its inline spectrum and
    ///     keeps drawing perfectly convincing water — the wrong sea, which on a client is a boat that
    ///     rides differently from the one on the server.
    /// </remarks>
    [Fact]
    public void TheZonesSeaStateComesOffDiskAndTheSurfaceMoves() {
        using var world = new World();

        Instantiate(world);

        var zones = Fold(world);

        Assert.Equal(1, zones.ZoneCount);
        Assert.Equal(1, zones.BodyCount);
        Assert.Equal(0, zones.ZonelessBodies);
        Assert.Equal(0, zones.UnresolvedBodies);
        Assert.Equal(0, zones.UnresolvedWaves);

        var query = zones.QueryAt(Vector2.Zero);

        Assert.NotNull(query);
        Assert.True(query.MaximumAmplitude > 0f, "the sea state off disk sums to a dead flat sea");

        // Two times, one surface: the swell has to actually move, which is the half a still clock
        // hides. `WaterClockSystem` is the one writer of that number in a game.
        Assert.NotEqual(query.Height(Vector2.Zero, 0f), query.Height(Vector2.Zero, 1.7f), 4);
    }

    /// <summary>The zones, folded once, with the two names in the file resolved.</summary>
    static WaterZoneSystem Fold(World world) {
        // The transforms first: the fold rasterises a body where `WorldTransform` says it is, and a
        // scene that has just been instantiated has only written the local ones.
        new TransformSystem().Resolve(world);

        var zones = new WaterZoneSystem(new RenderView("test")) {
            Splines = new PondSpline(),
            Waves = new FileWaves(),

            // A basin: the bed is well below the surface everywhere, so the shoreline is the body's
            // own falloff rather than the ground rising to meet it.
            Ground = new FlatWaterGround(-12f)
        };

        zones.Fold(world);

        return zones;
    }

    static Entity[] Instantiate(World world) {
        var problems = new List<string>();

        var content = SceneCompiler.Compile(
            SceneFile.FromYaml(Pond),
            (severity, message) => {
                if (severity == ImportSeverity.Error) {
                    problems.Add(message);
                }
            }
        );

        Assert.Empty(problems);
        Assert.NotNull(content);

        var created = new Entity[3];

        content.Instantiate(world, created);

        return created;
    }

    /// <summary>The one curve the scene names, which a project would keep as a <c>.vxspline</c>.</summary>
    sealed class PondSpline : IWaterSplineSource {
        public Spline? SplineFor(string name, in Matrix4x4 placement) {
            if (!string.Equals(name, "water/pond", StringComparison.Ordinal)) {
                return null;
            }

            var transform = placement;

            Vector3[] ring = [
                new(-24f, 0f, -24f), new(24f, 0f, -24f), new(24f, 0f, 24f), new(-24f, 0f, 24f)
            ];

            for (var index = 0; index < ring.Length; index++) {
                ring[index] = Matrix4x4.TransformPosition(ring[index], transform);
            }

            return new Spline(Spline.SmoothTangents(ring, closed: true, tension: 1f), closed: true);
        }
    }

    /// <summary>The sea state, read off the committed <c>.vxwaves</c> rather than written here.</summary>
    sealed class FileWaves : IWaterWaveSource {
        public WaterWaveSpectrum? SpectrumFor(string name) {
            if (!string.Equals(name, "water/pond", StringComparison.Ordinal)) {
                return null;
            }

            var path = Path.Combine(AppContext.BaseDirectory, WavesFile);

            Assert.True(File.Exists(path), FormattableString.Invariant($"{WavesFile} was not copied beside the test"));

            var asset = YamlSerializer.Parse<WaterWavesAsset>(File.ReadAllText(path));

            Assert.NotNull(asset);
            Assert.Null(asset.Validate());

            return asset.Spectrum;
        }
    }
}
