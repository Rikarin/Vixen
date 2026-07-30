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

/// <summary>An entity carrying a mesh asset is drawn in the viewport.</summary>
/// <remarks>
///     <para>
///         <b>Picking rendered, gizmos rendered, and geometry did not.</b> The collector knew how to
///         draw the built-in shapes and had no way to turn a mesh reference into vertices — so a level
///         of authored meshes was a viewport of nothing, while the same scene drew correctly in a game.
///     </para>
///     <para>
///         The source is a fake rather than a project's import cache, deliberately: what is being
///         asserted is that a reference reaches a batch and that the batch names the right geometry,
///         and a real cache would need a model imported to disk to say the same thing.
///     </para>
/// </remarks>
public class SceneMeshAssetTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-mesh-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    static readonly AssetReference Rock = new(new AssetId(new("11111111-1111-1111-1111-111111111111")));
    static readonly AssetReference Tree = new(new AssetId(new("22222222-2222-2222-2222-222222222222")));

    public SceneMeshAssetTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    /// <summary>A mesh reference becomes a batch that names the geometry the source gave.</summary>
    [Fact]
    public void A_mesh_reference_is_collected_as_its_own_batch() {
        Draw(Rock, new(4f, 0f, 0f));

        var meshes = new SceneMeshes { Meshes = new Meshes() };

        Assert.Equal(1, meshes.Build(scene));

        var batch = Assert.Single(meshes.Batches);

        Assert.True(batch.Shape.IsAsset);
        Assert.Equal(Rock, batch.Shape.Mesh);
        Assert.Equal(1, batch.Count);

        // And the geometry a device would register for it is the mesh, not a primitive: `Shape` is
        // the only thing that turns a batch into vertices, so this is the whole of the join.
        Assert.Equal("rock", meshes.Shape(batch.Shape)!.Name);
        Assert.Equal(4f, meshes.Instances[0].Transform.M41, 4);
    }

    /// <summary>Two entities drawing one mesh are one batch, and two meshes are two.</summary>
    /// <remarks>
    ///     The grouping is the whole reason a batch has a key: a hundred instances of a rock have to be
    ///     one draw, and a key that compared by identity rather than by reference would make them a
    ///     hundred.
    /// </remarks>
    [Fact]
    public void Entities_sharing_a_mesh_share_a_batch() {
        Draw(Rock, Vector3.Zero);
        Draw(Tree, new(3f, 0f, 0f));
        Draw(Rock, new(6f, 0f, 0f));

        var meshes = new SceneMeshes { Meshes = new Meshes() };

        meshes.Build(scene);

        Assert.Equal(2, meshes.Batches.Count);
        Assert.Equal(2, Assert.Single(meshes.Batches, batch => batch.Shape.Mesh == Rock).Count);
        Assert.Equal(1, Assert.Single(meshes.Batches, batch => batch.Shape.Mesh == Tree).Count);
    }

    /// <summary>A mesh and a shape are different batches and the keys do not collide.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure a default key would cause.</b> <c>PrimitiveKind.Cube</c> is zero, so a mesh
    ///     whose key left the kind at its default would batch with every cube in the scene — and the
    ///     cubes would be drawn as the mesh or the mesh as cubes, depending on which registered first.
    /// </remarks>
    [Fact]
    public void A_mesh_and_a_cube_do_not_share_a_key() {
        Draw(Rock, Vector3.Zero);
        scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(new Vector3(3f, 0f, 0f)));

        var meshes = new SceneMeshes { Meshes = new Meshes() };

        meshes.Build(scene);

        Assert.Equal(2, meshes.Batches.Count);
        Assert.Contains(meshes.Batches, batch => batch.Shape == SceneShape.Of(PrimitiveKind.Cube));
        Assert.Contains(meshes.Batches, batch => batch.Shape == SceneShape.Of(Rock));
    }

    /// <summary>With no source, a mesh entity is counted rather than drawn as something else.</summary>
    /// <remarks>
    ///     What an editor that has not imported the project yet is. Worth a number rather than a
    ///     silence: a scene of entities and an empty viewport is otherwise indistinguishable from a
    ///     camera pointing the wrong way.
    /// </remarks>
    [Fact]
    public void Without_a_source_a_mesh_entity_is_waited_for() {
        Draw(Rock, Vector3.Zero);

        var meshes = new SceneMeshes();

        Assert.Equal(0, meshes.Build(scene));
        Assert.Equal(1, meshes.Waiting);
        Assert.Empty(meshes.Batches);
    }

    /// <summary>An entity carrying both draws its mesh, exactly as a game would.</summary>
    /// <remarks>
    ///     ⚠ <b>The rule is the extraction system's and this has to match it.</b> A game makes "the mesh
    ///     wins" an archetype fact with <c>WithNone&lt;MeshRenderable&gt;</c>; the editor walks a
    ///     document's entity list and so writes it as a branch. An entity that looked different in the
    ///     viewport from how it looks in the game is the one defect a viewport must not have.
    /// </remarks>
    [Fact]
    public void An_entity_carrying_both_is_drawn_as_its_mesh() {
        var entity = scene.CreateShape(PrimitiveKind.Sphere, LocalTransform.At(Vector3.Zero));

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(Rock));

        var meshes = new SceneMeshes { Meshes = new Meshes() };

        meshes.Build(scene);

        Assert.Equal(SceneShape.Of(Rock), Assert.Single(meshes.Batches).Shape);
    }

    Entity Draw(AssetReference mesh, Vector3 at) {
        var entity = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(at));

        world.Remove<PrimitiveShape>(entity);
        MeshRenderables.Attach(world, entity, MeshRenderables.Default(mesh));

        return entity;
    }

    /// <summary>A source that names each mesh after the reference it was asked for.</summary>
    sealed class Meshes : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = reference == Rock ? "rock" : "tree",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
