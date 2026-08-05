// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Scenes;
using Vixen.Rendering.Water;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>A zone, a body and a boat, all the way from a <c>.vxscene</c> to entities in a world.</summary>
/// <remarks>
///     <para>
///         <b>The fixture that did not exist, which is why a scene could not carry water and the suite
///         was green anyway.</b> <c>Vixen.Rendering.Water</c> and <c>Vixen.Water.Physics</c> ran the
///         serialization generator and not <c>Vixen.Engine.Generators</c>, so all four component types
///         had a serializer, a contract name and an inspector description — everything a unit test
///         asks about — and no <c>[ModuleInitializer]</c> declaring them to
///         <see cref="SceneComponentRegistry" />. A missing generator reference is silent at build
///         time by construction: there is no code to fail to compile. It surfaces the first time
///         something names the component in a scene, and nothing did.
///     </para>
///     <para>
///         ⚠ <b>The constructor touches all three assemblies on purpose</b>, on
///         <c>SceneRenderComponentTests</c>' and <c>PhysicsSceneComponentTests</c>' terms: a module
///         initializer runs when its assembly is loaded, so a test that only ever named these types in
///         a YAML string would be asserting against a registry that had never heard of them — and
///         would fail for the wrong reason, which is worse than not existing.
///     </para>
/// </remarks>
public sealed class SceneWaterComponentTests {
    public SceneWaterComponentTests() {
        _ = WaterZoneComponent.Default;
        _ = BuoyancyBody.Default;
    }

    /// <remarks>
    ///     ⚠ <b>257 over 512 metres and not 256.</b> The samples include both edges, so a power of two
    ///     plus one is two metres exactly — <c>WaterZone.Validate</c> refuses the other combination,
    ///     and a fixture that authored an invalid zone would be asserting the compiler accepts nonsense.
    /// </remarks>
    const string Harbour = """
                           version: 1
                           name: Harbour
                           roots:
                             - id: 0000000000000000000000000000aaaa
                               name: Bay
                               position: 0 0 0
                               components:
                                 - !WaterZoneComponent
                                   extent: 512
                                   resolution: 257
                                   precision: Full
                                   scrollThreshold: 0.25
                                   attenuationDepth: 6.5
                                   waveAsset: seas/northsea
                             - id: 0000000000000000000000000000bbbb
                               name: Creek
                               components:
                                 - !WaterBodyComponent
                                   kind: River
                                   spline: splines/creek
                                   surfaceHeight: 3.5
                                   priority: 2
                                   halfWidth: 6
                                   depth: 2.25
                                   velocity: 1.5
                             - id: 0000000000000000000000000000cccc
                               name: Dinghy
                               components:
                                 - !BuoyancyBody
                                   coefficient: 1.2
                                   damping: 0.4
                                   quadraticDamping: 0.05
                                   maximumForce: 12000
                                   flowDrag: 0.8
                                 - !BuoyancyState
                                   wet: 0
                                   total: 4
                           """;

    /// <summary>
    ///     The assertion the whole path rests on: the compiler resolved every tag it was given.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Against the unfixed tree this is what failed, and it failed four times over</b> — one
    ///     <c>ImportSeverity.Error</c> per component, from <c>SceneCompiler</c>'s "nothing declared as
    ///     a scene component" path. The per-entity assertions below would then have failed too, but
    ///     for a reason a reader has to work backwards from; this one names the defect.
    /// </remarks>
    [Fact]
    public void ASceneCarryingWaterCompilesWithNoUnknownComponents() {
        var content = Compile();

        Assert.Equal(3, content.Count);
    }

    [Fact]
    public void AnAuthoredZoneLandsOnItsEntity() {
        using var world = new World();
        var created = Instantiate(world);

        Assert.True(world.Has<WaterZoneComponent>(created[0]));

        var zone = world.Read<WaterZoneComponent>(created[0]);

        Assert.Equal(512f, zone.Extent);
        Assert.Equal(257, zone.Resolution);
        Assert.Equal(WaterInfoPrecision.Full, zone.Precision);
        Assert.Equal(6.5f, zone.AttenuationDepth, 5);
        Assert.Equal("seas/northsea", zone.WaveAsset);

        // The zone the renderer actually reads is derived, not stored — so this is the assertion that
        // what survived the file is enough to build one, which is the only form of "it arrived" that
        // matters downstream.
        Assert.Equal(2f, zone.Zone.MetresPerTexel, 5);
    }

