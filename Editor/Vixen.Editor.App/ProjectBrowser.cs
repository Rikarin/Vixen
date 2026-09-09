// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The Project panel: what is in the asset database, as a tree somebody can click.</summary>
/// <remarks>
///     <para>
///         <b>The panel that turns a scanned project into something visible.</b> The database has been
///         built at startup since the shell existed — <c>EditorProject.Open</c> scans it, repairs the
///         sidecars and indexes every GUID — and until now nothing showed the result. This is the view
///         over it, and it is the seam every other "pick an asset" feature hangs off: the inspector's
///         asset picker, drag-and-drop into the scene, and the reverse-reference lookups all want a
///         browser to point at.
///     </para>
///     <para>
///         ⚠ <b>The shape is <see cref="AssetTree" />'s and not this class's</b>, which is what keeps
///         the ordering, the folder synthesis and the search testable without a document. What is left
///         here is genuinely a view: rows, selection, and when to rebuild.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt on demand, and the demand now also comes from the disk.</b> Nothing
///         <i>here</i> has a file-system watcher and nothing here should: what a watcher reports is a
///         fact about the project, not about a panel that may be closed. <c>EditorApplication</c>
///         owns one — see its <c>FollowDisk</c> — and drains it on the frame thread into the same
///         <see cref="Rescan" /> the Refresh command calls. The three things that used to be the
///         argument against having one are all <c>Vixen.Core.IO</c>'s and were before this panel
///         existed: <c>FileChangeCoalescer</c> debounces, collapses an atomic save's four events into
///         one, and can be told to ignore a write this program is about to make.
///     </para>
///     <para>
///         ⚠ <b>Every verb goes out as an event rather than being done here.</b> Renaming a file,
///         moving one and dropping one into the scene are all operations on the <i>project</i>, and a
///         browser that performed them would be the second place that knows how — the first being
///         <see cref="AssetOperations" />, which is where the sidecar invariant is written down and
///         tested.
///     </para>
/// </remarks>
sealed class ProjectBrowser {
    /// <summary>What the type filter offers when nothing is chosen.</summary>
    const string AnyType = "All types";

    readonly EditorProject project;
    readonly TreeView tree;
    readonly SearchBox search;
    readonly Select kinds;
    readonly ToggleButton grid;
    readonly Select sizes;
    readonly AssetGrid tiles;

    /// <summary>The button that drops the saved-filter menu.</summary>
    /// <remarks>
    ///     ⚠ <b>A menu behind one button rather than a fifth control in the bar, and the width is
    ///     the reason.</b> The bar already carries a search box, a kind dropdown, the view toggle and
    ///     the tile-size picker; a saved-filter dropdown beside them is the row that runs out of room
    ///     in a docked panel, which is the failure the inspector's own row hit. A menu also has
    ///     somewhere to put Save and Forget, which a dropdown does not.
    /// </remarks>
    readonly Button filters;

    /// <summary>The saved-filter menu, built on first use.</summary>
    /// <remarks>
    ///     ⚠ <b>Lazily, because a <c>ContextMenu</c> is an overlay and belongs to the document rather
    ///     than to the panel</b> — and the panel may not be in a document yet while its factory runs.
    ///     It is filled on every opening for <c>CurvePresetLines</c>'s reason: a menu filled once
    ///     holds the filters that existed when the panel was built, and the first thing anybody does
    ///     after saving one is look for it.
    /// </remarks>
    ContextMenu? filterMenu;

    /// <summary>The folders-only tree beside the grid.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second view over the same <c>AssetTree</c> rather than the first one turned
    ///         on, and doc 20 § B1 asks for exactly that: "a folder tree <i>beside</i> the grid".</b>
    ///         <see cref="tree" /> is the browsing surface in list mode and shows assets as well as
    ///         folders — its selection <i>is</i> the project's selection. This one shows folders
    ///         only and its selection <b>narrows</b> the grid instead. Building the second by
    ///         widening the first is how the two come to disagree about what is selected.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is the thing a user of either reference editor reaches for first.</b> The
    ///         breadcrumb above the grid answers "where am I" and does not answer "what else is
    ///         there", which is the question somebody has when they open a content browser.
    ///     </para>
    /// </remarks>
    readonly TreeView folders;

    /// <summary>Whether the folder tree is being brought into line rather than clicked in.</summary>
    /// <remarks>
    ///     ⚠ <b>Selecting a row raises <c>SelectionChanged</c>, and this panel writes that
    ///     selection from three places</b> — a rebuild, a grid navigation, and the click itself. An
    ///     unguarded restore would re-enter <see cref="Populate" /> from inside <see cref="Populate" />
    ///     on every rebuild, which is the round trip `ProjectBrowser.TileSize` already documents one
    ///     harmless instance of.
    /// </remarks>
    bool restoring;

    AssetTreeNode root;

    /// <summary>Which folder the grid is in, by path, so it survives a rescan.</summary>
    /// <remarks>
    ///     ⚠ <b>By path rather than by node.</b> A rescan rebuilds every <c>AssetTreeNode</c>, so a
    ///     held reference is to a folder that no longer exists — and the grid would come back at the
    ///     root every time somebody renamed anything.
    /// </remarks>
    string folder = AssetTree.RootName;

