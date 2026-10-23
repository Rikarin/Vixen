// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 48 § D12's mesh-map bake: the Assets ▸ Bake Mesh Maps line, its panel, and the settings both read.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of <c>EditorParity.cs</c> whole (#1296)</b>, where it was a quarter of the file:
///         the verb, the panel's registration, the one settings object, and the bake behind the panel's
///         button. Nothing in it read the application but the shell, the project, the content tasks, the
///         map baker and the mesh source — every one of them published — so <c>LibraryModule</c> builds
///         it, and <see cref="Register" /> adds the panel, the line, the tasks' <c>Baked</c> hook and the
///         per-frame <see cref="Refresh" /> through the module's context, which takes them back on unload.
///     </para>
///     <para>
///         ⚠ <b><see cref="ContentTasks" /> is published for this, as an App-internal service</b> like
///         <c>PlayInput</c> and <c>Deferred</c>: the bake runs on its queue, behind its busy flag, and
///         reports through its single <c>Baked</c> slot.
///     </para>
/// </remarks>
sealed class MeshMapBaking {
    readonly EditorShell shell;
    readonly EditorProject project;
    readonly ContentTasks content;
    readonly IMeshMapBaker meshMaps;
    readonly IMeshSource meshes;

    /// <summary>Over the services a bake reads.</summary>
    /// <param name="shell">Where the panel, the line and the notifications go.</param>
    /// <param name="project">Whose selection and settings.</param>
    /// <param name="content">What runs the bake, and says when it is busy.</param>
    /// <param name="meshMaps">What writes the maps into the project.</param>
    /// <param name="meshes">The imported geometry, read before the file.</param>
    public MeshMapBaking(
        EditorShell shell,
        EditorProject project,
        ContentTasks content,
        IMeshMapBaker meshMaps,
        IMeshSource meshes
    ) {
        this.shell = shell;
        this.project = project;
        this.content = content;
        this.meshMaps = meshMaps;
        this.meshes = meshes;
    }

    /// <summary>Adds the panel and the Assets ▸ Bake Mesh Maps line, and follows finished bakes and the selection.</summary>
    /// <param name="context">The library module's context, which takes all four back on unload.</param>
    public void Register(PluginContext context) {
        context.AddPanel(Descriptor());

        // ⚠ Doc 48 § D12's last line, and the reason this verb exists rather than a checkbox on the
        // model importer. A mesh map has to land in `Assets/` as a file an artist can open — § D12
        // says so in one sentence: they want to look at the curvature map when a generator
        // misbehaves. An importer cannot write one there. It writes artefacts into `Library/` under
        // a cache key, and a file it dropped into `Assets/` would be a file the next scan imports,
        // that the import it came out of never declared it read, and that no cache key can see — a
        // hidden cache with a re-entrancy bug on top. Baking is therefore a verb over a selected
        // model, taken through the same scan-then-read-back-the-GUID sequence as every other thing
        // the editor puts in a project.
        //
        // ⚠ And it opens the panel rather than baking, which § D12's last bullet is what changed.
        // A verb that baked at a constant 1024 with every map on was a bake nobody chose — the
        // constant's own comment said "until § D12's bake panel exists" — and a second verb beside
        // it that opened the panel would be two answers to what Bake means, which is doc 20's A4
        // complaint. One settings object, one place to press it. `MeshMapBakeView` is the panel and
        // `Bake` is still the thing its button calls.
        context.AddCommand(
            new EditorCommand("assets.bake-mesh-maps", EditorStrings.CommandAssetsBakeMeshMaps, () => shell.Workspace.Open(Panel)) {
                Category = EditorStrings.CategoryAssets
            }
        );

        // ⚠ And the panel's result list, which is the one surface a finished bake changes that a
        // rescan does not. `LastBake` is pulled rather than handed over, so that a panel opened *after*
        // a bake can still ask what it produced.
        content.Baked = () => view?.ShowResult(content.LastBake);
        context.OnUnload(() => content.Baked = null);

        context.OnUpdate(_ => Refresh());
    }

