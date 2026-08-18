// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>
///     A light and a shape used to be their own keys of the scene file, because neither was a component
///     any build declared. Both are <c>Vixen.Rendering</c>'s now, so both are written as ordinary
///     components — and every scene authored before that has to keep opening.
/// </summary>
public sealed class SceneFormatMigrationTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-migration-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public SceneFormatMigrationTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ALightIsWrittenAsAComponentAndNotAsItsOwnKey() {
        var entity = scene.Add("Sun", LocalTransform.Identity);
        Lights.Attach(world, entity, LightKind.Directional);

        var yaml = SceneSerializer.ToYaml(scene);

        Assert.Contains("components:", yaml, StringComparison.Ordinal);
        Assert.Contains("!Light", yaml, StringComparison.Ordinal);

        // ⚠ The legacy key is still emitted and is empty, which is what "read and never written" looks
        // like while `OmitDefaults` is a whole-document setting. What matters is that nothing is in it.
        Assert.Contains("light: null", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AShapeIsWrittenAsAComponentAndNotAsItsOwnKey() {
        var entity = scene.Add("Box", LocalTransform.Identity);
        PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cube);

        var yaml = SceneSerializer.ToYaml(scene);

        Assert.Contains("!PrimitiveShape", yaml, StringComparison.Ordinal);
        Assert.Contains("shape: ''", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ALightSurvivesTheNewRoundTrip() {
        var entity = scene.Add("Lamp", LocalTransform.At(new Vector3(0f, 3f, 0f)));
        var authored = Lights.Default(LightKind.Spot);
        authored.Colour = new Color3(0.2f, 0.4f, 0.6f);
        authored.Intensity = 2.5f;
        Lights.Attach(world, entity, authored);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            var loaded = Assert.Single(reloaded.Roots);
            Assert.True(Lights.TryGet(other, loaded, out var back));
            Assert.Equal(LightKind.Spot, back.Kind);
            Assert.Equal(authored.Colour, back.Colour);
            Assert.Equal(2.5f, back.Intensity);
            Assert.Equal(authored.OuterAngle, back.OuterAngle);
        }
    }

    [Fact]
    public void AShapeSurvivesTheNewRoundTrip() {
        var entity = scene.Add("Pipe", LocalTransform.Identity);
        PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cylinder);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            var loaded = Assert.Single(reloaded.Roots);
            Assert.True(PrimitiveShapes.TryGet(other, loaded, out var kind));
            Assert.Equal(PrimitiveKind.Cylinder, kind);
        }
    }

    /// <remarks>
    ///     ⚠ <b>The compatibility assertion, and the file below is hand-written on purpose.</b> Nothing
    ///     writes this shape any more, so a fixture built by calling the serializer could not test it —
    ///     it has to be the bytes an older editor actually produced.
    /// </remarks>
    [Fact]
    public void ASceneWrittenWithTheOldKeysStillOpens() {
        const string legacy = """
                              version: 1
                              name: Untitled
                              roots:
                                - id: 5c1b2a3d4e5f60718293a4b5c6d7e8f9
                                  name: Old Sun
                                  position: 0 4 0
                                  shape: Cube
                                  light:
                                    kind: Directional
                                    colour: 1 0.5 0.25
                                    intensity: 1.5
                                    range: 0
                                    radius: 0
                                    innerAngle: 0
                                    outerAngle: 0
                                    halfLength: 0
                              """;

        var (other, reloaded) = Fresh();

        using (other) {
            Assert.Equal(1, SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(legacy)));

            var loaded = Assert.Single(reloaded.Roots);
            Assert.Equal("Old Sun", reloaded.NameOf(loaded));

            Assert.True(Lights.TryGet(other, loaded, out var light));
            Assert.Equal(LightKind.Directional, light.Kind);
            Assert.Equal(new Color3(1f, 0.5f, 0.25f), light.Colour);
            Assert.Equal(1.5f, light.Intensity);

            // ⚠ The file's number and not the factory's, which is the assertion that lets
            // `Lights.Default` be retuned at all. A new directional light is a hundred thousand lux
            // now and was one lux before; a loader that started from the default rather than from
            // `new()` would have made this scene five decimal orders brighter on the day that
            // changed, silently and everywhere.
            Assert.NotEqual(Lights.Default(LightKind.Directional).Intensity, light.Intensity);

            // And the unit stays unstated. The legacy key has no field for one, so the light means
            // what it has always meant — `LightUnit.Native`, taken as written.
            Assert.Equal(LightUnit.Native, light.Unit);

            Assert.True(PrimitiveShapes.TryGet(other, loaded, out var kind));
            Assert.Equal(PrimitiveKind.Cube, kind);
        }
    }

    /// <remarks>
    ///     The other half of the migration: an old scene is rewritten in the new form the first time it
    ///     is saved, so the legacy reader is dead weight for that file from then on rather than
    ///     something every save has to keep agreeing with.
    /// </remarks>
    [Fact]
    public void AnOldSceneIsRewrittenInTheNewFormOnSave() {
        const string legacy = """
                              version: 1
                              name: Untitled
                              roots:
                                - id: 5c1b2a3d4e5f60718293a4b5c6d7e8f9
                                  name: Old Box
                                  shape: Sphere
                              """;

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(legacy));

            var rewritten = SceneSerializer.ToYaml(reloaded);

            Assert.Contains("shape: ''", rewritten, StringComparison.Ordinal);
            Assert.Contains("!PrimitiveShape", rewritten, StringComparison.Ordinal);

            // And the rewritten form reads back to the same thing, which is what makes the migration
            // lossless rather than merely quiet.
            var (third, again) = Fresh();

            using (third) {
                SceneSerializer.Load(again, SceneSerializer.FromYaml(rewritten));

                Assert.True(PrimitiveShapes.TryGet(third, Assert.Single(again.Roots), out var kind));
                Assert.Equal(PrimitiveKind.Sphere, kind);
            }
        }
    }

    (World World, SceneDocument Scene) Fresh() {
        var other = new World("Reloaded");

        return (other, new SceneDocument(project, other, AssetId.Empty, "Untitled"));
    }
}