    /// <summary>Raised when a row is activated — a double-click, or Enter on the keyboard.</summary>
    /// <remarks>
    ///     What opens an asset. The browser deliberately does not open it itself: which editor claims
    ///     a file is <c>AssetEditorRegistry</c>'s and where the resulting document goes is the
    ///     workspace's, and a browser that knew both would be the third thing that has to be told
    ///     when either changes.
    /// </remarks>
    public event Action<AssetId>? Activated;

    /// <summary>Raised when a row's inline editor is committed.</summary>
    public event Action<AssetId, string>? Renamed;

    /// <summary>Raised when the user switches between the tree and the grid.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes the choice outlive the panel.</b> A panel's factory runs again every time
    ///     it is reopened, so the toggle is a fresh unchecked button on every visit — and the
    ///     application is the only thing that can hold the answer, because it is the only thing that
    ///     owns a preferences file. Reported rather than written here for the browser's own rule:
    ///     every verb goes out as an event.
    /// </remarks>
    public event Action<bool>? ViewChanged;

    /// <summary>Raised when the user asks to keep the filter that is set, under a name.</summary>
    /// <remarks>
    ///     ⚠ <b>The request rather than the filter, for the browser's own rule: every verb goes out
    ///     as an event.</b> Naming it means a modal prompt and keeping it means a preferences file,
    ///     and a panel that owned either would be a second writer to the user store. What the host
    ///     reads back is <see cref="Search" /> and <see cref="Kind" />, which are the filter.
    /// </remarks>
    public event Action? FilterSaveRequested;

    /// <summary>Raised when the user forgets a saved filter, by name.</summary>
    public event Action<string>? FilterForgotten;

    /// <summary>Raised when rows are dropped onto a folder row.</summary>
    public event Action<IReadOnlyList<AssetId>, AssetId>? Moved;

    /// <summary>Raised when a drag leaves the panel and is released somewhere else.</summary>
    /// <remarks>
    ///     ⚠ <b>The browser resolves the drop, because nothing else can.</b> A drag belongs to the
    ///     element the press landed on for its whole life — that is what makes it a drag rather than
    ///     a series of moves — so the panel the pointer is released <i>over</i> never hears about it.
    ///     What goes out is the assets and the point; what that point means is the application's,
    ///     since only it knows which panel is where.
    /// </remarks>
    public event Action<IReadOnlyList<AssetId>, float, float>? DroppedOutside;

    /// <summary>Raised while a drag is somewhere outside the panel, before it is released.</summary>
    /// <remarks>
    ///     ⚠ <b>What lets a drop be aimed.</b> <see cref="DroppedOutside" /> is the verb and this is
    ///     the feedback: an inspector row is twenty pixels tall, and a person dragging a mesh onto the
    ///     right one of four asset fields needs the target to say so while the pointer is still down.
    ///     The point is <see cref="float.NaN" /> when the drag has come back over the panel or was
    ///     cancelled, because "no longer anywhere" has to be reportable or the last thing highlighted
    ///     stays highlighted.
    /// </remarks>
    public event Action<IReadOnlyList<AssetId>, float, float>? DraggedOutside;

    /// <summary>Raised when files are dropped onto the panel from outside the editor.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The paths and the folder they landed in, because where a drop lands is what
    ///         chooses the destination.</b> Doc 20 § B3 asks an import to choose a destination, and a
    ///         drag says it with the pointer: dropping a folder of textures on <c>Textures/</c> means
    ///         that folder, and there is no dialog in the world that says it faster. What the browser
    ///         will not do is the import itself — copying into <c>Assets/</c> and rescanning is the
    ///         application's, for the same reason every other verb here goes out as an event.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An in-app drag is not this.</b> <see cref="DropEvent.Files" /> is empty for a
    ///         drag begun inside the editor — a row dragged out of this very panel arrives as one —
    ///         and importing an asset the project already holds would copy a file over itself.
    ///     </para>
    /// </remarks>
    public event Action<IReadOnlyList<string>, string>? FilesDropped;

    /// <summary>Raised when a pointer goes down on the panel, and again when it comes up.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this there is no such thing as dragging an asset into an inspector
    ///         field, and the reason is two panels deep.</b> Pressing a row selects it, a selected
    ///         asset wins the inspector from whatever entity had it, and the panel is rebuilt — so by
    ///         the time the pointer has moved far enough to be a drag at all, the field it was aimed
    ///         at no longer exists. The gesture was not merely awkward; it was impossible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The press rather than the drag, and the difference is a frame that matters.</b> A
    ///         drag does not begin until the pointer has passed the slop threshold, which is several
    ///         frames after the press — and the inspector is handed over on the first of them. What
    ///         the application does with this is suspend that hand-over until the gesture is over,
    ///         which is the rule every editor follows without saying so: <i>a drag is not a click</i>.
    ///     </para>
    ///     <para>
    ///         Captured rather than bubbled, because the tree and the grid both handle their own
    ///         presses and a bubbling handler behind them would hear about only the ones that landed
    ///         on nothing.
    ///     </para>
    /// </remarks>
    public event Action<bool>? Grabbing;

    /// <summary>What has been contributed, which is where the pictures come from.</summary>
    IEditorRegistry Extensions { get; }

    /// <summary>Builds the panel's contents into a container.</summary>
    /// <param name="project">The project being browsed.</param>
    /// <param name="panel">Where to put the rows.</param>
    /// <param name="extensions">
    ///     What has been contributed, for the pictures. A constructor argument and not a settable
    ///     property, because this constructor populates both views before it returns — a registry
    ///     assigned afterwards would arrive one panel too late, which is the shape of mistake
    ///     <c>EditorSession</c> already made once with <c>init</c>.
    /// </param>
    public ProjectBrowser(EditorProject project, UiElement panel, IEditorRegistry extensions) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(extensions);

