// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § P2c: the Content Library's use cases, with no window and through the plugin door.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two halves, and each proves something the other cannot.</b> The first half builds the
///         service over a bare <see cref="EditorProject" /> — no shell, no panel, no session — which is
///         doc 50 § D2's first property: a test calls the verb without a window. The second opens an
///         editor the way <c>EditorHost</c> does and reaches the verb by <i>effect</i> — a drop the
///         platform delivers, a command id — and asserts the object that did the work is the one
///         published to plugins, which is the property a plugin depends on.
///     </para>
///     <para>
///         ⚠ <b>The browser suites (<c>BrowserFileDropTests</c>, <c>BrowserCollectionTests</c>,
///         <c>BrowserSavedFilterTests</c>, <c>ExpiredRefusalTests</c>' package) are the wiring proof
///         and stay as they were</b>; what changed under them is that every one now runs through this
///         service.
///     </para>
/// </remarks>
public sealed class ContentLibraryTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-content-library", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(root, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }

    /// <summary>Import copies in, indexes, asks the tasks to import and tells the browser — with no window.</summary>
    [Fact]
    public void Import_copies_indexes_and_imports_without_a_window() {
        var (library, project, imports) = Bare();
        var arrived = 0;

        library.Arrived += () => arrived++;

        var outside = Outside("wood.png");

        Assert.Equal(1, library.Import([outside], AssetTree.RootName));

        Assert.True(File.Exists(Path.Combine(project.Paths.Assets, "wood.png")));
        Assert.Contains(project.Assets.Entries, entry => entry.Name == "wood.png");
        Assert.Equal(1, imports());
        Assert.Equal(1, arrived);
    }

    /// <summary>A path already inside the project is refused and nothing is scanned or imported.</summary>
    [Fact]
    public void Import_refuses_what_the_project_already_holds() {
        var (library, project, imports) = Bare();

        var inside = Path.Combine(project.Paths.Assets, "here.png");
        File.WriteAllText(inside, "here");

        Assert.Equal(0, library.Import([inside], "Assets/Sub"));
        Assert.Equal(0, imports());
        Assert.Contains("here.png is already in this project", reported);
    }

    /// <summary>Collections and saved filters are one API, each written where its type says.</summary>
    /// <remarks>
    ///     ⚠ <b>The collection is in the project's settings file and the filter is in the
    ///     preferences</b> — the per-project and per-user split both types document — so asserting the
    ///     file for one and the write delegate for the other is the whole claim.
    /// </remarks>
    [Fact]
    public void Collections_and_filters_go_through_one_service() {
        var (library, project, _) = Bare();
        var changed = 0;

        library.CollectionsChanged += () => changed++;

        var asset = AssetId.New();

        library.AddToCollection("Rocks", [asset]);
        library.AddToCollection("Rocks", [asset]);

        var kept = Assert.Single(library.Collections);
        Assert.Equal([asset], kept.Assets);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(project.Settings.FileFor<AssetCollections>()));

        library.RemoveFromCollection("Rocks", [asset]);
        Assert.Empty(Assert.Single(library.Collections).Assets);

        library.ForgetCollection("Rocks");
        Assert.Empty(library.Collections);
        Assert.Equal(3, changed);

        library.SaveFilter("Textures", "rock", "TextureImporter");
        library.SaveFilter("Textures", "moss", "TextureImporter");

        var filter = Assert.Single(library.Filters);
        Assert.Equal("moss", filter.Search);
        Assert.Equal(2, filtersWritten);

        library.ForgetFilter("Textures");
        library.ForgetFilter("Textures");
        Assert.Empty(library.Filters);
        Assert.Equal(3, filtersWritten);
    }

    /// <summary>A package carries the closure and every sidecar, with no window.</summary>
    [Fact]
    public void Export_writes_the_closure_with_its_sidecars_without_a_window() {
        var (library, project, _) = Bare();

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Leaf.yaml"), "name: leaf\n");
        File.WriteAllText(Path.Combine(project.Paths.Assets, "Root.yaml"), "name: root\n");
        project.Assets.Scan();

        var leaf = project.Assets.Entries.Single(entry => entry.Name == "Leaf.yaml").Guid;
        var rootAsset = project.Assets.Entries.Single(entry => entry.Name == "Root.yaml").Guid;

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Root.yaml"), "uses: " + new AssetReference(leaf) + "\n");
        project.References.Build(project.Assets);

        var closure = library.Closure([rootAsset]);
        Assert.Equal([rootAsset, leaf], closure);

        var package = Path.Combine(root, "out" + ContentLibrary.PackageExtension);

        Assert.Equal(2, library.ExportPackage(package, closure));

        using var archive = ZipFile.OpenRead(package);

        Assert.Equal(
            ["Assets/Leaf.yaml", "Assets/Leaf.yaml.meta", "Assets/Root.yaml", "Assets/Root.yaml.meta"],
            archive.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal)
        );
    }

    /// <summary>Create writes a kind's starter file beside the selection, scans it in and announces it — with no window.</summary>
    /// <remarks>
    ///     ⚠ <b>The returned identity is the database's</b>, which is what the command opens the new
    ///     document under; a create that wrote the file and did not scan would return null here.
    /// </remarks>
    [Fact]
    public void Create_writes_scans_and_announces_without_a_window() {
        var (library, project, _) = Bare();
        var arrived = 0;

        library.Arrived += () => arrived++;

        Directory.CreateDirectory(Path.Combine(project.Paths.Assets, "Props"));
        File.WriteAllText(Path.Combine(project.Paths.Assets, "Props", "Crate.yaml"), "name: crate\n");
        project.Assets.Scan();
        project.Selection.Set([project.Assets.Entries.Single(entry => entry.Name == "Crate.yaml").Guid]);

        var kind = new NewAssetKind("test.create-thing", "Thing", ".thing", "New Thing", "name: thing\n");

        var first = library.Create(kind);
        var second = library.Create(kind);

        Assert.Equal("name: thing\n", File.ReadAllText(Path.Combine(project.Paths.Assets, "Props", "New Thing.thing")));
        Assert.True(project.Assets.TryGetByGuid(Assert.NotNull(first), out var made));
        Assert.Equal("Assets/Props/New Thing.thing", made.Path);
        Assert.True(project.Assets.TryGetByGuid(Assert.NotNull(second), out var again));
        Assert.Equal("Assets/Props/New Thing 2.thing", again.Path);
        Assert.Equal(2, arrived);
    }

    /// <summary>Referrers and dependencies walk one edge each way, which the closure does not.</summary>
    [Fact]
    public void Referrers_and_dependencies_walk_one_edge_without_a_window() {
        var (library, project, _) = Bare();

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Leaf.yaml"), "name: leaf\n");
        File.WriteAllText(Path.Combine(project.Paths.Assets, "Middle.yaml"), "name: middle\n");
        File.WriteAllText(Path.Combine(project.Paths.Assets, "Top.yaml"), "name: top\n");
        project.Assets.Scan();

        AssetId Id(string name) => project.Assets.Entries.Single(entry => entry.Name == name).Guid;

        var (leaf, middle, top) = (Id("Leaf.yaml"), Id("Middle.yaml"), Id("Top.yaml"));

        File.WriteAllText(Path.Combine(project.Paths.Assets, "Middle.yaml"), "uses: " + new AssetReference(leaf) + "\n");
        File.WriteAllText(Path.Combine(project.Paths.Assets, "Top.yaml"), "uses: " + new AssetReference(middle) + "\n");
        project.References.Build(project.Assets);

        Assert.Equal([middle], library.Referrers([leaf]));
        Assert.Equal([middle], library.Dependencies([top]));
        Assert.Equal([top], library.Referrers([middle, middle]));
        Assert.Empty(library.Referrers([top]));
        Assert.Equal([top, middle, leaf], library.Closure([top]));
    }

    /// <summary>The OS drop the platform delivers reaches the published service, and nothing beside it.</summary>
    /// <remarks>
    ///     ⚠ <b>By effect, through <c>PlatformInput.Dispatch</c></b> — the bridge the host feeds — and
    ///     the assertion is on the service a plugin gets from <c>Require&lt;IContentLibrary&gt;()</c>
    ///     raising <see cref="IContentLibrary.Arrived" />. A second copy of the copy-and-scan left in
    ///     the application would land the file and leave this red.
    /// </remarks>
    [Fact]
    public void An_os_drop_reaches_the_service_a_plugin_requires() {
        using var editor = EditorSession.Start();
        editor.Open("project");

        var library = editor.Plugins.Services.Require<IContentLibrary>();
        var arrived = 0;

        library.Arrived += () => arrived++;

        var bounds = editor.Panel("project").Bounds;
        var at = new System.Numerics.Vector2(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        var file = Outside("moss.png");

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropBegin, 1, 0, string.Empty, at)
        );

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropFile, 1, 0, file, at)
        );

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropComplete, 1, 0, string.Empty, at)
        );

        editor.Settle();

        Assert.True(File.Exists(Path.Combine(editor.Project.Paths.Assets, "moss.png")));
        Assert.Equal(1, arrived);
        Assert.Same(library.Project, editor.Project);
    }

    /// <summary>A plugin filing assets into a collection is what the browser's shelf shows.</summary>
    [Fact]
    public void A_collection_a_plugin_makes_is_on_the_browser_shelf() {
        using var editor = EditorSession.Start();
        editor.Open("project");

        var asset = editor.Project.Assets.Entries.First(entry => !entry.IsFolder).Guid;

        editor.Plugins.Services.Require<IContentLibrary>().AddToCollection("From a plugin", [asset]);
        editor.Settle();

        var folders = Descendants(editor.Panel("project"))
            .OfType<TreeView>()
            .Single(candidate => candidate.HasClass("browser-folders"));

        Assert.Contains("From a plugin", EditorSession.Labels(folders));
    }

    /// <summary>A rename keeps the asset's identity because the sidecar moves with it — with no window.</summary>
    /// <remarks>
    ///     ⚠ <b>The GUID is the assertion that matters.</b> A rename that moved the file and not its
    ///     <c>.meta</c> still puts a file of the new name in the index — under a freshly minted identity,
    ///     with every reference to the old one dangling and nothing reporting it.
    /// </remarks>
    [Fact]
    public void Rename_keeps_the_identity_and_its_sidecar_without_a_window() {
        var (library, project, _) = Bare();
        var rearranged = 0;

        library.Rearranged += () => rearranged++;

        var crate = Scanned(project, "Textures/crate.png");

        Assert.True(library.Rename(crate, "barrel").Ok);

        Assert.True(project.Assets.TryGetByGuid(crate, out var renamed));
        Assert.Equal("Assets/Textures/barrel.png", renamed.Path);
        Assert.True(File.Exists(Path.Combine(project.Paths.Assets, "Textures", "barrel.png.meta")));
        Assert.False(File.Exists(Path.Combine(project.Paths.Assets, "Textures", "crate.png.meta")));

        // The index on disk agrees, so the next launch reads the same identity back.
        var reopened = new EditorProject(new ProjectPaths(project.Paths.Root));
        reopened.Open();

        Assert.True(reopened.Assets.TryGetByGuid(crate, out var survived));
        Assert.Equal("Assets/Textures/barrel.png", survived.Path);

        // A refusal is reported, and the views are still told — the tree already shows what was typed.
        Scanned(project, "Textures/taken.png");

        Assert.False(library.Rename(crate, "taken").Ok);
        Assert.Equal("Could not rename", reported[^1]);
        Assert.Equal(2, rearranged);
    }

    /// <summary>Move carries sidecars and identities into the folder, and Delete takes both away — with no window.</summary>
    [Fact]
    public void Move_and_delete_carry_the_sidecars_without_a_window() {
        var (library, project, _) = Bare();
        var rearranged = 0;

        library.Rearranged += () => rearranged++;

        var crate = Scanned(project, "crate.png");
        var plank = Scanned(project, "plank.png");

        Directory.CreateDirectory(Path.Combine(project.Paths.Assets, "Props"));

        Assert.Equal(2, library.Move([crate, plank], "Assets/Props"));
        Assert.Equal("Moved to Props", reported[^1]);

        Assert.True(project.Assets.TryGetByGuid(crate, out var moved));
        Assert.Equal("Assets/Props/crate.png", moved.Path);
        Assert.True(File.Exists(Path.Combine(project.Paths.Assets, "Props", "plank.png.meta")));

        Assert.Equal(1, library.Delete([crate]));

        Assert.False(project.Assets.TryGetByGuid(crate, out _));
        Assert.False(File.Exists(Path.Combine(project.Paths.Assets, "Props", "crate.png")));
        Assert.False(File.Exists(Path.Combine(project.Paths.Assets, "Props", "crate.png.meta")));
        Assert.True(project.Assets.TryGetByGuid(plank, out _));
        Assert.Equal(2, rearranged);
    }

    /// <summary>A rename a plugin asks for redraws the open browser, and records nothing to undo.</summary>
    /// <remarks>
    ///     ⚠ <b>Not undoable, as the browser's rename never was</b> — <see cref="IContentLibrary.Rename" />
    ///     says so, and this holds it: Undo is exactly as available after the rename as before it.
    /// </remarks>
    [Fact]
    public void A_rename_a_plugin_asks_for_reaches_the_open_browser_and_is_not_undoable() {
        using var editor = EditorSession.Start();
        editor.Open("project");

        var library = editor.Plugins.Services.Require<IContentLibrary>();

        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "crate.png"), "pixels");
        editor.Run("assets.refresh");

        Assert.True(editor.Project.Assets.TryGetByPath("Assets/crate.png", out var crate));

        var couldUndo = editor.CanRun("edit.undo");

        Assert.True(library.Rename(crate.Guid, "barrel").Ok);
        editor.Settle();
        editor.ExpandAll(editor.Assets);

        Assert.Contains("barrel.png", EditorSession.Labels(editor.Assets));
        Assert.DoesNotContain("crate.png", EditorSession.Labels(editor.Assets));
        Assert.Equal(couldUndo, editor.CanRun("edit.undo"));
    }

    static AssetId Scanned(EditorProject project, string path) {
        var absolute = Path.Combine(project.Paths.Assets, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, path);
        project.Assets.Scan();

        return project.Assets.TryGetByPath("Assets/" + path, out var entry)
            ? entry.Guid
            : throw new InvalidOperationException($"'{path}' was not indexed.");
    }

    int filtersWritten;

    readonly List<string> reported = [];

    (ContentLibrary Library, EditorProject Project, Func<int> Imports) Bare() {
        Directory.CreateDirectory(Path.Combine(root, "Project", "Assets"));

        var project = new EditorProject(new ProjectPaths(Path.Combine(root, "Project")));
        project.Assets.Scan();

        List<SavedAssetFilter> filters = [];
        var imports = 0;

        var library = new ContentLibrary(
            project,
            (message, _, _) => reported.Add(message),
            () => filters,
            () => filtersWritten++,
            () => imports++
        );

        return (library, project, () => imports);
    }

    string Outside(string name) {
        var folder = Path.Combine(root, "Outside");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);
        File.WriteAllText(path, name);

        return path;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
