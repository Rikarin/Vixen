// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>What the outliner is looking at, which must outlive the panel: its filter and its order.</summary>
/// <remarks>
///     The workspace's per-view state for <c>"hierarchy"</c> (<c>IWorkspace.ViewState</c>, doc 50 § D2).
///     These were two fields on the application, parked there because a panel's factory runs again when
///     it is reopened — so a filter kept in the control would be silently forgotten by closing the panel,
///     which looks like the outliner spontaneously showing rows that were hidden.
/// </remarks>
sealed class OutlinerState {
    /// <summary>The orders the outliner offers, the first being the one it opens with.</summary>
    /// <remarks>
    ///     ⚠ <b>Hierarchy order is first and is the default.</b> It is the only one that carries
    ///     information the others destroy — the order of siblings is something the user arranged, and
    ///     a name sort on by default would hide that permanently and look like the arrangement not
    ///     having been saved.
    /// </remarks>
    public static readonly string[] Orders = ["Hierarchy order", "Name (A–Z)", "Name (Z–A)"];

    /// <summary>What the filter box says, or <see langword="null" /> for no filter.</summary>
    public string? Filter { get; set; }

    /// <summary>How siblings are ordered.</summary>
    public string Order { get; set; } = Orders[0];
}

/// <summary>The outliner: the active scene's entities as a tree, with the eye and the padlock beside each.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of the application, not rewritten (#1290).</b> <c>RebuildHierarchy</c>,
///         <c>MarkRow</c>, <c>SyncTreeSelection</c> and their neighbours were private methods on
///         <c>EditorApplication</c> reading <c>scene</c>, <c>world</c> and <c>Extensions</c>, and the
///         panel was registered by the application. The bodies are here unchanged but for what they
///         read: the active scene is <see cref="ISceneEditing.Active" />, the icons come from the
///         registry the host published, and the filter and order are the workspace's
///         <see cref="OutlinerState" />. <see cref="SceneModule" /> builds one and registers its panel,
///         so switching the module off takes the panel with it.
///     </para>
///     <para>
///         ⚠ <b>Told what changed through the service, not by the application setting a flag.</b> A
///         rebuild is <see cref="ISceneEditing.HierarchyChanged" /> — raised by the verbs, and by
///         <c>SceneEditing.Restructured</c> where the application changes the scene's shape itself — and
///         a restate of the two columns is <see cref="ISceneEditing.MarksChanged" />. Hide and lock stay
///         non-undoable, as decided on #1290.
///     </para>
/// </remarks>
sealed class SceneOutliner : IDisposable {
    /// <summary>The panel's id, which saved arrangements name.</summary>
    public const string PanelId = "hierarchy";

    readonly EditorShell shell;
    readonly ISceneEditing editing;
    readonly IEditorRegistry registry;
    readonly OutlinerState state;
    readonly string context;

    TreeView? view;
    ContextMenu? menu;
    bool stale = true;

    /// <summary>Whether the tree is being told about the selection rather than reporting one.</summary>
    bool echoing;

    /// <summary>Builds the outliner over the scene service.</summary>
    /// <param name="shell">The shell, for the context and the menu.</param>
    /// <param name="editing">The scene service: the active scene and what changes it.</param>
    /// <param name="registry">Where the row icons are contributed.</param>
    /// <param name="state">The filter and the order, which outlive the panel.</param>
    /// <param name="context">The shell context a press in the panel selects.</param>
    public SceneOutliner(EditorShell shell, ISceneEditing editing, IEditorRegistry registry, OutlinerState state, string context) {
        this.shell = shell;
        this.editing = editing;
        this.registry = registry;
        this.state = state;
        this.context = context;

        editing.HierarchyChanged += Restructured;
        editing.ActiveChanged += Restructured;
        editing.MarksChanged += Remarked;
    }

    /// <summary>Whether the panel is open: there is a tree to rename in or clear.</summary>
    public bool IsOpen => view is not null;

    SceneDocument Scene => editing.Active;

    /// <inheritdoc />
    public void Dispose() {
        editing.HierarchyChanged -= Restructured;
        editing.ActiveChanged -= Restructured;
        editing.MarksChanged -= Remarked;
    }

    void Restructured(ISceneEditing _) => stale = true;