    [Fact]
    public void AnAuthoredBodyLandsOnItsEntityWithItsSplineName() {
        using var world = new World();
        var created = Instantiate(world);

        var body = world.Read<WaterBodyComponent>(created[1]);

        Assert.Equal(WaterBodyKind.River, body.Kind);

        // ⚠ A name and not a handle, and the string is the half a scene cannot recover from anything
        // else — an unresolved spline counts into WaterZoneSystem.UnresolvedBodies rather than failing,
        // so a name lost in the file is a river that never appears and never complains.
        Assert.Equal("splines/creek", body.Spline);
        Assert.Equal(3.5f, body.SurfaceHeight, 5);
        Assert.Equal(2, body.Priority);
        Assert.Equal(6f, body.HalfWidth, 5);
        Assert.Equal(1.5f, body.Velocity, 5);
    }

    /// <remarks>
    ///     The second assembly, which had the same omission and is reached through a different
    ///     dependency chain — <c>Vixen.Water.Physics</c> takes <c>Vixen.Engine</c> transitively through
    ///     <c>Vixen.Physics</c> and never names it. That is what made the reference easy to leave out
    ///     twice.
    /// </remarks>
    [Fact]
    public void TheBuoyancyComponentsLandOnTheirEntityToo() {
        using var world = new World();
        var created = Instantiate(world);

        var body = world.Read<BuoyancyBody>(created[2]);

        Assert.Equal(1.2f, body.Coefficient, 5);
        Assert.Equal(0.4f, body.Damping, 5);
        Assert.Equal(12000f, body.MaximumForce, 5);

        Assert.Equal(4, world.Read<BuoyancyState>(created[2]).Total);
    }

    /// <summary>Each of the four, by type, without a scene in the way.</summary>
    /// <remarks>
    ///     ⚠ <b>Cheaper to read than a compile failure when this regresses.</b> A scene that stops
    ///     compiling says "'Bay' carries a WaterZoneComponent, which nothing declared"; this says which
    ///     of the four types lost its declaration, which is the same information one step closer to the
    ///     csproj that has to change.
    /// </remarks>
    [Theory]
    [InlineData(typeof(WaterZoneComponent))]
    [InlineData(typeof(WaterBodyComponent))]
    [InlineData(typeof(BuoyancyBody))]
    [InlineData(typeof(BuoyancyState))]
    public void EveryWaterComponentIsDeclaredByTheAssemblyThatOwnsIt(Type component) {
        Assert.True(SceneComponentRegistry.TryGet(component, out var binder));
        Assert.Equal(component, binder.ComponentType);
    }

    /// <remarks>
    ///     ⚠ <b>A zeroed zone is not a small zone, it is an invalid one</b> — <c>WaterZone.Validate</c>
    ///     refuses an extent of zero and a resolution that is not a power of two plus one. So a zone
    ///     added from the inspector had to arrive carrying its own default or arrive broken, which is
    ///     <c>CharacterMovement</c>'s case from doc 35's side of the boundary.
    /// </remarks>
    [Theory]
    [InlineData(typeof(WaterZoneComponent))]
    [InlineData(typeof(WaterBodyComponent))]
    [InlineData(typeof(BuoyancyBody))]
    public void AComponentWithAUsableDefaultCarriesItAcrossTheBoundary(Type component) {
        Assert.True(SceneComponentRegistry.TryGet(component, out var binder));
        Assert.True(binder.HasDefault);
        Assert.NotEqual(Activator.CreateInstance(component), binder.CreateDefault());
    }

    static Entity[] Instantiate(World world) {
        var created = new Entity[3];

        Compile().Instantiate(world, created);

        return created;
    }

    static SceneContent Compile() {
        var problems = new List<string>();

        var content = SceneCompiler.Compile(
            SceneFile.FromYaml(Harbour),
            (severity, message) => {
                if (severity == ImportSeverity.Error) {
                    problems.Add(message);
                }
            }
        );

        Assert.Empty(problems);
        Assert.NotNull(content);

        return content;
    }
}
