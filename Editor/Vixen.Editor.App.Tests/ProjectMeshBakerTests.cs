// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 24's block-out bake, and the identity it is supposed to hand back.</summary>
/// <remarks>
///     ⚠ <b>Found by writing doc 48's mesh-map bake next to it, and it had no test of its own.</b>
///     <c>IMeshBaker</c>'s remarks are emphatic that the point of the seam is the return value — "it
///     returns a reference rather than a path, because a path is not an identity" — and
///     <see cref="ProjectMeshBaker" /> measured that path from <c>Paths.Assets</c> while the database
///     keys every entry from <c>Paths.Root</c>. So the file was written, the sidecar was minted, the
///     GUID existed, and the look-up asked for <c>Blockout/Wall.obj</c> against an index holding
///     <c>Assets/Blockout/Wall.obj</c>. Every block-out bake returned <c>AssetReference.Null</c>, and
///     nothing anywhere said so — the entity got a reference to nothing and the file was on disk to
///     prove the bake had worked.
/// </remarks>
public sealed class ProjectMeshBakerTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-blockout-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A file the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    /// <summary>A baked block-out comes back as the asset it became, not as nothing.</summary>
    [Fact]
    public void A_baked_mesh_comes_back_as_an_asset() {
        var project = Project();
        var baker = new ProjectMeshBaker(project);
        var reference = baker.Bake("Wall", ".obj", "o Wall\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

        Assert.NotNull(baker.Written);
        Assert.True(File.Exists(baker.Written), baker.Written);
        Assert.NotEqual(AssetReference.Null, reference);
        Assert.True(project.Assets.TryGetByGuid(reference.Asset, out var entry));
        Assert.Equal("Assets/Blockout/Wall.obj", entry.Path);
    }

    /// <summary>Baking the same name twice overwrites the file and keeps its identity.</summary>
    /// <remarks>Its own remarks promise this: "an existing file of the same name is overwritten, and
    ///     it keeps its GUID", so that every entity already pointing at the asset picks up the new
    ///     shape.</remarks>
    [Fact]
    public void Re_baking_keeps_the_guid() {
        var project = Project();
        var baker = new ProjectMeshBaker(project);

        var first = baker.Bake("Wall", ".obj", "o Wall\nv 0 0 0\n");
        var second = baker.Bake("Wall", ".obj", "o Wall\nv 1 0 0\n");

        Assert.NotEqual(AssetReference.Null, first);
        Assert.Equal(first, second);
    }

    EditorProject Project() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        return new(new ProjectPaths(root));
    }
}