    /// <summary>Brings the open panel up to date with the selection, once a frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Pulled</b>: what the panel says depends on the project <i>selection</i>, and
    ///     <c>Selection&lt;T&gt;</c> raises nothing when it changes — so a panel opened before a model
    ///     was clicked would sit greyed with "select a model" for ever.
    /// </remarks>
    public void Refresh() => view?.Refresh();

    /// <summary>Bakes the selected model's mesh maps into the project.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The caller <c>MapBaker.Bake</c> did not have.</b> Doc 48 § D12's seven
    ///         measurements were built on <c>BakedMaps</c> and nothing in the repository outside
    ///         <c>MapBakerTests</c> called the bake — not an importer, not a content build, not the
    ///         editor. This line and <c>ContentTasks.BakeMeshMaps</c> behind it are what makes them
    ///         reachable by a person.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source and the target are the same mesh, and that is the ordinary case
    ///         rather than a simplification.</b> A separate high-poly belongs to the retopology
    ///         flow; asking for the occlusion, curvature and thickness <i>of the mesh you are
    ///         texturing</i> is what every generator in § 4.8 reads, and it is what Painter's bake
    ///         does when nobody supplies a high-poly. A high-poly picker is a bake panel's job.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first mesh with an atlas, and it says which.</b> A model file can hold
    ///         several, the bake is one at a time because it writes one named set, and a verb that
    ///         silently picked one of four would be a verb whose output nobody can account for.
    ///     </para>
    /// </remarks>
    internal void Bake() {
        if (project.Selection.Count != 1 || !project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            return;
        }

        var absolute = project.Paths.Absolute(entry.Path);

        try {
            if (BakeableMesh(entry, absolute, out var name, out var refusal) is not { } mesh) {
                shell.Notifications.Show("Nothing to bake into", NotificationSeverity.Warning, refusal);

                return;
            }

            var kernel = ModelGeometry.ToEditMesh(mesh);

            // ⚠ The model's own asset id goes with the name, and it is what keys the set rather
            // than decorating it. The name is whatever the artist called the object in Blender,
            // which is `Cube` in every file nobody renamed — see `MeshMapNaming.ModelKey`.
            content.BakeMeshMaps(
                meshMaps,
                entry.Guid,
                name,
                kernel,
                kernel,
                Options.ToBake(),
                Options.Overwrite
            );
        } catch (Exception failure) when (failure
            is IOException
            or UnauthorizedAccessException
            or ModelFormatException
            or ArgumentException) {
            shell.Notifications.Show("Could not bake mesh maps", NotificationSeverity.Error, failure.Message);
        }
    }

    /// <summary>The first mesh of a model that has an atlas to bake into, and what to call the set.</summary>
    /// <param name="entry">The selected asset.</param>
    /// <param name="absolute">Where its file is.</param>
    /// <param name="name">What the set is named after, or empty when there is nothing to bake.</param>
    /// <param name="refusal">Why there is nothing, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The imported chunks first, and the file only for a model this project has never
    ///         imported</b> — the same seam <c>LayerStackMesh.Open</c> takes, and for the reason
    ///         <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a> gives about painting:
    ///         <c>ModelImportSettings.Unwrap</c> generates its coordinates inside
    ///         <c>ModelRetopology.Run</c>, which the <em>importer</em> calls and <c>ModelReader</c>
    ///         does not. Reading the file meant that a model unwrapped at import — the one thing
    ///         somebody does before asking for maps of its atlas — was refused here with "none of its
    ///         meshes carries texture coordinates", about a state they had already created.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the set takes the name the project gave the mesh</b>, which is what
    ///         <c>SubAssetNames</c> may have changed and what <c>TextureSetAsset.Mesh</c> is matched
    ///         against. A bake filed under the file's name beside a stack narrowed by the project's
    ///         would be two names for one mesh, and a generator reading a map by usage would bind
    ///         nothing with both of them present.
    ///     </para>
    /// </remarks>
    MeshData? BakeableMesh(AssetEntry entry, string absolute, out string name, out string refusal) {
        name = "";
        refusal = "";

        var declared = ProjectMeshSource.Declared(absolute);

        if (declared.Count > 0) {
            foreach (var mesh in declared) {
                if (meshes.TryGet(new(entry.Guid, mesh.Id), out var imported) && HasAtlas(imported)) {
                    name = mesh.Name.Length > 0 ? mesh.Name : Path.GetFileNameWithoutExtension(absolute);

                    return imported;
                }
            }

            refusal = "None of this model's imported meshes carries texture coordinates, and a mesh map is "
                + "a picture of the atlas. Unwrap it — the model importer's Unwrap setting will — and "
                + "import again.";

            return null;
        }

        var read = ModelReader.Read(
            File.ReadAllBytes(absolute),
            Path.GetExtension(absolute),
            Path.GetFileNameWithoutExtension(absolute),
            ModelSettingsOf(absolute)
        );

        if (Array.Find(read.Meshes, HasAtlas) is { } found) {
            name = found.Name.Length > 0 ? found.Name : Path.GetFileNameWithoutExtension(absolute);

            return found;
        }

        refusal = read.Meshes.Length == 0
            ? "That file has no meshes."
            : "None of its meshes carries texture coordinates, and a mesh map is a picture of the atlas. "
            + "This project has never imported it — import it, because the model importer's Unwrap "
            + "setting generates the atlas — or unwrap it in the tool that made it.";

        return null;
    }