        this.project = project;
        Extensions = extensions;
        root = AssetTree.Build(project.Assets.Entries);

        // ⚠ On the panel and in the capture phase, so it is heard before the tree and the grid
        // decide what the press meant — see `Grabbing`. A release that lands outside the panel still
        // arrives here, because a press captures the pointer and the route is built from the element
        // it captured to.
        panel.AddHandler<PointerEvent>(
            (_, args) => {
                switch (args.Action) {
                    case PointerAction.Pressed:
                        Grabbing?.Invoke(true);
                        break;

                    case PointerAction.Released:
                        Grabbing?.Invoke(false);
                        break;

                    default:
                        break;
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

        // ⚠ The first consumer of an OS drop in this repository, and the panel is the target rather
        // than a row: a file dragged out of Finder lands wherever the pointer is, which is as often
        // the empty space below the last row as it is on a folder. The route bubbles, so a drop on a
        // row arrives here anyway and `FolderAt` reads what it landed on.
        panel.AllowDrop = true;

        panel.AddHandler<DropEvent>(
            (_, args) => {
                if (args.Files.Count == 0) {
                    return;
                }

                // Handled, so a drop is imported once. Nothing above this is listening today, and
                // the day something is, two copies of a hundred textures is not the failure to find
                // out that way.
                args.Handled = true;
                FilesDropped?.Invoke(args.Files, FolderAt(args.X, args.Y));
            }
        );

        var bar = panel.Add<UiElement>("browser-filters");

        // A `SearchBox` rather than a plain field, so the clear button and the magnifier come from
        // the control that already has them rather than from a panel drawing its own.
        search = bar.Add<SearchBox>();
        search.Placeholder = "Search assets";
        search.ValueChanged += (_, _) => Populate();

        // ⚠ The importer tag rather than the extension, and the list is what the project actually
        // holds rather than everything the engine can import. A dropdown offering nine formats in a
        // project with two of them is a filter that mostly narrows to nothing, and a filter that
        // narrows to nothing is one people stop using after the second time.
        kinds = bar.Add<Select>();
        kinds.SelectionChanged += (_, _) => Populate();

        // ⚠ A toggle rather than two panels, and the two views share everything behind them: the
        // search box, the type filter, the selection and the verbs. A grid with a filter of its own
        // would be a second browser that disagrees with the first about what is in the project.
        grid = bar.Add<ToggleButton>();
        grid.Label = "Grid";
        grid.Size = ControlSize.Small;
        grid.Variant = ControlVariant.Subtle;
        grid.AddClass("browser-view");

        grid.CheckedChanged += (_, on) => {
            Populate();
            Restate();
            ViewChanged?.Invoke(on);
        };

        // ⚠ In the bar and not in the assets' context menu, because a filter is a property of the
        // panel rather than of whatever row was right-clicked — and because the menu over a row has
        // to stay the verbs that act on the selection. See `filters`.
        filters = bar.Add<Button>();
        filters.Label = "Filters";
        filters.Size = ControlSize.Small;
        filters.Variant = ControlVariant.Subtle;
        filters.AddClass("browser-filter-menu");
        filters.Clicked += _ => OpenFilters();

        // ⚠ A row holding the folder tree and whichever browsing surface is showing. The two views
        // were direct children of the panel, which is a column — so a folder tree added beside them
        // there would have been a strip *above* the grid rather than next to it. See `browser-body`
        // in `BrowserTheme.vcss`; an element no stylesheet mentions lays its children out across,
        // which is what this one wants and is said out loud all the same.
        var body = panel.Add<UiElement>("browser-body");

        tree = body.Add<TreeView>();
        tree.MultiSelect = true;
        tree.AllowDrag = true;

        // ⚠ Double-click opens and a second click on the selected row renames, which is the pair
        // every file manager ships and the reason the two are separate properties on the control. A
        // browser whose double-click renamed would have no gesture left for the thing a browser is
        // for; the outliner is the other way round, because a row there is a name rather than a
        // document — see `TreeView.RenameOnSecondClick`.
        tree.RenameOnSecondClick = true;

        tree.Activated += (_, node) => {
            // ⚠ Only what the database has an identity for, and never a folder — the same rule the
            // selection follows, for the same reason.
            if (node.Tag is AssetTreeNode { IsIndexed: true, IsFolder: false } asset) {
                Activated?.Invoke(asset.Guid);
            }
        };

        tree.SelectionChanged += changed => {
            List<AssetId> picked = [];

            foreach (var node in changed.Selection) {
                // ⚠ Only what the database has an identity for. A folder synthesised because its
                // sidecar was never written has no GUID, and putting `AssetId.Empty` in the selection
                // would make every such folder select the same nothing — and look like one asset.
                if (node.Tag is AssetTreeNode { IsIndexed: true } asset) {
                    picked.Add(asset.Guid);
                }
            }

            project.Selection.Set(picked);
        };

        tree.Renamed += (_, node, name) => {
            if (node.Tag is AssetTreeNode { IsIndexed: true } asset) {
                Renamed?.Invoke(asset.Guid, name);
            }
        };

        // ⚠ The tree has already moved the row by the time this runs and the disk has not, so the
        // handler reads where it landed, tells the application, and lets the rescan put the rows
        // back where the disk says they are. A move the file system refuses would otherwise leave
        // the browser showing a folder that does not contain what it is drawing.
        // ⚠ On the tree as well as the grid, and it has to be the *source* that watches for this:
        // a drop outside the panel never reaches the panel it landed on.
        tree.AddHandler<DragEvent>((_, args) => {
            var outside = !Inside(tree, args.X, args.Y);

            // ⚠ However it ended. A cancelled drag — a window losing focus mid-gesture — produces no
            // release, so a host that learned "the gesture is over" from the pointer alone would
            // never learn it, and what that suspends is the inspector following the selection.
            if (args.Stage is DragStage.Completed or DragStage.Cancelled) {
                Grabbing?.Invoke(false);
            }

            switch (args.Stage) {
                case DragStage.Completed when outside:
                    Escaped(args.X, args.Y);
                    break;

                case DragStage.Started or DragStage.Moved when outside:
                    Hovering(args.X, args.Y);
                    break;

                case DragStage.Started or DragStage.Moved or DragStage.Cancelled:
                    Hovering(float.NaN, float.NaN);
                    break;

                default:
                    break;
            }
        });

        tree.Moved += (_, node) => {
            if (node.Parent?.Tag is AssetTreeNode { IsIndexed: true, IsFolder: true } folder) {
                Moved?.Invoke(Dragged(node), folder.Guid);
            } else {
                Populate();
            }
        };

        tiles = body.Add<AssetGrid>();
        tiles.Containing = Containing;
        tiles.Art = Art;
        tiles.Picture = Pictured;
        tiles.Navigated += entered => {
            folder = entered.Path;
            Populate();
        };

        tiles.Selected += node => project.Selection.Set(node.IsIndexed ? [node.Guid] : []);
        tiles.DroppedOutside += (x, y) => Escaped(x, y);
        tiles.DraggedOutside += (x, y) => Hovering(x, y);
        tiles.DragEnded += () => Grabbing?.Invoke(false);

        tiles.Activated += node => {
            if (node.IsIndexed) {
                Activated?.Invoke(node.Guid);
            }
        };

        // ⚠ Built last and drawn first, which is `order: -1` in the stylesheet rather than an index
        // here. Its handler writes to the grid, so a lambda closing over a field the constructor has
        // not reached yet is a null the compiler is right to complain about — and putting the
        // element first to fix that would make `Descendants(panel).OfType<TreeView>().First()` the
        // *folder* tree, which is how the harness and three existing tests reach the browsing one.
        // The layout has implemented `order` all along; see `.component-icon` in the same sheet for
        // the other place this argument is made.
        //
        // ⚠ Single-select and no drags: it is a place to stand rather than a thing to act on, and a
        // drop onto it would be a second, disagreeing answer to "where does this file go" — `tree`
        // already takes those.
        folders = body.Add<TreeView>();
        folders.AddClass("browser-folders");

        folders.SelectionChanged += changed => {
            if (restoring) {
                return;
            }

            if (changed.Selection.FirstOrDefault()?.Tag is AssetTreeNode { IsFolder: true } chosen) {
                folder = chosen.Path;
                Populate();
            }
        };

        // ⚠ Built after the grid and put in the bar all the same, because its handler writes to the
        // grid — a lambda closing over a field the constructor has not reached yet is a null the
        // compiler is right to complain about. It lands beside the view toggle, and is hidden with
        // it: a control that stays on screen and does nothing teaches people it is broken.
        sizes = bar.Add<Select>();
        sizes.Size = ControlSize.Small;
        sizes.AddClass("browser-tile-size");

        foreach (var size in AssetGrid.TileSizes) {
            sizes.AddOption(size.Name, size.Name);
        }

        sizes.Value = tiles.TileSize;

        sizes.SelectionChanged += (_, value) => {
            if (value is not { Length: > 0 } chosen) {
                return;
            }

            tiles.TileSize = chosen;
            TileSizeChanged?.Invoke(chosen);
        };

        Refilter();
        Populate();
        Restate();
    }

    /// <summary>Shows the tile-size picker and the folder tree only when there are tiles.</summary>
    /// <remarks>
    ///     ⚠ <b>The folder tree is the grid's, not the panel's.</b> In list mode <see cref="tree" />
    ///     already shows the folders — it is the browsing surface — so a second folders-only column
    ///     beside it would be the same information twice, with two selections to keep in step. What
    ///     the grid has and the list does not is a view with no hierarchy in it at all.
    /// </remarks>
    void Restate() {
        if (IsGrid) {
            sizes.RemoveClass("hidden");
            folders.RemoveClass("hidden");
        } else {
            sizes.AddClass("hidden");
            folders.AddClass("hidden");
        }
    }

    /// <summary>The folder tree, for the panel that holds it and for the harness.</summary>
    public TreeView Folders => folders;

    /// <summary>Which folder the grid is showing, by path.</summary>
    /// <remarks>
    ///     ⚠ <b>A path rather than a node, for <see cref="folder" />'s reason</b>: a rescan rebuilds
    ///     every <c>AssetTreeNode</c>, so a held reference names a folder that no longer exists.
    /// </remarks>
    public string Folder => folder;

    /// <summary>Which folder a point in the panel means, for a drop that has to land somewhere.</summary>
    /// <param name="x">Where, in document space.</param>
    /// <param name="y">Where, in document space.</param>
    /// <returns>A project-relative folder path, never empty.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The row under the pointer decides, and an <i>asset</i> row means the folder that
    ///         holds it.</b> Nobody aiming at <c>wood.png</c> means "inside wood.png"; they mean the
    ///         folder they can see it in. Walking up from whatever was hit answers both cases with
    ///         one rule.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the fallback is the folder being shown rather than the root.</b> A drop on
    ///         the empty space under the last tile is the commonest aim of all — the tiles fill the
    ///         top of the panel and the space below them is most of it — and answering <c>Assets/</c>
    ///         there would put files somewhere the user is not looking, which is the mistake nobody
    ///         notices until the build.
    ///     </para>
    /// </remarks>
    public string FolderAt(float x, float y) {
        foreach (var view in new[] { folders, tree }) {
            if (view.HasClass("hidden") || view.NodeAt(x, y) is not { } hit) {
                continue;
            }

            for (var node = hit; node is not null; node = node.Parent) {
                if (node.Tag is AssetTreeNode { IsFolder: true } under) {
                    return under.Path;
                }
            }
        }

        return string.IsNullOrEmpty(folder) ? AssetTree.RootName : folder;
    }

    /// <summary>Rebuilds the folders-only tree and puts the mark back on the folder being shown.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unfiltered, deliberately, and it is the one thing here the search does not touch.</b>
    ///         This column answers "what else is there", and a tree that shrank to the folders
    ///         holding matches would answer "where are the matches" — which is what the grid beside
    ///         it is already saying. A folder tree that moves while somebody types is one they
    ///         cannot aim at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The restore is guarded.</b> Selecting a row raises <c>SelectionChanged</c>, whose
    ///         handler calls <see cref="Populate" /> — so without <see cref="restoring" /> every
    ///         rebuild would re-enter the rebuild it is inside.
    ///     </para>
    /// </remarks>
    void Trunk() {
        var open = Expanded(folders);

        restoring = true;

        try {
            while (folders.Root.Children.Count > 0) {
                folders.Root.Remove(folders.Root.Children[^1]);
            }

            Only(folders.Root, root);
            folders.Refresh();

            TreeNode? showing = null;

            foreach (var node in Descendants(folders.Root)) {
                if (node.Tag is not AssetTreeNode { IsFolder: true } asset) {
                    continue;
                }

                // The root and whatever the user had open, by path — a folder that has gone simply
                // does not match, which is the right answer rather than a special case.
                if (open.Count == 0 ? asset.Path == AssetTree.RootName : open.Contains(asset.Path)) {
                    folders.Expand(node);
                }

                if (string.Equals(asset.Path, folder, StringComparison.Ordinal)) {
                    showing = node;
                }
            }

            // ⚠ Every ancestor of the shown folder, so a grid navigated three deep by double-click
            // is a mark somebody can see rather than one inside a collapsed branch.
            for (var walk = showing; walk is not null; walk = walk.Parent) {
                folders.Expand(walk);
            }

            folders.Select(showing);
        } finally {
            restoring = false;
        }
    }

    /// <summary>Adds a node's folders and nothing else.</summary>
    static void Only(TreeNode parent, AssetTreeNode asset) {
        if (!asset.IsFolder) {
            return;
        }

        var node = parent.Add(asset.Name, asset);

        node.Art = StandardIcons.Folder;

        foreach (var child in asset.Children) {
            Only(node, child);
        }
    }

    /// <summary>Which of a tree's folders are open, by path.</summary>
    static HashSet<string> Expanded(TreeView view) {
        HashSet<string> open = new(StringComparer.Ordinal);

        foreach (var node in Descendants(view.Root)) {
            if (node.IsExpanded && node.Tag is AssetTreeNode { IsFolder: true } asset) {
                open.Add(asset.Path);
            }
        }

        return open;
    }

    /// <summary>Whether the grid is showing rather than the tree.</summary>
    public bool IsGrid {
        get => grid.IsChecked;
        set => grid.IsChecked = value;
    }

    /// <summary>How big the grid's tiles are, by the name the dropdown shows.</summary>
    /// <remarks>
    ///     ⚠ <b>A name rather than a number, for the reason the layout presets are names.</b> A tile
    ///     is a width, a height, a glyph size and a caption height that have to agree — a free number
    ///     would let somebody ask for a 40-pixel tile with a 40-pixel glyph in it — and a name is
    ///     what a preferences file can hold across a version that changes the sizes.
    /// </remarks>
    public string TileSize {
        get => tiles.TileSize;

        set {
            tiles.TileSize = value;

            // ⚠ Through the control, so the dropdown shows what was restored. Assigning `Value`
            // raises `SelectionChanged`, which writes it back to the same place — harmlessly, and
            // it is why the grid is set first rather than left to that round trip.
            sizes.Value = tiles.TileSize;
        }
    }

    /// <summary>What is in the search box.</summary>
    public string Search => search.Value ?? string.Empty;

    /// <summary>The importer tag the kind dropdown is on, or empty for every kind.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty rather than <c>All types</c>, because the dropdown's own line is a label and
    ///     not a tag.</b> A saved filter that stored the label would come back as a filter for assets
    ///     whose importer is called "All types", which is a filter that matches nothing — silently,
    ///     since an empty grid is what a narrow filter looks like.
    /// </remarks>
    public string Kind => kinds.Value is { } chosen && chosen != AnyType ? chosen : string.Empty;

    /// <summary>Where the saved filters come from, asked at the moment the menu opens.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A function rather than the list, and holding the list would have been a bug
    ///         rather than a style.</b> <c>EditorPreferences</c> is <i>replaced</i> — the settings
    ///         window's Revert re-reads the file into a new object, and so does a restart — so a
    ///         panel that had captured <c>preferences.AssetFilters</c> would go on offering the
    ///         filters that existed when it was opened, silently, for as long as it stayed open. It
    ///         is the same shape as <c>CurvePresetLines</c>'s <c>Func&lt;InspectorRow?&gt;</c>: asked
    ///         at the moment it is needed, because that is the only moment the answer is known to be
    ///         current.
    ///     </para>
    ///     <para>
    ///         Null while nothing has offered any, which is a browser with no saved filters rather
    ///         than an error — the panel is constructible without a preferences file, and the
    ///         harness builds one that way.
    ///     </para>
    /// </remarks>
    public Func<IReadOnlyList<SavedAssetFilter>>? SavedFilters { get; set; }

    /// <summary>Puts a filter into the two controls that are the filter.</summary>
    /// <param name="query">What to put in the search box.</param>
    /// <param name="kind">The importer tag, or empty for every kind.</param>
    /// <remarks>
    ///     ⚠ <b>Through the controls rather than around them, which is what makes an applied filter
    ///     visible.</b> A filter applied to <see cref="Populate" /> alone would narrow the grid while
    ///     the search box sat empty — a browser showing a fifth of the project with nothing on screen
    ///     saying why, which is the state people restart the editor to get out of.
    ///     <para>
    ///         ⚠ A kind the project no longer holds falls back to every kind, because
    ///         <see cref="Refilter" /> only offers the tags the project actually has. That is the
    ///         same answer the dropdown already gives when the last texture is deleted, and it is
    ///         better than a filter that hides everything with no way to tell why.
    ///     </para>
    /// </remarks>
    public void Apply(string query, string kind) {
        search.Value = query ?? string.Empty;

        kinds.Value = !string.IsNullOrEmpty(kind) && kinds.Options.Any(option => option.Value == kind)
            ? kind
            : AnyType;

        Populate();
    }

    /// <summary>Drops the saved-filter menu under the button, filled from what the host has given.</summary>
    void OpenFilters() {
        var menu = filterMenu ??= filters.Document.Root.Add<ContextMenu>();

        // ⚠ Closed before it is emptied. An open menu has focus inside it, and removal is final in
        // this framework — taking the focused item out from under the focus is the shape of thing
        // that leaves a document pointing at a slot somebody else has been given.
        if (menu.IsOpen) {
            menu.Close();
        }

        while (menu.Children.Count > 0) {
            menu.Children[^1].Remove();
        }

        var save = menu.AddItem("Save Filter…");

        // ⚠ Nothing set is nothing to save. A filter of "" over every kind is the browser's resting
        // state, and a menu that offered to name it would be offering to keep a row that does
        // nothing when it is applied.
        save.Disabled = Search.Length == 0 && Kind.Length == 0;
        save.Clicked += _ => FilterSaveRequested?.Invoke();

        var offered = SavedFilters?.Invoke() ?? [];

        if (offered.Count > 0) {
            menu.AddSeparator();

            foreach (var saved in offered) {
                var line = menu.AddItem(saved.Name);
                var query = saved.Search;
                var kind = saved.Kind;

                line.Clicked += _ => Apply(query, kind);
            }

            var forget = menu.AddSubmenu("Forget Filter");

            foreach (var saved in offered) {
                var line = forget.AddItem(saved.Name);
                var name = saved.Name;

                line.Clicked += _ => FilterForgotten?.Invoke(name);
            }
        }

        var bounds = filters.Bounds;

        menu.OpenAt(bounds.X, bounds.Y + bounds.Height);
    }

    /// <summary>Raised when the tile size is changed, so that the choice can outlive the panel.</summary>
    /// <inheritdoc cref="ViewChanged" select="remarks" />
    public event Action<string>? TileSizeChanged;

    /// <summary>The grid, for the panel that holds it.</summary>
    public AssetGrid Grid => tiles;

    /// <summary>How many rows the tree is showing.</summary>
    public int Count => tree.Root.Children.Count;

    /// <summary>The tree, for the harness and for the panel that holds it.</summary>
    public TreeView Tree => tree;

    /// <summary>Reports a drag released outside the panel, with whatever it was carrying.</summary>
    void Escaped(float x, float y) {
        List<AssetId> carried = [.. project.Selection];

        if (carried.Count > 0) {
            DroppedOutside?.Invoke(carried, x, y);
        }
    }

    /// <summary>Reports where a drag has got to, so a target can say it would take it.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported even when the selection is empty, unlike <see cref="Escaped" />.</b> A drag
    ///     that begins on nothing still has to clear whatever the previous one lit up, and a guard
    ///     that swallowed it here would leave a field outlined until the next successful drop.
    /// </remarks>
    void Hovering(float x, float y) => DraggedOutside?.Invoke([.. project.Selection], x, y);

    static bool Inside(UiElement element, float x, float y) {
        var bounds = element.Bounds;

        return x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;
    }

    /// <summary>Brings the grid's marks into line with the project's selection.</summary>
    /// <remarks>
    ///     ⚠ <b>Pushed once a frame rather than only after a click in the grid.</b> Selecting an
    ///     asset anywhere else — the inspector's picker, a command, the tree — leaves the tiles
    ///     showing whatever was clicked in them last, which is the same failure the outliner had.
    /// </remarks>
    public void SyncSelection() {
        if (IsGrid) {
            tiles.Mark(project.Selection);
        }
    }

    /// <summary>Deselects everything.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the tree rather than through <c>EditorProject.Selection</c>.</b> The rows'
    ///     highlight is the tree's own state and the document's selection is written <i>from</i> it —
    ///     so clearing the far end alone leaves a row that looks selected, and the next click on it
    ///     is a click on something the tree already thinks is picked. Clearing here raises
    ///     <c>SelectionChanged</c>, which is what empties the project's selection.
    /// </remarks>
    public void Deselect() => tree.Select(null);

    /// <summary>Opens the inline editor on an asset's row.</summary>
    /// <param name="asset">Which asset.</param>
    /// <returns>Whether a row for it is on screen to edit.</returns>
    public bool BeginRename(AssetId asset) {
        foreach (var node in Descendants(tree.Root)) {
            if (node.Tag is AssetTreeNode { IsIndexed: true } found && found.Guid == asset) {
                tree.BeginRename(node);
                return true;
            }
        }

        return false;
    }

    /// <summary>Rescans the project and rebuilds the tree.</summary>
    /// <returns>What the scan found, for whoever is reporting it.</returns>
    /// <remarks>
    ///     ⚠ <b>The index is saved after the scan.</b> It lives in <c>Library/</c> and is what makes
    ///     the <i>next</i> launch skip the walk; a rescan that left the old one on disk would make
    ///     the editor slower the more often it was refreshed, which is the wrong way round.
    /// </remarks>
    public ScanReport Rescan() {
        var report = project.Assets.Scan();

        project.Assets.Save();
        // ⚠ Rebuilt with it. The reverse index is what answers "what would break if I deleted this",
        // and one built against the previous scan answers it about assets that have moved.
        project.References.Build(project.Assets);

        Rebuild();
        return report;
    }

    /// <summary>Rebuilds the rows from the database, keeping the filters.</summary>
    /// <remarks>
    ///     What an operation that has already rescanned calls. <see cref="Rescan" /> is the whole
    ///     round trip and doing it twice for one rename is two walks of the project.
    /// </remarks>
    public void Rebuild() {
        root = AssetTree.Build(project.Assets.Entries);

        Refilter();
        Populate();
    }

    /// <summary>Brings the type dropdown into line with what the project actually holds.</summary>
    void Refilter() {
        var chosen = kinds.Value;

        kinds.ClearOptions();
        kinds.AddOption(AnyType);

        foreach (var tag in project.Assets.Entries
            .Where(entry => !entry.IsFolder && !string.IsNullOrEmpty(entry.ImporterTag))
            .Select(entry => entry.ImporterTag!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)) {
            kinds.AddOption(tag);
        }

        // ⚠ Kept if it still exists. Deleting the last texture in a project must not silently widen
        // a filter somebody set — but a filter naming an importer nothing uses any more would hide
        // everything with no way to tell why.
        kinds.Value = chosen is not null && kinds.Options.Any(option => option.Value == chosen) ? chosen : AnyType;
    }

    /// <summary>The assets a drag is carrying: the whole selection when it includes the dragged row.</summary>
    /// <remarks>
    ///     The same rule the outliner follows for entities, and for the same reason: dragging one of
    ///     five selected rows and having four of them stay behind is the behaviour nobody means.
    /// </remarks>
    List<AssetId> Dragged(TreeNode node) {
        if (node.Tag is not AssetTreeNode { IsIndexed: true } asset) {
            return [];
        }

        return tree.Selection.Contains(node) ? [.. project.Selection] : [asset.Guid];
    }

    /// <summary>Which folders are open, by path.</summary>
    HashSet<string> Opened() {
        HashSet<string> open = new(StringComparer.Ordinal);

        foreach (var node in Descendants(tree.Root)) {
            if (node.IsExpanded && node.Tag is AssetTreeNode { IsFolder: true } asset) {
                open.Add(asset.Path);
            }
        }

        return open;
    }

    /// <summary>Rebuilds whichever view is showing, from the tree and the two filters.</summary>
    void Populate() {
        var shown = AssetTree.Filter(root, search.Value);
        var kind = kinds.Value is { } value && value != AnyType ? value : null;

        // ⚠ Before either view, and for both of them. The column is hidden in list mode rather than
        // unbuilt, so that switching to tiles shows a tree that is already in the right place — a
        // panel that builds its left-hand column on the frame you first look at it is one that
        // flashes empty.
        Trunk();

        if (IsGrid) {
            tree.AddClass("hidden");
            tiles.RemoveClass("hidden");

            var kept = Prune(shown, kind) ?? shown;

            // ⚠ Falls back to whatever survives rather than showing an empty grid. A folder can go
            // — deleted, renamed, filtered out — and a browser sitting in one that no longer exists
            // is one with no way back to anything.
            tiles.Show(AssetTree.Find(kept, folder) ?? kept);
            tiles.Mark(project.Selection);

            return;
        }

        tree.RemoveClass("hidden");
        tiles.AddClass("hidden");

        // ⚠ Read before the rows are thrown away, and this is what makes a rescan survivable. The
        // tree is rebuilt from scratch on every rebuild — a rename, an import, and now a file
        // somebody saved from another program — and until this existed each of those closed every
        // folder the user had opened. That was tolerable while a rebuild only followed something
        // they had just done; with a watcher behind it, the project tree collapsed by itself.
        var open = Opened();

        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        Branch(tree.Root, shown, kind);
        tree.Refresh();

        // ⚠ By path rather than by node, for the reason `folder` is: a rebuild makes a new
        // `AssetTreeNode` for everything, so a remembered reference is to a folder that no longer
        // exists. A path that has gone — the folder was deleted or renamed — simply does not match,
        // which is the right answer rather than a special case.
        foreach (var node in Descendants(tree.Root).ToList()) {
            if (node.Tag is AssetTreeNode { IsFolder: true } asset && open.Contains(asset.Path)) {
                tree.Expand(node);
            }
        }

        // The root and its immediate folders, so a project opens showing something. Deeper than that
        // is the user's business — and a search has already narrowed to what matched, so opening
        // everything it kept is what makes a result visible without a click.
        foreach (var node in tree.Root.Children) {
            tree.Expand(node);

            if (!string.IsNullOrWhiteSpace(search.Value) || kind is not null) {
                Reveal(node);
            }
        }

        void Reveal(TreeNode node) {
            tree.Expand(node);

            foreach (var child in node.Children) {
                Reveal(child);
            }
        }
    }

    /// <summary>Adds an asset and its children, dropping the branches the type filter empties.</summary>
    /// <remarks>
    ///     ⚠ <b>Decided bottom-up, the same way the outliner's name filter is.</b> A folder survives
    ///     because something under it did; dropping a folder whose own tag does not match would take
    ///     every matching file inside it with it, which for a filter is the one row somebody was
    ///     looking for.
    /// </remarks>
    bool Branch(TreeNode parent, AssetTreeNode asset, string? kind) {
        var node = parent.Add(asset.Name, asset);

        // The same picture the grid draws, from the same resolution. See `Art`.
        node.Art = Art(asset);

        var kept = kind is null || (!asset.IsFolder && Tagged(asset, kind));

        foreach (var child in asset.Children) {
            kept |= Branch(node, child, kind);
        }

        if (!kept) {
            parent.Remove(node);
        }

        return kept;
    }

    /// <summary>The same filter the tree applies, as a tree rather than as rows.</summary>
    /// <remarks>
    ///     ⚠ <b>Bottom-up, exactly as <see cref="Branch" /> is.</b> A folder survives because
    ///     something under it did — dropping one whose own tag does not match would take every
    ///     matching file inside it, which for a filter is the one thing somebody was looking for.
    /// </remarks>
    AssetTreeNode? Prune(AssetTreeNode asset, string? kind) {
        if (kind is null) {
            return asset;
        }

        List<AssetTreeNode> kept = [];

        foreach (var child in asset.Children) {
            if (Prune(child, kind) is { } survivor) {
                kept.Add(survivor);
            }
        }

        if (kept.Count == 0 && (asset.IsFolder || !Tagged(asset, kind))) {
            return null;
        }

        return asset with { Children = kept };
    }

    /// <summary>What contains a node, for the grid's breadcrumbs.</summary>
    AssetTreeNode? Containing(AssetTreeNode node) {
        var slash = node.Path.LastIndexOf('/');

        return slash <= 0 ? null : AssetTree.Find(root, node.Path[..slash]);
    }

    /// <summary>Where the pictures come from, when the host can make any.</summary>
    /// <remarks>
    ///     ⚠ <b>Subscribed to, because a picture arrives after the tile that wanted it was drawn.</b>
    ///     A decode takes a few frames; without this the grid would show glyphs until something else
    ///     happened to make it rebind, which for a folder somebody is looking at is never.
    /// </remarks>
    public ThumbnailCache? Thumbnails {
        get;

        set {
            if (field is not null) {
                field.Changed -= Rebind;
            }

            field = value;

            if (field is not null) {
                field.Changed += Rebind;
            }
        }
    }

    void Rebind() {
        if (IsGrid) {
            tiles.Refresh();
        }
    }

    /// <summary>The picture for an asset, asking for one if there is none yet.</summary>
    ulong Pictured(AssetTreeNode asset) =>
        asset.IsIndexed && Thumbnails is { } cache && cache.TryGet(asset.Guid, out var image) ? image : 0;

    /// <summary>Which importer claims an asset, for its glyph.</summary>
    string? Importer(AssetTreeNode asset) =>
        asset.IsIndexed && project.Assets.TryGetByGuid(asset.Guid, out var entry) ? entry.ImporterTag : null;

    /// <summary>The picture for an asset, which both of this panel's views draw.</summary>
    /// <remarks>
    ///     ⚠ <b>One method, and that is the whole of F12's fix.</b> The grid asked the database what
    ///     claimed a file and drew a coloured glyph for it; the tree drew a folder or a generic page,
    ///     with a remark saying a tile was large enough for the answer to be worth reading and a row
    ///     was not. It was not a size decision — the same asset was two different things in two panes
    ///     of one panel, which is the kind of disagreement nobody reports as a bug and everybody
    ///     notices.
    /// </remarks>
    IconArt Art(AssetTreeNode asset) {
        if (asset.IsFolder) {
            return StandardIcons.Folder;
        }

        return EditorArt.Of(Extensions.All<AssetIcon>(), Importer(asset), asset.Name) ?? StandardIcons.Unknown;
    }

    bool Tagged(AssetTreeNode asset, string kind) =>
        asset.IsIndexed
        && project.Assets.TryGetByGuid(asset.Guid, out var entry)
        && string.Equals(entry.ImporterTag, kind, StringComparison.Ordinal);

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
