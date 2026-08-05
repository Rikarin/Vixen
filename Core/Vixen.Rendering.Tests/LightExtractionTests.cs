// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The bridge from what a scene authored to what the lighting feature reads.</summary>
/// <remarks>
///     A light's position and orientation are the transform's and nothing else's, so most of what can
///     go wrong here is an axis: a spot that renders down a different vector from the cone the gizmo
///     drew is the failure this file exists to catch.
/// </remarks>
public sealed class LightExtractionTests {
    [Fact]
    public void ADirectionalLightPointsAlongTheEntitysForward() {
        // A quarter turn about +Y takes -Z to -X.
        var matrix = Matrix4x4.FromQuaternion(
            Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 2f)
        );

        var light = LightExtractionSystem.Convert(Lights.Default(LightKind.Directional), matrix);

        Assert.Equal(LightKind.Directional, light.Kind);
        Assert.Equal(-1f, light.Direction.X, 5);
        Assert.Equal(0f, light.Direction.Y, 5);
        Assert.Equal(0f, light.Direction.Z, 5);
    }

    /// <remarks>
    ///     The sun does not fall off, so a directional light reads no position — and a scene that moved
    ///     one would otherwise look like it had done something.
    /// </remarks>
    [Fact]
    public void ADirectionalLightIgnoresWhereItIs() {
        var matrix = Matrix4x4.FromTranslation(new Vector3(9f, 9f, 9f));

        var light = LightExtractionSystem.Convert(Lights.Default(LightKind.Directional), matrix);

        Assert.Equal(Vector3.Zero, light.Position);
    }

    [Fact]
    public void APointLightTakesItsPositionAndNotItsOrientation() {
        var matrix = Matrix4x4.FromQuaternion(
                Quaternion.FromAxisAngle(Vector3.UnitX, 0.7f)
            )
            * Matrix4x4.FromTranslation(new Vector3(1f, 2f, 3f));

        var light = LightExtractionSystem.Convert(Lights.Default(LightKind.Point), matrix);

        Assert.Equal(LightKind.Point, light.Kind);
        Assert.Equal(new Vector3(1f, 2f, 3f), light.Position);
        Assert.Equal(Vector3.Zero, light.Direction);
    }

    /// <summary>
    ///     ⚠ A punctual light's authored radius survives extraction — a point with a radius is a
    ///     sphere light.
    /// </summary>
    /// <remarks>
    ///     The factories describe punctual geometry and take no radius, so <c>Convert</c> has to carry
    ///     it across itself — and when it did not, the field the shader widens the specular lobe by
    ///     (<c>radius / distance</c> in <c>Lighting.Resolve</c>) reached the GPU as zero. Sample 13's
    ///     lanterns author 0.12–0.16 m and showed the failure's shape exactly: a bright speck in every
    ///     metal reflection and no visible glow on anything rough.
    /// </remarks>
    [Theory]
    [InlineData(LightKind.Point)]
    [InlineData(LightKind.Spot)]
    public void APunctualLightKeepsItsAuthoredRadius(LightKind kind) {
        var authored = Lights.Default(kind) with { Radius = 0.14f };

        var light = LightExtractionSystem.Convert(authored, Matrix4x4.Identity);

        Assert.Equal(0.14f, light.Radius);
        Assert.Equal(0.14f, light.ToGpu().Radius);
    }

    [Fact]
    public void ASpotLightCarriesBothItsPositionAndItsAim() {
        var matrix = Matrix4x4.FromQuaternion(
                Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI)
            )
            * Matrix4x4.FromTranslation(new Vector3(0f, 5f, 0f));

        var authored = Lights.Default(LightKind.Spot);
        var light = LightExtractionSystem.Convert(authored, matrix);

        Assert.Equal(LightKind.Spot, light.Kind);
        Assert.Equal(new Vector3(0f, 5f, 0f), light.Position);

        // A half turn about +Y takes -Z to +Z.
        Assert.Equal(1f, light.Direction.Z, 5);
        Assert.Equal(authored.InnerAngle, light.InnerAngle);
        Assert.Equal(authored.OuterAngle, light.OuterAngle);
    }

    /// <remarks>
    ///     ⚠ The authored half-length is the light's length, and the transform supplies only which way
    ///     it runs. A tube that grew with the gizmo's scale would make "how long is this light" a
    ///     question with two answers.
    /// </remarks>
    [Fact]
    public void ATubeRunsAlongItsOwnXAndKeepsItsAuthoredLength() {
        var matrix = Matrix4x4.FromUniformScale(4f)
            * Matrix4x4.FromQuaternion(Quaternion.FromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        var authored = Lights.Default(LightKind.Tube);
        var light = LightExtractionSystem.Convert(authored, matrix);

        Assert.Equal(LightKind.Tube, light.Kind);
        Assert.Equal(authored.HalfLength, light.HalfLength);

        // A quarter turn about +Z takes +X to +Y, and the scale must not survive the normalisation.
        Assert.Equal(0f, light.Tangent.X, 5);
        Assert.Equal(1f, light.Tangent.Y, 5);
        Assert.Equal(1f, light.Tangent.Length(), 5);
    }

    [Fact]
    public void ARectEmitsAlongForwardWithItsWidthAcrossX() {
        var authored = Lights.Default(LightKind.Rect);

        var light = LightExtractionSystem.Convert(authored, Matrix4x4.Identity);

        Assert.Equal(LightKind.Rect, light.Kind);
        Assert.Equal(-1f, light.Direction.Z, 5);
        Assert.Equal(1f, light.Tangent.X, 5);
        Assert.Equal(authored.HalfLength, light.HalfLength);
        Assert.Equal(authored.Radius, light.Radius);
    }

    /// <remarks>
    ///     A scene written by a newer editor that knows a sixth kind should still light something. The
    ///     position, colour and range all mean the same thing whatever the kind is, so it lights as a
    ///     point rather than not at all — the same tolerance <c>Lights.TryParse</c> applies on read.
    /// </remarks>
    [Fact]
    public void AKindThisBuildDoesNotKnowLightsAsAPoint() {
        var authored = Lights.Default(LightKind.Point);
        authored.Kind = (LightKind)99;

        var light = LightExtractionSystem.Convert(authored, Matrix4x4.FromTranslation(Vector3.UnitY));

        Assert.Equal(LightKind.Point, light.Kind);
        Assert.Equal(Vector3.UnitY, light.Position);
    }

    [Fact]
    public void ColourAndIntensityComeThroughUntouched() {
        var authored = Lights.Default(LightKind.Point);
        authored.Colour = new Color3(0.25f, 0.5f, 0.75f);
        authored.Intensity = 3f;

        var light = LightExtractionSystem.Convert(authored, Matrix4x4.Identity);

        Assert.Equal(authored.Colour, light.Colour);
        Assert.Equal(3f, light.Intensity);
        Assert.Equal(new Vector3(0.75f, 1.5f, 2.25f), light.Radiance);
    }

    // ------------------------------------------------------------------ over a world

    [Fact]
    public void EveryLitEntityReachesTheFeature() {
        using var world = new World();
        var feature = new ForwardLightingRenderFeature();
        var system = new LightExtractionSystem(feature);

        Lit(world, LightKind.Point, new Vector3(1f, 0f, 0f));
        Lit(world, LightKind.Point, new Vector3(2f, 0f, 0f));
        Lit(world, LightKind.Directional, Vector3.Zero);

        system.Extract(world);

        Assert.Equal(3, system.LightCount);
        Assert.Equal(3, feature.Lights.Count);
    }

    /// <remarks>
    ///     The whole route rather than <c>Convert</c> alone: a lamp authored in a world, extracted,
    ///     and read back as the packed record the cluster binning and the shading loop consume.
    /// </remarks>
    [Fact]
    public void AnExtractedLampsRadiusReachesThePackedRecord() {
        using var world = new World();
        var feature = new ForwardLightingRenderFeature();
        var system = new LightExtractionSystem(feature);

        var entity = world.Create();
        Lights.Attach(world, entity, Lights.Default(LightKind.Point) with { Radius = 0.16f });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new Vector3(0f, 3.4f, 0f)) });

        system.Extract(world);

        Assert.Equal(0.16f, feature.Lights[0].Radius);
        Assert.Equal(0.16f, feature.Lights[0].ToGpu().Radius);
    }

    /// <remarks>
    ///     ⚠ <b>Refilled rather than appended, which is the whole reason a light needs no handle.</b>
    ///     Two frames of a static scene must produce one list, and a destroyed entity must take its
    ///     light with it — both of which an accumulating list gets wrong in a way that only shows up
    ///     after a while.
    /// </remarks>
    [Fact]
    public void ExtractingTwiceDoesNotAccumulate() {
        using var world = new World();
        var feature = new ForwardLightingRenderFeature();
        var system = new LightExtractionSystem(feature);

        var entity = Lit(world, LightKind.Point, Vector3.Zero);

        system.Extract(world);
        system.Extract(world);

        Assert.Single(feature.Lights);

        world.Destroy(entity);
        system.Extract(world);

        Assert.Empty(feature.Lights);
        Assert.Equal(0, system.LightCount);
    }

    /// <remarks>
    ///     A light on an entity the transform system has not resolved yet has no world position, and
    ///     placing it at the origin would put a lamp in the middle of the level for one frame.
    /// </remarks>
    [Fact]
    public void ALightWithNoWorldTransformIsNotExtracted() {
        using var world = new World();
        var feature = new ForwardLightingRenderFeature();
        var system = new LightExtractionSystem(feature);

        var entity = world.Create();
        Lights.Attach(world, entity, LightKind.Point);

        system.Extract(world);

        Assert.Empty(feature.Lights);
    }

    // ------------------------------------------------------------------ the declaration

    /// <remarks>
    ///     The components are <c>[Component]</c> and <c>[DataContract]</c>, so the engine's generator
    ///     declares them from this assembly and a compiled scene may name them. Nothing calls a
    ///     registration method, which is the assertion.
    /// </remarks>
    [Fact]
    public void TheRenderComponentsAreDeclaredToTheSceneRegistry() {
        Assert.True(SceneComponentRegistry.TryGet(typeof(Light), out var light));
        Assert.Equal("Light", light.Name);

        Assert.True(SceneComponentRegistry.TryGet(typeof(MeshRenderable), out _));
        Assert.True(SceneComponentRegistry.TryGet(typeof(PrimitiveShape), out _));
    }

    /// <summary>
    ///     ⚠ A light that says nothing about its colour is white, not black.
    /// </summary>
    /// <remarks>
    ///     <c>Colour</c> is a tint — multiplied by the intensity and by the colour temperature — so a
    ///     zeroed one is a light that emits nothing however many lumens it declares. Zero is also what
    ///     a component gets by default and what a scene file that omits <c>colour:</c> reads as, which
    ///     made a level of fifteen lanterns, each with a unit, an intensity, a temperature, a range and
    ///     a radius, come out completely dark with every counter reporting them extracted.
    /// </remarks>
    [Theory]
    [InlineData(LightKind.Point)]
    [InlineData(LightKind.Directional)]
    [InlineData(LightKind.Spot)]
    public void ALightWithNoColourIsWhiteRatherThanOff(LightKind kind) {
        var authored = Lights.Default(kind) with { Colour = default, Intensity = 1200f, Temperature = 0f };
        var light = LightExtractionSystem.Convert(authored, Matrix4x4.Identity);

        Assert.Equal(1f, light.Colour.R, 5);
        Assert.Equal(1f, light.Colour.G, 5);
        Assert.Equal(1f, light.Colour.B, 5);
        Assert.True(light.Radiance.X > 0f, "a light with no colour contributed nothing");
    }

    /// <summary>An authored colour is still the authored colour.</summary>
    [Fact]
    public void AColouredLightKeepsTheColourItWasGiven() {
        var authored = Lights.Default(LightKind.Point) with { Colour = new Color3(0.2f, 0.4f, 0.9f) };
        var light = LightExtractionSystem.Convert(authored, Matrix4x4.Identity);

        Assert.Equal(0.2f, light.Colour.R, 5);
        Assert.Equal(0.9f, light.Colour.B, 5);
    }

    static Entity Lit(World world, LightKind kind, Vector3 position) {
        var entity = world.Create();

        Lights.Attach(world, entity, kind);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });

        return entity;
    }
}