    /// <summary>Whether a mesh has one texture coordinate per vertex, which is what a bake lands in.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>The lengths have to <em>match</em> and not merely both be non-zero.</b> A file that
    ///     lied about its vertex count gives a short array, and <c>ModelGeometry.ToEditMesh</c> reads
    ///     one coordinate per vertex — so a bake over one would land on coordinates belonging to other
    ///     vertices rather than fail.
    /// </remarks>
    static bool HasAtlas(MeshData mesh) =>
        mesh.TexCoords.Length == mesh.Positions.Length && mesh.TexCoords.Length > 0;

    /// <summary>What the Bake Mesh Maps panel is called in an arrangement.</summary>
    public const string Panel = "mesh-map-bake";

    /// <summary>The panel while it is open, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Held for <c>buildView</c>'s reason and released the same way.</b> What it displays —
    ///     which model is selected, whether a content task is running, what the last bake produced —
    ///     changes for reasons that are not edits to the panel, so something has to be able to ask it
    ///     again.
    /// </remarks>
    MeshMapBakeView? view;

    /// <summary>The one copy of what a bake is set to measure, which the verb and the panel share.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exposed for a suite to read, and it is the assertion doc 20's A4 rule needs.</b>
    ///         "The panel and the bake read one object" is not visible from either of them — a panel
    ///         editing a copy looks identical from the panel's own tests, and the failure is a
    ///         resolution somebody set being ignored by the button beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Out of the settings store rather than a field</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/701">#701</a>. <c>Get&lt;T&gt;</c>
    ///         hands every caller the same instance, which is what keeps the sentence above true
    ///         across the persistence: the panel edits what the verb reads and what
    ///         <see cref="SaveSettings" /> writes, and none of the three is a copy.
    ///     </para>
    /// </remarks>
    public MeshMapBakeSettings Options => project.Settings.Get<MeshMapBakeSettings>();

    /// <summary>Writes the bake settings, which is what every control on the panel does.</summary>
    /// <remarks>
    ///     ⚠ <b>Immediately rather than behind an Apply</b>, for the reason
    ///     <c>PlayerDelivery.SaveSettings</c> gives: the Bake button reads these fields, so an edit
    ///     that had not been committed would mean the button baking something other than what is on
    ///     screen. Nothing here costs anything until a bake runs.
    /// </remarks>
    void SaveSettings() {
        project.Settings.MarkChanged<MeshMapBakeSettings>();

        try {
            project.Settings.Save<MeshMapBakeSettings>();
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            shell.Notifications.Show(
                "Could not save the mesh-map bake settings",
                NotificationSeverity.Error,
                failure.Message
            );
        }
    }

