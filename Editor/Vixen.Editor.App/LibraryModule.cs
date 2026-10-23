// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The Content Library's own Create ▸ kinds and reference verbs, as a module over <see cref="IContentLibrary" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3, a slice of #1292.</b> The editor's own asset kinds were added to the
///         registry by <c>EditorWorlds.CreateAssetCommands</c>, and Find References and Select
///         Dependencies were registered by <c>EditorParity.AssetCommands</c> over private methods. Both
///         now come through the <see cref="PluginContext" /> a third party is handed, over the service a
///         third party can <c>Require</c> — so the Create menu is built from kinds that all arrived by
///         the same door, and the reference walk is <see cref="IContentLibrary.Referrers" /> and
///         <see cref="IContentLibrary.Dependencies" /> rather than a closure inside the application.
///     </para>
///     <para>
///         <b>And the Assets-menu verbs that only touch content</b> (#1292): Show in Explorer, New
///         Folder, Delete, Reimport All and Import Assets are names for <see cref="IContentLibrary" /> and
///         <see cref="IExternalLinks" />, and Delete asks through the shell's own confirmation, which a
///         module is handed.
///     </para>
///     <para>
///         ⚠ <b>Not yet the whole Assets menu the issue names.</b> Open is here, over the published
///         <see cref="IWorkspace" /> (whose open asks <see cref="IDocumentHost" />). History and Revert are not
///         this module's but <see cref="SourceControlModule" />'s, over the published <see cref="ISourceControl" />
///         (#1505) — the document host was never what History needed. Rename starts the browser panel's inline editor and Move To asks through this
///         editor's drawn chooser, so both still register in the application — and both end in
///         <see cref="IContentLibrary.Rename" /> and <see cref="IContentLibrary.Move" />. The menu layout — which lines appear where — is the application's
///         too, and ⚠ stays so: the Assets menu carries five contexts' lines, and a module that owned
///         the menu would take the others' off the bar when it was switched off. A line naming a
///         command nobody registered is skipped, so switching this module off takes only its lines.
///     </para>
///     <para>
///         ⚠ <b>Inside <c>Vixen.Editor.App</c> on purpose, as doc 50 § D3 says modules start.</b> The
///         kinds spell their extensions as literals — the three that named an asset editor type's
///         constant were rewritten when the application stopped referencing that assembly (#1297) —
///         so this file carries feature-name rows for the extensions; they leave the shell when the
///         module moves to an assembly whose reference set is clean.
///     </para>
/// </remarks>
sealed class LibraryModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.library";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Library";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        var registry = context.Services.Require<IEditorRegistry>();
        var library = context.Services.Require<IContentLibrary>();

        foreach (var kind in BuiltInAssetKinds) {
            context.Owns(registry.Add(kind));
        }

        // ⚠ Doc 20's A8: "Find References is the same query and belongs in three places at once."
        // Two of the three are these — the browser's context menu and the Assets menu — and they are
        // the *same command* rather than two, which is what stops them disagreeing. The third,
        // an asset field's own menu, is the inspector's and is not built.
        Reach(
            context,
            library,
            "assets.find-references",
            EditorStrings.CommandAssetsFindReferences,
            EditorStrings.CategoryAssets,
            library.Referrers,
            "Nothing references that.",
            "referrer"
        );

        // And the Edit menu's line of the same name, which was a third registration of the same walk
        // in the application. Its own id because the Edit menu and the palette list it under Edit.
        Reach(
            context,
            library,
            "edit.find-references",
            EditorStrings.CommandEditFindReferences,
            EditorStrings.CategoryEdit,
            library.Referrers,
            "Nothing references that.",
            "referrer"
        );

        Reach(
            context,
            library,
            "assets.select-dependencies",
            EditorStrings.CommandAssetsSelectDependencies,
            EditorStrings.CategoryAssets,
            library.Dependencies,
            "That does not reference anything.",
            "dependency"
        );

        var selection = library.Project.Selection;
        var links = context.Services.Require<IExternalLinks>();

        context.AddCommand(
            new EditorCommand(
                "assets.show-in-explorer",
                EditorStrings.CommandAssetsShowInExplorer,
                () => ShowInExplorer(library.Project, links)
            ) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => selection.Count > 0 && links.CanOpen
            }
        );

        // ⚠ Through the published workspace, not the application (#1292): its open is the one a
        // double-click takes, asks `IDocumentHost.TryOpen` for the document and puts it in a panel of its
        // own — the panel half is still the application's, behind the delegate `EditorWorkspace` holds.
        var workspace = context.Services.Require<IWorkspace>();

        context.AddCommand(
            new EditorCommand(
                "assets.open",
                EditorStrings.CommandAssetsOpen,
                () => {
                    if (selection.Count > 0) {
                        workspace.Open(selection[0]);
                    }
                }
            ) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => selection.Count > 0
            }
        );

        // ⚠ Greyed while the Project panel is closed, as it was in the application (#1292): the row
        // appearing in the browser is the verb's only answer, and a folder made where nobody can see
        // it is a click that seemed to do nothing.
        context.AddCommand(
            new EditorCommand(
                "assets.new-folder",
                EditorStrings.CommandAssetsNewFolder,
                () => library.CreateFolder(FolderBeside(library.Project), "New Folder")
            ) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => context.Shell.Workspace.IsOpen(ProjectBrowser.PanelId)
            }
        );

        context.AddCommand(
            new EditorCommand("assets.delete", EditorStrings.CommandAssetsDelete, () => Delete(context, library)) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => selection.Count > 0
            }
        );

        context.AddCommand(
            new EditorCommand("assets.reimport-all", EditorStrings.CommandAssetsReimportAll, library.ReimportAll) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => !library.IsImporting
            }
        );

        // ⚠ The File-category name for the same run (#1292): `ContentTasks.Import`, which is what
        // `ReimportAll` is, behind the same busy counter. It was the application's, registered over the
        // tasks object directly — the one content-task door the library had not taken.
        context.AddCommand(
            new EditorCommand("assets.import", EditorStrings.CommandImportAssets, library.ReimportAll) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => !library.IsImporting
            }
        );
    }

    /// <summary>Opens the selected asset's folder in the platform's file manager.</summary>
    /// <param name="project">The project the selection is in.</param>
    /// <param name="links">What opens a URL on this host.</param>
    static void ShowInExplorer(EditorProject project, IExternalLinks links) {
        if (project.Selection.Count == 0 || !project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            return;
        }

        var full = Path.Combine(project.Paths.Root, entry.Path);

        // The folder rather than the file: every desktop opens a directory URI in its file manager
        // and none of them agree on how to ask for a file to be revealed.
        links.Open(new Uri(Path.GetDirectoryName(full) ?? project.Paths.Root).AbsoluteUri);
    }

    /// <summary>Where New Folder makes its folder: inside the selected folder, or beside the selected file.</summary>
    /// <param name="project">The project, whose selection decides.</param>
    /// <returns>The project-relative folder; <c>Assets</c> when nothing is selected.</returns>
    /// <remarks>
    ///     What every browser does and what somebody who has just clicked a folder means. Moved from
    ///     <c>EditorApplication.NewAssetFolder</c>, whose summary also said it "starts renaming it" — ⚠ it
    ///     never did: nothing called the browser's <c>BeginRename</c> on the new row.
    /// </remarks>
    static string FolderBeside(EditorProject project) {
        var parent = "Assets";

        if (project.Selection.Count > 0 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            parent = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "Assets";
        }

        return parent;
    }

    /// <summary>Deletes the selected assets, having said what would break.</summary>
    /// <param name="context">Where the confirmation is asked.</param>
    /// <param name="library">What deletes them.</param>
    /// <remarks>
    ///     ⚠ <b>The list of referrers goes in the question, not in a report afterwards.</b>
    ///     <c>ReferenceIndex</c> has answered "what breaks if I delete this" since it was written and
    ///     nothing asked it; a list of newly-broken scenes shown once the file has gone is not a
    ///     warning. Five names and a count, because a dialog listing four hundred is one nobody reads
    ///     and the number is the part that changes the decision. The delete itself is
    ///     <see cref="IContentLibrary.Delete" />, which asks nobody.
    /// </remarks>
    static void Delete(PluginContext context, IContentLibrary library) {
        var project = library.Project;
        List<AssetId> assets = [.. project.Selection];

        if (assets.Count == 0) {
            return;
        }

        var names = assets
            .Select(asset => project.Assets.TryGetByGuid(asset, out var entry) ? entry.Name : asset.ToString())
            .ToList();

        var broken = AssetOperations.Breakage(project, assets);

        var what = assets.Count == 1 ? $"Delete '{names[0]}'?" : $"Delete {assets.Count} assets?";

        var message = broken.Count == 0
            ? "Nothing in the project references them. This cannot be undone."
            : $"{Count(broken.Count, "asset")} would be left pointing at nothing:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Take(5))
            + (broken.Count > 5 ? Environment.NewLine + $"…and {broken.Count - 5} more" : string.Empty);

        _ = Ask();

        async Task Ask() {
            if (await context.Shell.Dialogs.ConfirmAsync(what, message, "Delete", danger: true).ConfigureAwait(true)) {
                library.Delete(assets);
            }
        }

        static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    /// <summary>Registers one walk of the reference graph from the selection, as a command that selects what it found.</summary>
    /// <param name="context">The module's registration scope.</param>
    /// <param name="library">The service the walk is.</param>
    /// <param name="id">The command id.</param>
    /// <param name="title">What the menu line says.</param>
    /// <param name="category">Where the palette files it.</param>
    /// <param name="walk">Which way to walk: the service's referrers or its dependencies.</param>
    /// <param name="empty">What to say when the answer is nothing.</param>
    /// <param name="noun">What one result is called.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The answer is a selection rather than a list, and that is the useful shape.</b>
    ///         <c>ReferenceIndex</c> answers "who points at this" in one lookup; what somebody does
    ///         with the answer is open one of them, delete the lot, or look at what they have in
    ///         common — all three of which are things the browser already does to a selection.
    ///         A read-only list would be a fourth panel that can only be read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A selection of nothing is left alone and reported.</b> Replacing the selection
    ///         with an empty one would look exactly like the command having failed, and "nothing
    ///         references this" is the answer people are usually checking for.
    ///     </para>
    /// </remarks>
    static void Reach(
        PluginContext context,
        IContentLibrary library,
        string id,
        StringId title,
        StringId category,
        Func<IEnumerable<AssetId>, IReadOnlyList<AssetId>> walk,
        string empty,
        string noun
    ) {
        var selection = library.Project.Selection;

        context.AddCommand(
            new EditorCommand(
                id,
                title,
                () => {
                    var found = walk(selection);

                    if (found.Count == 0) {
                        context.Shell.Notifications.Show(empty, NotificationSeverity.Info);
                        return;
                    }

                    selection.Set(found);
                    context.Shell.Context = EditorApplication.AssetContext;
                    context.Shell.Notifications.Success(found.Count == 1 ? $"1 {noun}" : $"{found.Count} {noun}s");
                }
            ) {
                Category = category,
                Enablement = () => selection.Count > 0
            }
        );
    }

    // ── The editor's own Create ▸ kinds ─────────────────────────────────────────────────────────

    /// <summary>The asset kinds the authoring surfaces add, and what an empty one of each is called.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An empty file, not a template.</b> Every one of these documents opens a zero-byte
    ///         file as a sensible new one — a VFX graph with a spawner and an output, a shader graph
    ///         with a colour property and a master, an animation graph with one layer and one state,
    ///         an input asset with <c>Player/Move</c> — which is <c>AssetFile.Read</c>'s own bargain
    ///         and is why creating one is a <c>File.Create</c> rather than a copy of something in a
    ///         templates folder that would then be a second place the defaults live.
    ///     </para>
    ///     <para>
    ///         Seven of these are E5's and the shader graph is the eighth, added when its panel was:
    ///         doc 20's Create ▸ names it, and until this line existed the only way to reach the
    ///         editor was to make the file outside the editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Doc 31's four are the exception to the empty-file bargain, and they have to
    ///         be.</b> Those documents open a zero-byte file as a sensible new one; a file read by
    ///         <c>TerrainAssetImporter</c> is deserialised and its type's own <c>Validate()</c> run —
    ///         so an empty one imports incomplete and says so beside the file. Starter text is the
    ///         difference between "created" and "created, with a warning". ⚠ Three of the four — the
    ///         layer, the foliage type and the grass type — are declared by the terrain module now
    ///         (<c>TerrainAssetKinds</c>, #1287), and arrive and leave with it; the spline stays here,
    ///         because a curve is not terrain's alone. It does not open: nothing claims it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>.vxterrain</c> is deliberately not on this list.</b> A terrain is a size, a
    ///         tile count and a height range before it is a file, and an empty one of those is eight
    ///         gigabytes as easily as eight megabytes — which is what the Terrain panel's create form
    ///         and its derived-cost readout exist for. A Create ▸ line making a default-sized one
    ///         would be the one asset in the menu whose default is a guess about the size of
    ///         somebody's world.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>These are registered, not read.</b> F3 reported this as a literal array in the
    ///         application, which is why a plugin introducing an asset type could not put it in
    ///         Create ▸ at all. It is still a literal — the application is where the application's own
    ///         kinds belong — but it goes into <c>EditorRegistry</c> when this module activates, through
    ///         the <see cref="PluginContext" /> a plugin is handed, and the menu is built from the
    ///         registry — so a contributed kind and a built-in one are the same thing to everything
    ///         downstream, and switching this module off takes the editor's own lines out of Create ▸.
    ///     </para>
    /// </remarks>
    static readonly NewAssetKind[] BuiltInAssetKinds = [
        // ⚠ The two an editor is *for*, and neither had a line here until 2026-09-17. A scene could
        // be reset in memory by File ▸ New Scene and never written as a second file, and a material
        // — the asset the whole of doc 48 is about — could only be made by creating the file by
        // hand and opening it, which AssetFile.Read's remarks describe as "the ordinary way" because
        // it was the only way. Both are registered first so they lead the menu; the sort below is
        // stable, so position here is order there.
        new("assets.create-scene", EditorStrings.CreateAssetKinds["scene"], SceneFile.Extension, "New Scene", NewSceneFile),
        new("assets.create-material", EditorStrings.CreateAssetKinds["material"], ".vxmat", "New Material", NewMaterial),

        new("assets.create-shader-graph", EditorStrings.CreateAssetKinds["shader-graph"], ".vxshadergraph", "New Shader Graph"),

        // The project's frame, as the game template ships it — one !StandardFrame node whose knobs
        // are semantic — and an addressable group. Both are claimed by an importer and both open.
        // ⚠ Spelled as literals since #1297, like every other line here: the three constants were
        // the asset editors' types, and naming them was this file's whole reference to that assembly.
        new("assets.create-frame", EditorStrings.CreateAssetKinds["frame"], ".vxcompositor", "New Frame", NewFrame),
        new("assets.create-addressable-group", EditorStrings.CreateAssetKinds["addressable-group"], ".vxgroup", "New Group", NewGroup),

        new("assets.create-vfx", EditorStrings.CreateAssetKinds["vfx"], ".vxvfx", "New Effect"),
        new("assets.create-animation", EditorStrings.CreateAssetKinds["animation"], ".vxanim", "New Clip"),
        new("assets.create-animation-graph", EditorStrings.CreateAssetKinds["animation-graph"], ".vxanimgraph", "New Animation Graph"),
        new("assets.create-sequence", EditorStrings.CreateAssetKinds["sequence"], ".vxseq", "New Sequence"),
        new("assets.create-input", EditorStrings.CreateAssetKinds["input"], ".vxinput", "New Input Actions"),
        new("assets.create-mixer", EditorStrings.CreateAssetKinds["mixer"], ".vxmixer", "New Mixer"),
        new("assets.create-font", EditorStrings.CreateAssetKinds["font"], ".vxfont", "New Font"),

        new("assets.create-spline", EditorStrings.CreateAssetKinds["spline"], ".vxspline", "New Spline", NewSpline, false),

        new("assets.create-move-set", EditorStrings.CreateAssetKinds["move-set"], ".vxmoveset", "New Move Set", NewMoveSet),
        new("assets.create-proxy-shapes", EditorStrings.CreateAssetKinds["proxy-shapes"], ".vxproxyshapes", "New Proxy Shapes", NewProxyShapes),
        new("assets.create-shape-vocabulary", EditorStrings.CreateAssetKinds["shape-vocabulary"], ".vxshapevocab", "New Shape Vocabulary", NewVocabulary),
        new("assets.create-priorities", EditorStrings.CreateAssetKinds["priorities"], ".vxpriorities", "New Priority Ladder", NewPriorities, false),
        new("assets.create-constraint-template", EditorStrings.CreateAssetKinds["constraint-template"], ".vxconstraints", "New Constraint Template", NewTemplate, false),
        new("assets.create-harness", EditorStrings.CreateAssetKinds["harness"], ".vxharness", "New Harness", NewHarness)
    ];

    /// <summary>A scene with one root, which is what File ▸ New Scene builds in memory.</summary>
    /// <remarks>
    ///     ⚠ <b>One root rather than none, on purpose.</b> <c>NewScene</c> adds "Scene Root" at
    ///     identity, so a file made from the menu and a scene made from the File menu are the same
    ///     scene — and an author who has only ever seen one of them meets no surprise in the other.
    /// </remarks>
    const string NewSceneFile = """
        version: 1
        name: New Scene
        roots:
          - name: Scene Root
            position: 0 0 0
        """;

    /// <summary>A neutral dielectric, which is the material an object has before anyone chooses one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One feature rather than none, because the importer questions a material with
    ///         none.</b> <c>MaterialImporterTests.AMaterialWithNoFeaturesIsBuiltAndQuestioned</c>
    ///         pins that a featureless material builds with a <em>warning</em> — right for a block-out
    ///         somebody left, and wrong as the first thing the Create menu greets an author with.
    ///         <c>!MetalRoughness</c> with constants is the smallest feature that samples nothing, so
    ///         the file imports clean before a single texture exists in the project.
    ///     </para>
    ///     <para>
    ///         Grey at 0.8 rather than white, because the renderer works in cd/m² and an albedo of one
    ///         returns every photon it receives — a surface no real material is.
    ///     </para>
    /// </remarks>
    const string NewMaterial = """
        shader: ForwardPlus
        shading: StandardShading
        features:
          - !MetalRoughness
            baseColor: 0.8 0.8 0.8
            metalness: 0
            roughness: 0.5
        """;

    /// <summary>The frame the game template ships, verbatim.</summary>
    /// <remarks>
    ///     Kept identical to <c>Tools/Vixen.Templates/templates/vixen-game/Assets/Frame.vxcompositor</c>
    ///     rather than abbreviated, because the comments are the point: they name the guide, say what
    ///     each knob may be, and say what <c>vixen frame explode</c> costs before anyone runs it.
    /// </remarks>
    const string NewFrame = """
        # The project's whole frame, as one node. The knobs are semantic on purpose: they say
        # what the game wants, never how the frame is wired — the node expands at build time
        # into the same graph a hand-authored document would contain. Guide:
        # docs/guide/rendering/standard-frame and docs/guide/rendering/choosing-a-frame.
        # When the knobs stop being enough, eject with
        # `vixen frame explode Assets/Frame.vxcompositor` — one-way, and the file will say so.
        #
        # `gi:` above Off and `reflections: Screen` also need their host halves — the shading
        # permutations and the builder's field slots — before they do more than nothing;
        # Samples/03-PbrShowcase is the smallest complete example of paying for them.
        version: 2
        game: !StandardFrame
          quality: High          # Low | Medium | High | Epic — or delete this line and GraphicsOptions.Quality decides
          shadows: Cascades      # Off | Cascades | Virtual
          gi: Off                # Off | Ambient | Probes
          reflections: Off       # Off | Probe | Screen
          antialiasing: Taa      # Off | Fxaa | Taa | TaaFxaa
          exposure: Automatic    # Fixed | Automatic
          output: SceneColour
        """;

    /// <summary>An addressable group with the defaults every sample's <c>Default.vxgroup</c> has.</summary>
    const string NewGroup = """
        name: New Group
        loadPath: Local
        packing: PackTogether
        bundleNaming: FilenameHash
        """;

    /// <summary>An empty movement vocabulary.</summary>
    /// <remarks>
    ///     No rows, because a row with no clip is an import error and a row naming a clip that does
    ///     not exist is a worse one: the first says "fill this in" beside the file, and the second
    ///     says nothing at all until somebody plays it.
    /// </remarks>
    const string NewMoveSet = """
        name: New Move Set
        entries: []
        rules: []
        """;

    /// <summary>An empty shape set that names neither a vocabulary nor a rig yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Both reference lines are absent rather than pointing at a conventional path.</b> A set
    ///     naming a file that does not exist fails its import — the importer declares the dependency
    ///     and refuses — where one naming none imports clean and unchecked, which is the right state
    ///     for a file somebody made ten seconds ago. The panel has a row for each, so the first thing
    ///     it says about a new set is which one to fill in.
    /// </remarks>
    const string NewProxyShapes = """
        name: New Proxy Shapes
        shapes: []
        """;

    /// <summary>A vocabulary with the two terms almost every humanoid set uses.</summary>
    /// <remarks>
    ///     Two rather than none, because the file's whole purpose is a list somebody adds to — and an
    ///     empty list gives no clue what a term looks like, or that the meaning is the useful half.
    /// </remarks>
    const string NewVocabulary = """
        name: New Vocabulary
        shapes:
          - name: belly
            meaning: The front of the torso, where a hand rests or leans.
          - name: right-palm
            meaning: The gripping face of the right hand.
        tags: []
        classes: []
        """;

    /// <summary>The conventional ladder, spelled out in the file.</summary>
    /// <remarks>
    ///     ⚠ <b>The rungs are in the file and not defaulted by the reader.</b> A priority is a name a
    ///     project agrees on, and the agreement has to be somewhere two people can read and edit it;
    ///     a ladder that came from a constant would be one nobody knew they were allowed to change.
    ///     A hundred apart, because a sub-step is clamped to ±99 and the importer warns about any two
    ///     rungs closer than that.
    /// </remarks>
    const string NewPriorities = """
        name: New Priority Ladder
        step: 100
        rungs:
          - name: flourish
            value: 0
            meaning: A secondary motion. Anything may override it.
          - name: look
            value: 100
            meaning: Where the head is pointed.
          - name: aim
            value: 200
            meaning: Where a weapon is pointed.
          - name: balance
            value: 300
            meaning: Keeping the body over its feet.
          - name: interaction
            value: 400
            meaning: A deliberate reach for something.
          - name: contact
            value: 500
            meaning: A hand or a foot that must not slide.
        """;

    /// <summary>A named template with no tags in it yet.</summary>
    /// <remarks>
    ///     ⚠ <b>The name is why this is not an empty file.</b> The importer refuses a nameless
    ///     template: the name is written into every tag it produces and is how a re-apply finds them
    ///     again, so a nameless one can be applied once and never maintained.
    /// </remarks>
    const string NewTemplate = """
        name: New Template
        revision: 1
        meaning: What this bundle of constraints is for.
        tags: []
        """;

    /// <summary>A run that names nothing yet, and one threshold so that it is a gate.</summary>
    /// <remarks>
    ///     ⚠ <b>This one imports with an error on purpose, and the error is the instructions.</b> A
    ///     harness refuses a plan with no clip and no rig, because a build step that always passes is
    ///     worse than one that says what it is missing — so the two lines an author has to fill in are
    ///     the two the importer complains about, beside the file, the moment it is created.
    ///     <para>
    ///         ⚠ <b><c>''</c> rather than a bare <c>clip:</c>, and the quotes are load-bearing.</b> An
    ///         empty scalar is the document's null, and
    ///         <see cref="Vixen.Animation.Constraints.HarnessPlanContent.Clip" /> is
    ///         declared <c>string</c> — so a bare key is refused by the binder before the importer can
    ///         reach it, and the author gets a schema complaint instead of the sentence written for
    ///         them. The error this file is *for* is the importer's, which needs the plan to bind
    ///         first.
    ///     </para>
    /// </remarks>
    const string NewHarness = """
        name: New Harness
        clip: ''
        rig: ''
        samples: 32
        bodies: [0.85, 1.0, 1.2]
        thresholds:
          residual: 0.02
        """;

    /// <summary>Two control points, which is the fewest a curve can be built from.</summary>
    /// <remarks>
    ///     ⚠ <b>One point is an error rather than a warning</b> — <c>SplineAsset.Build</c> throws for
    ///     it, and <c>TerrainAssetImporter</c> fails the import on that. So a created spline has two,
    ///     ten metres apart, and is a road somebody can drag rather than a file that arrives broken.
    /// </remarks>
    const string NewSpline = """
        name: New Spline
        points:
          - position: {x: 0, y: 0, z: 0}
          - position: {x: 10, y: 0, z: 0}
        """;
}