    /// <summary>Rebuilds the rows on the next <see cref="Update" />, as a change to the hierarchy does.</summary>
    /// <remarks>What Search Everywhere's entity result asks for after selecting one (#1518).</remarks>
    public void Invalidate() => stale = true;

    void Remarked(ISceneEditing _) => RefreshMarks();

    /// <summary>Brings the tree up to date, once a frame: the rows when the scene's shape changed, the highlight always.</summary>
    /// <remarks>
    ///     ⚠ <b>The highlight every frame rather than only after a rebuild.</b> A selection made anywhere
    ///     but the tree — a viewport click, a command, an undo — changes nothing structural, so a sync that
    ///     only ran when the rows were rebuilt would leave the outliner highlighting whatever was clicked in
    ///     it last. Comparing a handful of handles is the same trade the application makes for the
    ///     selections themselves.
    /// </remarks>
    public void Update() {
        if (stale) {
            stale = false;
            RebuildHierarchy();
        }

        SyncTreeSelection();
    }

    /// <summary>Starts an in-place rename of an entity's row.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether its row was there to rename.</returns>
    /// <remarks>
    ///     Through the tree's own inline editor rather than a dialog, so that the menu line and F2 do
    ///     the same thing — and so the commit goes through <c>TreeView.Renamed</c>, which is already
    ///     wired to the document's undoable rename.
    /// </remarks>
    public bool BeginRename(Entity entity) {
        if (view is not { } tree) {
            return false;
        }

        foreach (var node in Descendants(tree.Root)) {
            if (node.Tag is Entity tagged && tagged == entity) {
                tree.BeginRename(node);
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops the selection through the tree, so no row keeps its highlight.</summary>
    /// <returns>Whether there was a tree to clear.</returns>
    /// <remarks>
    ///     The rows' highlight is the tree's own state and the document's selection is written from
    ///     it, so clearing only the document would leave a row that looks selected.
    /// </remarks>
    public bool ClearSelection() {
        if (view is not { } tree) {
            return false;
        }

        tree.Select(null);
        return true;
    }

    /// <summary>Builds the panel: the filter bar, the order, the tree and its menu.</summary>
    /// <param name="panel">The panel.</param>
    public void Build(DockPanel panel) {
        panel.WhenPressedIn(() => shell.Context = context);

        // ⚠ Above the tree and outside it, because a filter is about the panel rather than
        // about the control: the tree is a view of whatever it is handed, and what it is
        // handed is this outliner's decision. `TreeView` has no filter of its own for
        // the same reason it has no idea what an entity is.
        var bar = panel.Add<UiElement>("outliner-filters");

        var search = bar.Add<SearchBox>();
        search.Placeholder = "Filter by name…";

        // ⚠ Put back from the workspace's state, which is what makes the filter outlive the panel.
        search.Value = state.Filter ?? string.Empty;

        search.ValueChanged += (_, value) => {
            state.Filter = string.IsNullOrWhiteSpace(value) ? null : value;
            stale = true;
        };

        // ⚠ Hierarchy order is first and is the default, because it is the only one that
        // carries information the others destroy: the order of siblings is a thing the user
        // arranged, and a sort that is on by default would hide it permanently.
        var order = bar.Add<Select>();

        foreach (var mode in OutlinerState.Orders) {
            order.AddOption(mode);
        }

        order.Value = state.Order;

        order.SelectionChanged += (_, value) => {
            state.Order = value ?? OutlinerState.Orders[0];
            stale = true;
        };

        var tree = view = panel.Add<TreeView>();
        tree.MultiSelect = true;
        tree.AllowDrag = true;

        // ⚠ The two columns are added on bind rather than built with the row, because the
        // rows are pooled: thirty of them serve a scene of any size, so an eye appended per
        // call would add one per scrolled row for the life of the panel.
        tree.RowBound += MarkRow;

        tree.SelectionChanged += changed => {
            // ⚠ Ignored while the tree is being brought into line with the selection rather
            // than the other way round. Restoring the highlight after a rebuild raises this,
            // and writing it straight back would be the tree overwriting the very selection
            // it is being told about — which for a click in the viewport means the click is
            // undone on the next frame.
            if (echoing) {
                return;
            }

            List<Entity> picked = [];

            foreach (var node in changed.Selection) {
                if (node.Tag is Entity entity) {
                    picked.Add(entity);
                }
            }

            Scene.Selection.Set(picked);
        };

        tree.Renamed += (_, node, name) => {
            if (node.Tag is Entity entity) {
                Scene.Rename(entity, name);
            }
        };

        tree.Moved += (_, node) => Dropped(node);

        // ⚠ Double-click renames, and it is the control's own gesture rather than three
        // lines here. A row's name is the thing you edit in place, in this panel and in the
        // content browser both — see `TreeView.RenameOnActivate`, which is where the two
        // copies of it went. F2 still comes through `edit.rename`, so the keyboard and the
        // pointer cannot disagree about what a rename is.
        tree.RenameOnActivate = true;

        EditorApplication.Contextualise(tree, menu ??= HierarchyMenu());
        stale = true;
    }

    /// <summary>Forgets the tree when its panel closes.</summary>
    /// <remarks>
    ///     ⚠ <b>The tree goes with the panel; the outliner outlives it.</b> The application never let go
    ///     of its tree, so after the panel closed F2 stayed enabled over a removed tree and every
    ///     structural change rebuilt rows nobody could see.
    /// </remarks>
    public void Closed() => view = null;

    /// <summary>What is on the outliner's menu.</summary>
    ContextMenu HierarchyMenu() {
        var group = new MenuGroup(EditorStrings.MenuHierarchy);

        group.Add("scene.create-entity");
        EditorApplication.Creatable(group);

        group.AddSeparator();
        group.Add("edit.rename", "edit.delete");
        group.AddSeparator();
        group.Add("entity.toggle-hidden", "entity.toggle-lock");
        group.AddSeparator();
        group.Add("scene.focus");

        return MenuPresenter.Context(shell.Document, group, shell.Commands, shell.Keys);
    }

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }

    /// <summary>What a drag in the outliner meant, as an undoable move of the document.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tree has already moved the node by the time this runs, and the document has
    ///         not.</b> <c>TreeView</c> owns the gesture and its own rows; the scene owns what is
    ///         true. So this reads where the node landed, tells the document, and marks the tree
    ///         stale either way — a move the document refuses (a cycle, an entity already there)
    ///         would otherwise leave the outliner showing a hierarchy the scene does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole selection moves when the dragged row is part of it.</b> The same rule
    ///         the context menu follows, and for the same reason: dragging one of five selected rows
    ///         and having four of them stay behind is the behaviour nobody means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where it landed among its new siblings is read off the tree and passed on, and
    ///         until it was, half of this gesture did nothing.</b> <c>TreeView</c> has always
    ///         distinguished a drop <i>on</i> a row from one <i>between</i> two — that is what the
    ///         drop indicator draws — and this method read only <c>node.Parent</c>, so every drop
    ///         landed the entity at the head of its new parent's children. Doc 20 § Part D lists
    ///         "reorder among siblings" beside "reparent by drag" for that reason: the drag was
    ///         wired, and the position it carried was discarded one call short of the document.
    ///     </para>
    /// </remarks>
    void Dropped(TreeNode node) {
        if (node.Tag is not Entity moved) {
            return;
        }

        var parent = node.Parent is { Tag: Entity target } ? target : Entity.Null;
        var entities = Scene.Selection.Contains(moved) ? Scene.Selection.ToList() : [moved];

        Scene.Reparent(entities, parent, Preceding(node));
        stale = true;
    }

    /// <summary>The entity a moved row now sits behind, or none when it is first.</summary>
    /// <remarks>
    ///     ⚠ <b>Read after the tree has moved the node, which is the only moment it is knowable.</b>
    ///     <c>TreeView.MoveNode</c> raises <c>Moved</c> once the row is where the pointer put it, so
    ///     the row above it in its new parent is the sibling the document has to land behind — and a
    ///     <c>DropPosition</c> handed over instead would be a second encoding of the same fact for
    ///     this method to get wrong.
    /// </remarks>
    static Entity Preceding(TreeNode node) {
        if (node.Parent is not { } parent) {
            return Entity.Null;
        }

        var index = parent.IndexOf(node);

        return index > 0 && parent.Children[index - 1].Tag is Entity before ? before : Entity.Null;
    }

    void RebuildHierarchy() {
        if (view is not { } tree) {
            return;
        }

        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        // ⚠ One snapshot for the whole rebuild rather than one per row. `IEditorRegistry.All` takes
        // a lock and hands back a fresh array on every call, so asking it inside `Branch` was a
        // locked allocation per entity, and the binder behind each icon was resolved per entity as
        // well. A scene of a few thousand entities paid for both on every rename, every component
        // added and every undo — which is what made having the panel open feel expensive.
        var icons = GlyphSources();

        foreach (var entity in Ordered(Scene.Roots)) {
            Branch(tree.Root, entity);
        }

        // ⚠ Expanded by writing the flag and refreshing *once*, rather than by calling `Expand` per
        // root. `TreeView.Expand` refreshes on every call — the right shape for a person clicking a
        // chevron and the wrong one for a loop — so a scene with N roots flattened the whole tree
        // and realised its rows N+1 times. That is the O(n²) this method had.
        tree.Expand(tree.Root.Children);
        ShowSelectionInTree(tree);

        // ⚠ Whether a branch is kept is decided bottom-up: an entity survives the filter if it
        // matches, and a parent survives if any of its descendants did. A filter that dropped a
        // non-matching parent would take the matching child with it, which for a name typed into
        // an outliner is the one row the user was looking for.
        bool Branch(TreeNode parent, Entity entity) {
            var node = parent.Add(Scene.NameOf(entity), entity);

            node.Art = GlyphFor(icons, entity);

            var kept = Matches(entity);

            foreach (var child in Ordered(Children(entity))) {
                kept |= Branch(node, child);
            }

            if (!kept) {
                parent.Remove(node);
            }

            return kept;
        }
    }


    /// <summary>The registered icons that have a binder, most characteristic first.</summary>
    /// <remarks>
    ///     ⚠ <b>Resolved once per rebuild, which is what makes <see cref="GlyphFor" /> cheap.</b> The
    ///     registry copy, the binder lookup and the ordering are all facts about the *session* rather
    ///     than about an entity, and doing them per row turned an outliner refresh into a locked
    ///     allocation and a dictionary walk per entity. Sorted descending here so the first match
    ///     wins and the loop can stop.
    /// </remarks>
    List<(ISceneComponentBinder Binder, IconArt Art)> GlyphSources() {
        List<(ISceneComponentBinder, IconArt)> found = [];

        foreach (var icon in registry.All<TypeIcon>().OrderByDescending(entry => entry.Order)) {
            if (SceneComponentRegistry.TryGet(icon.Target, out var binder)) {
                found.Add((binder, icon.Art));
            }
        }

        return found;
    }

    /// <summary>The glyph an outliner row draws for what an entity is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What it carries rather than what it is called.</b> An outliner of forty identical
    ///         rows is one you read rather than scan, and a name is the one thing on the row that is
    ///         already text — an icon that repeated it would be decoration. A camera, a light and a
    ///         piece of geometry are the three things a scene is mostly made of, and telling them
    ///         apart at a glance is what the column is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked once per rebuild rather than on every bind.</b> The node keeps the answer,
    ///         and a rebuild is what a component being added or removed already triggers — so a row
    ///         scrolling past does not ask the world a question per registered icon per frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>From the registry rather than from a three-case switch, which is doc 36 § D6.</b>
    ///         The switch could only ever name components this assembly references, so an entity
    ///         carrying a plugin's component was a plain dot whatever it was. What decides now is which
    ///         registered icon's type the entity actually carries — asked through the binder, because
    ///         an archetype knows dense ids and this knows types, and <c>ISceneComponentBinder.Has</c>
    ///         is the only bridge between them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Highest <c>Order</c> first, so "most characteristic" is a declared thing.</b> An
    ///         entity with a camera and a light has to draw one of them, and the alternative to an
    ///         order is the registration sequence — which is whichever assembly happened to load first.
    ///     </para>
    /// </remarks>
    IconArt GlyphFor(List<(ISceneComponentBinder Binder, IconArt Art)> icons, Entity entity) {
        foreach (var (binder, art) in icons) {
            if (binder.Has(Scene.World, entity)) {
                return art;
            }
        }

        return EntityArt;
    }
    /// <summary>What a row with nothing else to say draws.</summary>
    /// <remarks>
    ///     Not a registration, because nothing keys it: it is the absence of every other answer rather
    ///     than the picture for a type.
    /// </remarks>
    static readonly IconArt EntityArt = IconArt.Of(EditorIcons.Entity);

    /// <summary>An entity's children, as a list a sort can be applied to.</summary>
    /// <remarks>
    ///     <c>Hierarchy.ChildrenOf</c> hands back a struct sequence rather than a list, which is
    ///     right for a walk that does not want an allocation and is the one thing a sort cannot be
    ///     done on. One list per branch, in a rebuild that only happens when the scene's shape
    ///     changed.
    /// </remarks>
    List<Entity> Children(Entity entity) {
        List<Entity> found = [];

        foreach (var child in Hierarchy.ChildrenOf(Scene.World, entity)) {
            found.Add(child);
        }

        return found;
    }

    /// <summary>Puts a set of siblings in whatever order the outliner is showing.</summary>
    /// <remarks>
    ///     ⚠ <b>Per level, not over the flattened tree.</b> Sorting an outliner is sorting each
    ///     parent's children — a global sort would put a child above its own parent, which is not a
    ///     tree.
    /// </remarks>
    IEnumerable<Entity> Ordered(IReadOnlyList<Entity> siblings) =>
        state.Order switch {
            "Name (A–Z)" => siblings.OrderBy(Scene.NameOf, StringComparer.CurrentCultureIgnoreCase),
            "Name (Z–A)" => siblings.OrderByDescending(Scene.NameOf, StringComparer.CurrentCultureIgnoreCase),
            _ => siblings
        };

    /// <summary>Puts the eye and the padlock on a row, and brings them up to date.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Made once per row and updated on every bind.</b> A virtualised tree rebinds its
    ///         rows as it scrolls, so a handler that added an element per call would add one per
    ///         scrolled row for the life of the panel. The two buttons are found by class on the row
    ///         they already belong to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row whose parent is hidden shows the eye off but dimmed.</b> It is off because
    ///         the entity is not being drawn, and dimmed because clicking it would do nothing — the
    ///         mark it would clear is on an ancestor. Showing it on would be a lie about what is on
    ///         screen; showing it plainly off would make the click look broken.
    ///     </para>
    /// </remarks>
    void MarkRow(TreeRow row, TreeNode node) {
        var eye = Mark(row, "outliner-hidden", EditorIcons.Eye, EditorIcons.EyeOff, "Hide");
        var padlock = Mark(row, "outliner-locked", ControlIcons.Unlock, ControlIcons.Lock, "Lock");

        if (node.Tag is not Entity entity) {
            eye.AddClass("hidden");
            padlock.AddClass("hidden");

            return;
        }

        eye.RemoveClass("hidden");
        padlock.RemoveClass("hidden");

        Restate(eye, Scene.IsHiddenDirectly(entity), Scene.IsHidden(entity));
        Restate(padlock, Scene.IsLockedDirectly(entity), Scene.IsLocked(entity));

        void Restate(ToggleButton button, bool directly, bool inherited) {
            button.IsChecked = directly;

            if (inherited && !directly) {
                button.AddClass("inherited");
            } else {
                button.RemoveClass("inherited");
            }

            // ⚠ Written here and not only from `CheckedChanged`. `IsChecked` above raises nothing
            // when the value it is given is the one it already had, and a pooled row rebound to a
            // different entity is exactly that case — the eye would keep the last entity's shape.
            Wear(button, directly || inherited);
        }
    }

    /// <summary>The toggle for one column, made on the row's first bind and reused after that.</summary>
    /// <remarks>
    ///     ⚠ <b>Two glyphs and no word, which is <c>InspectorView</c>'s padlock argument applied to
    ///     the column beside it.</b> These used to be a cross and a tick with "Hide" and "Lock"
    ///     written next to them — a label four times the button's width on every row of the outliner,
    ///     saying what pressing it <i>does</i> rather than what state it is in, which is the one thing
    ///     a toggle must not do. The shape carries the state now and the label stays for the tooltip
    ///     and the screen reader.
    /// </remarks>
    ToggleButton Mark(TreeRow row, string className, PathBuilder clear, PathBuilder marked, string label) {
        foreach (var child in row.Children) {
            if (child is ToggleButton existing && existing.HasClass(className)) {
                return existing;
            }
        }

        var button = row.Add<ToggleButton>();

        button.AddClass(className);
        button.LeadingIcon.Geometry = clear;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Label = label;
        button.TabIndex = -1;

        marks[button] = (clear, marked);

        // ⚠ Reads the row's *current* node rather than the one it was made for. The row outlives the
        // node by design — that is what pooling is — so a closure over the node would toggle
        // whatever entity happened to be in this slot when the panel first scrolled.
        button.CheckedChanged += (control, on) => {
            if (row.Node?.Tag is not Entity entity) {
                return;
            }

            if (className == "outliner-hidden") {
                Scene.SetHidden(entity, on);
            } else {
                Scene.SetLocked(entity, on);
            }

            // The descendants' rows show an inherited mark, so they have to be restated too — and
            // the selection may now hold something that cannot be picked.
            RefreshMarks();
            _ = control;
        };

        return button;
    }

    /// <summary>Which pair of glyphs a column's toggle draws, off and on.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the button rather than on it, because a <c>ToggleButton</c> has one icon slot
    ///     and no tag.</b> Bounded by the row pool — about thirty rows, two toggles each — for the
    ///     same reason the console's row map is.
    /// </remarks>
    readonly Dictionary<ToggleButton, (PathBuilder Clear, PathBuilder Marked)> marks = [];

    /// <summary>Puts the glyph for a state on a column's toggle.</summary>
    void Wear(ToggleButton button, bool marked) {
        if (marks.TryGetValue(button, out var pair)) {
            button.LeadingIcon.Geometry = marked ? pair.Marked : pair.Clear;
        }
    }

    /// <summary>Brings every realised row's two columns up to date.</summary>
    void RefreshMarks() {
        if (view is not { } tree) {
            return;
        }

        foreach (var row in tree.Rows) {
            if (row.Node is { } node) {
                MarkRow(row, node);
            }
        }
    }

    bool Matches(Entity entity) =>
        state.Filter is not { } filter
        || Scene.NameOf(entity).Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Brings the outliner's highlight into line with the document, when they differ.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20's "selection <i>into</i> the tree", which is the half that was missing.</b>
    ///     Clicking a row selected an entity; selecting an entity anywhere else — the viewport, a
    ///     command, an undo — left the outliner showing whatever had been clicked last, because the
    ///     highlight is the tree's own state and nothing wrote it.
    /// </remarks>
    void SyncTreeSelection() {
        if (view is not { } tree || stale) {
            return;
        }

        var shown = 0;
        var agrees = true;

        foreach (var node in tree.Selection) {
            shown++;

            if (node.Tag is not Entity entity || !Scene.Selection.Contains(entity)) {
                agrees = false;
                break;
            }
        }

        // Same count and every row selected is selected in the document: nothing to do, which is
        // every frame in which nobody clicked anything.
        if (agrees && shown == Scene.Selection.Count) {
            return;
        }

        ShowSelectionInTree(tree);
    }

    /// <inheritdoc cref="SyncTreeSelection" />
    void ShowSelectionInTree(TreeView tree) {
        if (Scene.Selection.Count == 0) {
            // ⚠ Guarded as well. Clearing the tree's rows raises `SelectionChanged` too, and letting
            // that through would write an empty selection into whichever document the inspector had
            // just been handed.
            echoing = true;

            try {
                tree.Select(null);
            } finally {
                echoing = false;
            }

            return;
        }

        // ⚠ Guarded, because `Select` raises `SelectionChanged` and the handler writes the tree's
        // selection back into the document. Without this, restoring a selection of three entities
        // would set the document's to one — the first row selected — and the other two would vanish.
        echoing = true;

        try {
            var first = true;

            foreach (var node in Descendants(tree.Root)) {
                if (node.Tag is Entity entity && Scene.Selection.Contains(entity)) {
                    tree.Select(node, first ? ModifierKeys.None : ModifierKeys.Control);
                    first = false;
                }
            }
        } finally {
            echoing = false;
        }
    }
}
