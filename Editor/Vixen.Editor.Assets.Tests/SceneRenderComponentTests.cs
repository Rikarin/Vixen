// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     A light and a mesh, all the way from a <c>.vxscene</c> to entities in a world.
/// </summary>
/// <remarks>
///     <para>
///         <b>The thing that was structurally impossible.</b> Both components used to be the editor's,
///         so a compiled scene could not name either — a level's lighting existed in the authoring file
///         and nowhere else, and a build that shipped it shipped an unlit room. This is the assertion
///         that the whole path now closes: authored keys, through the binder, into a column, through
///         <c>SceneContent.Instantiate</c>, onto an entity.
///     </para>
///     <para>
///         ⚠ <b>The constructor touches a <c>Vixen.Rendering</c> type on purpose.</b> The components are
///         declared by a <c>[ModuleInitializer]</c> in the assembly that owns them, and a module
///         initializer runs when its assembly is loaded — so a test that only ever named these types in
///         a YAML string would be asserting against a registry that had never heard of them. A real
///         editor loads its subsystems for other reasons; a test has to be explicit about it, and this
///         is what the caveat looks like from the inside.
///     </para>
/// </remarks>
public sealed class SceneRenderComponentTests {
    public SceneRenderComponentTests() => _ = Lights.All.Count;

    const string Lit = """
                       version: 1
                       name: Lit
                       roots:
                         - id: 0123456789abcdef0123456789abcdef
                           name: Sun
                           position: 0 10 0
                           components:
                             - !Light
                               kind: Directional
                               colour: 1 0.9 0.8
                               intensity: 2.5
                               range: 0
                         - id: 11111111111111111111111111111111
                           name: Crate
                           components:
                             - !MeshRenderable
                               mesh: vx:9e8a44c9930c64e388ca034c5fe4c426#2b9e5f13
                               material: vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081
                               castsShadows: true
                         - id: 22222222222222222222222222222222
                           name: Block
                           components:
                             - !PrimitiveShape
                               kind: Cylinder
                       """;

    [Fact]
    public void AnAuthoredLightCompilesAndLoadsOntoAnEntity() {
        var content = Compile();

        using var world = new World();
        var created = new Entity[3];
        content.Instantiate(world, created);

        Assert.True(world.Has<Light>(created[0]));

        var light = world.Read<Light>(created[0]);
        Assert.Equal(LightKind.Directional, light.Kind);
        Assert.Equal(2.5f, light.Intensity);
        Assert.Equal(0.9f, light.Colour.G, 5);
    }

    /// <remarks>
    ///     The reference survives with its sub-asset id intact, which is the half of it that could not
    ///     be recovered from an address — see <c>BuildPlanner.SubAssetAddress</c>.
    /// </remarks>
    [Fact]
    public void AnAuthoredMeshCompilesWithBothItsReferences() {
        var content = Compile();

        using var world = new World();
        var created = new Entity[3];
        content.Instantiate(world, created);

        var renderable = world.Read<MeshRenderable>(created[1]);

        Assert.Equal(new AssetId(new("9e8a44c9-930c-64e3-88ca-034c5fe4c426")), renderable.Mesh.Asset);
        Assert.Equal(new SubAssetId(0x2b9e5f13), renderable.Mesh.SubAsset);
        Assert.Equal(new AssetId(new("1a2b3c4d-5e6f-7081-9a2b-3c4d5e6f7081")), renderable.Material.Asset);
        Assert.True(renderable.CastsShadows);
    }

    [Fact]
    public void AnAuthoredPrimitiveCompilesToo() {
        var content = Compile();

        using var world = new World();
        var created = new Entity[3];
        content.Instantiate(world, created);

        Assert.Equal(PrimitiveKind.Cylinder, world.Read<PrimitiveShape>(created[2]).Kind);
    }

    /// <remarks>
    ///     ⚠ Three entities carrying three different components are three archetypes, so this also
    ///     asserts the compiler put each in its own block — the arrangement that makes a load one bulk
    ///     create per archetype rather than one per entity.
    /// </remarks>
    [Fact]
    public void EachComponentSetIsItsOwnBlock() {
        var content = Compile();

        Assert.Equal(3, content.Count);
        Assert.Equal(3, content.Blocks.Length);
        Assert.All(content.Blocks, block => Assert.Single(block.Columns));
    }

    static SceneContent Compile() {
        var problems = new List<string>();

        var content = SceneCompiler.Compile(
            SceneFile.FromYaml(Lit),
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
