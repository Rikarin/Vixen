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

        tree = panel.Add<TreeView>();
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

        tiles = panel.Add<AssetGrid>();
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

    /// <summary>Shows the tile-size picker only when there are tiles to size.</summary>
    void Restate() {
        if (IsGrid) {
            sizes.RemoveClass("hidden");
        } else {
            sizes.AddClass("hidden");
        }
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
