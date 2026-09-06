// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     What the Bake button bakes is the geometry the project has —
///     <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a> on the bake side.
/// </summary>
/// <remarks>
///     <para>
///         <b>The fixture is a cube with no <c>vt</c> line in it, imported with
///         <c>UnwrapMode.Always</c> and its one mesh renamed.</b> So the file has no atlas at all and
///         the project has a perfectly good one — which is exactly the state an artist is in when
///         they ask for mesh maps, because unwrapping is what they did in order to be able to.
///         Reading the file, which is what this verb did, refused it with "none of its meshes carries
///         texture coordinates".
///     </para>
///     <para>
///         ⚠ <b>The assertion is the task's title and not a written map, because the bake is on the
///         pool.</b> <c>ContentTasks.BakeMeshMaps</c> starts it and returns; how long the casting
///         takes is the machine's business, and a suite that waited for the PNGs would be a
///         wall-clock budget calibrated on an idle machine — this repository's largest flake source.
///         The title carries both halves of the claim anyway: a bake was started at all, and it is
///         filed under the name the <em>project</em> gives that mesh rather than the one in the file.
///     </para>
///     <para>
///         ⚠ <b>Live tasks and ended ones are both collected, which is what makes it race-free.</b>
///         A settle runs a fixed number of frames; whether a 256-texel bake finishes inside them is
///         not something to assert. Subscribing before the press and reading the list after it means
///         the title is seen either way.
///     </para>
/// </remarks>
public sealed class MeshMapBakeGeometryTests : IDisposable {
    /// <summary>A cube with no texture coordinates, so an atlas can only have been generated.</summary>
    const string Cube = """
        o Cube
        v -0.5 -0.5 -0.5
        v 0.5 -0.5 -0.5
        v 0.5 0.5 -0.5
        v -0.5 0.5 -0.5
        v -0.5 -0.5 0.5
        v 0.5 -0.5 0.5
        v 0.5 0.5 0.5
        v -0.5 0.5 0.5
        f 5 6 7
        f 5 7 8
        f 1 4 3
        f 1 3 2
        f 2 3 7
        f 2 7 6
        f 1 5 8
        f 1 8 4
        f 4 8 7
        f 4 7 3
        f 1 2 6
        f 1 6 5

        """;

    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vixen-bake-geometry-" + Guid.NewGuid().ToString("N")
    );

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A model unwrapped at import is bakeable, under the name the project gave its mesh.</summary>
    [Fact]
    public async Task A_bake_reads_the_atlas_the_import_generated_and_the_name_it_chose() {
        await Prepare(new() { Unwrap = UnwrapMode.Always, UnwrapResolution = 256, SubAssetNames = [Rename] });

        using var session = EditorSession.Start(new() { ProjectRoot = root });
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");

        // The smallest bake this panel offers, because what is under test is which geometry reaches
        // it and not how long casting takes.
        view.ResolutionPicker.Value = "256";
        view.SamplesPicker.Value = "16";

        foreach (var box in new[] { view.Occlusion, view.Bent, view.Curvature, view.Thickness, view.PositionMap, view.WorldNormal, view.Identifiers }) {
            box.IsChecked = false;
        }

        List<string> titles = [];

        session.Shell.Tasks.Ended += task => titles.Add(task.Title);

        Select(session);

        Assert.False(view.BakeButton.Disabled, "a selected model left Bake greyed.");

        session.Click(view.BakeButton);
        session.Settle();

        titles.AddRange(session.Shell.Tasks.Tasks.Select(task => task.Title));

        // ⚠ 'Body' and not 'Cube'. The name is the sidecar's, which `SubAssetNames` chose; a bake
        // filed under the file's name would be a set no stack narrowed by `TextureSetAsset.Mesh`
        // could bind, which is the same rename read from the other end.
        Assert.Contains(titles, title => title.Contains("Body", StringComparison.Ordinal));

        Assert.DoesNotContain(
            session.Shell.Notifications.History,
            message => message.Message.Contains("Nothing to bake into", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     A model this project has never imported is still read from the file, and told which action
    ///     it is owed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The fallback is what stops this being a regression for the commonest moment.</b> A
    ///     model dropped into <c>Assets/</c> a minute ago has no import record, and refusing it
    ///     outright would have made "drop a model in and bake it" stop working in order to fix
    ///     "unwrap a model and bake it". What changed there is the sentence, which now names the
    ///     import rather than telling somebody to do again what they already did.
    /// </remarks>
    [Fact]
    public void An_unimported_model_without_an_atlas_is_told_that_its_import_has_not_run() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, "Assets", "Hull.obj"), Cube);

        using var session = EditorSession.Start(new() { ProjectRoot = root });
        var view = session.Control<MeshMapBakeView>("mesh-map-bake");

        Select(session);
        session.Click(view.BakeButton);
        session.Settle();

        Assert.Contains(
            session.Shell.Notifications.History,
            message => (message.Detail ?? "").Contains("never imported", StringComparison.Ordinal)
        );
    }

    /// <summary>What the one mesh in the fixture is renamed to.</summary>
    static SubAssetRename Rename => new() { Source = "Cube", Name = "Body" };

    /// <summary>Selects the fixture's model in the project the session opened.</summary>
    static void Select(EditorSession session) {
        var entry = session.Project.Assets.Entries.First(
            candidate => candidate.Path.EndsWith("Hull.obj", StringComparison.Ordinal)
        );

        session.Project.Selection.Set([entry.Guid]);
        session.Settle();
    }

    /// <summary>Writes the model, gives it import settings, and imports the project — before the session.</summary>
    /// <remarks>
    ///     ⚠ <b>Before, and that ordering is forced rather than tidy.</b> A workspace loads the import
    ///     cache in its constructor and the editor builds its own when it opens the project, so an
    ///     import run <em>after</em> the session started would be invisible to the very source the
    ///     bake reads — which is a real limitation of the editor and not of this test, and is why
    ///     <c>ContentTasks</c> owns the one workspace the editor has.
    /// </remarks>
    async Task Prepare(ModelImportSettings settings) {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));

        var absolute = Path.Combine(root, "Assets", "Hull.obj");

        File.WriteAllText(absolute, Cube);

        var workspace = new ProjectWorkspace(new ProjectPaths(root));

        workspace.Database.Scan();

        var sidecar = AssetMetaFile.PathFor(absolute);

        AssetMetaFile.WriteFile(sidecar, AssetMetaFile.ReadFile(sidecar) with { Importer = settings });

        List<ContentDiagnostic> said = [];

        var summary = await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            said.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            summary.Failed == 0,
            "the import failed: " + string.Join("; ", said.Select(diagnostic => diagnostic.Message))
        );

        workspace.Save();

        // The bake asks the library what a usage resolves to, so the fixture is only honest if the
        // maps it is about to write are the first ones there.
        Assert.Empty(MeshMapLibrary.Index(workspace.Database).Maps);
    }
}