    /// <summary>Doc 48 § D12's bake panel.</summary>
    PanelDescriptor Descriptor() =>
        new PanelDescriptor(
            Panel,
            EditorStrings.PanelMeshMapBake,
            panel => {
                var view = panel.Add<MeshMapBakeView>();

                // ⚠ Pulled, both, and `BuildSettingsView` says why: this factory runs again on
                // every reopen, so an answer captured once would be from a selection the user
                // has since changed.
                view.Chosen = Subject;
                view.Refusal = Refusal;

                view.BakeRequested += _ => Bake();

                // ⚠ Every control on the panel writes, and the panel is the only writer of these
                // settings — no inspector page over the same state, which is doc 20's A4 rule:
                // two writers is how a settings row and the panel beside it come to disagree.
                view.Changed = SaveSettings;
                view.Show(Options);
                view.ShowResult(content.LastBake);

                this.view = view;
            }
        ) {
            Closed = () => this.view = null
        };

    /// <summary>What the panel says would be baked.</summary>
    /// <remarks>
    ///     ⚠ <b>The file, not the mesh inside it.</b> Naming the mesh means reading the model through
    ///     Assimp, which is the bake's own first step and is far too much to do while a panel is
    ///     restating itself — see <see cref="Refusal" /> for what this surface may cost.
    /// </remarks>
    string? Subject() =>
        project.Selection.Count == 1 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)
            ? Path.GetFileName(entry.Path)
            : null;

    /// <summary>Why the panel's Bake button is greyed, or null when it is not.</summary>
    /// <remarks>
    ///     ⚠ <b>Cheap enough to ask once a frame, which <c>BuildRefusal</c> is not.</b> That one
    ///     enumerates the project root looking for a <c>.csproj</c> and is therefore asked only when
    ///     something happens; this is a count, a dictionary lookup and a set lookup, and the
    ///     selection it depends on has no change event to hang off — see
    ///     <see cref="MeshMapBakeView.Refresh" />, which <c>Update</c> calls while the panel is open.
    /// </remarks>
    string? Refusal() =>
        content.IsBusy
            ? "Something is already importing, building or baking."
            : SelectedIsAModel()
                ? null
                : "Select one model in the project browser — "
                + string.Join(", ", ModelExtensions.Order(StringComparer.Ordinal))
                + ".";

    /// <summary>Whether exactly one asset is selected and it is a model file.</summary>
    /// <remarks>
    ///     ⚠ <b>The extension is checked here rather than discovered by failing.</b> A verb enabled
    ///     for every selection and refusing most of them is doc 20's complaint in reverse: it reads
    ///     as an editor that cannot bake, once, per texture the artist happened to have selected.
    ///     The list is <c>ModelImporter</c>'s own <c>[Importer]</c> attribute rather than a second
    ///     copy of it, so a format added there is a format this offers.
    /// </remarks>
    bool SelectedIsAModel() =>
        project.Selection.Count == 1
        && project.Assets.TryGetByGuid(project.Selection[0], out var entry)
        && !entry.IsFolder
        && ModelExtensions.Contains(Path.GetExtension(entry.Path));

    /// <summary>What <c>ModelImporter</c> claims, read once from its own attribute.</summary>
    static readonly HashSet<string> ModelExtensions =
        new(new ModelImporter().Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>How the model is imported, so the bake reads the geometry the project uses.</summary>
    /// <remarks>
    ///     ⚠ <b>Defaults where the sidecar cannot be read, rather than a refusal.</b> A model that
    ///     has never been imported has no importer block at all, and that is the commonest moment to
    ///     want a bake — the file has just been dropped in.
    /// </remarks>
    static ModelImportSettings ModelSettingsOf(string absolute) {
        var sidecar = AssetMetaFile.PathFor(absolute);

        try {
            return File.Exists(sidecar) && AssetMetaFile.ReadFile(sidecar).Importer is ModelImportSettings settings
                ? settings
                : new();
        } catch (Exception failure)
            when (failure is IOException or YamlParseException or YamlBindingException or MetaVersionException) {
            return new();
        }
    }
}
