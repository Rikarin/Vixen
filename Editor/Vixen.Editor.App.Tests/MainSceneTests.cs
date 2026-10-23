// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A project's first run seeds, writes and indexes its main scene; every later run reads it (#1293).</summary>
public sealed class MainSceneTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-main-scene-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            Directory.Delete(root, recursive: true);
        } catch (IOException) {
            // A file the OS has not let go of. Not what is under test.
        }
    }

    [Fact]
    public void A_first_run_seeds_and_writes_and_the_second_reads_what_it_wrote() {
        var project = new EditorProject(new ProjectPaths(root));
        var path = MainScene.PathIn(project.Paths);

        using var first = new World("First");
        var seeded = new SceneDocument(project, first, AssetId.Empty, "Main") { Writer = new SceneFileWriter(path) };

        Assert.True(MainScene.LoadOrSeed(seeded, path, project));
        Assert.True(File.Exists(path));
        Assert.Contains(project.Assets.Entries, entry => entry.Path.EndsWith("Main" + SceneSerializer.Extension, StringComparison.Ordinal));
        Assert.False(seeded.HasUnsavedChanges.Value);

        using var second = new World("Second");
        var read = new SceneDocument(project, second, AssetId.Empty, "Main") { Writer = new SceneFileWriter(path) };

        Assert.False(MainScene.LoadOrSeed(read, path, project));
        Assert.Equal(seeded.Roots.Count, read.Roots.Count);
        Assert.Equal(seeded.Entities.Count(), read.Entities.Count());
    }
}
