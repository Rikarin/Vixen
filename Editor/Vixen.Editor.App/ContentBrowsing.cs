// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The Project panel's model: the browser while it is open, its context menu, and the two verbs that ask a name.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2's rule for a panel's state, applied to the content browser (#1294).</b> The
///         registration, the browser field, the context menu over its two views and the two prompts
///         behind its Save Filter and New Collection buttons were <c>EditorApplication</c>'s. They are
///         this model's now; the application builds it once and registers the panel it describes.
///     </para>
///     <para>
///         ⚠ <b>What stays the application's arrives as delegates, and it is the arbitration.</b> A
///         double-click opens a tab (<c>AssetTabs</c>), a drop outside lands in a scene or a field, and
///         a press holds off the inspector's selection-following for the length of a drag — each one
///         decides between this panel and another, which is the composition root's job under § D2.
///         What the panel reads of the library, the source-control column and the thumbnails is
///         handed in as the objects themselves.
///     </para>
/// </remarks>
sealed class ContentBrowsing {
    /// <summary>The Project panel's id, which a saved arrangement carries it back by.</summary>
    public const string PanelId = "project";

    readonly EditorShell shell;
    readonly string context;
    readonly IProjectSession session;
    readonly EditorProject project;
    readonly IContentLibrary library;
    readonly IEditorRegistry extensions;
    readonly Func<string, SourceControlStatus> status;
    readonly ThumbnailCache thumbnails;
    readonly Func<IEnumerable<string>> creatable;
    readonly Action<AssetId> open;
    readonly Action<IReadOnlyList<AssetId>, float, float> dropped;
    readonly Action<IReadOnlyList<AssetId>, float, float> dragging;
    readonly Action<bool> grabbing;

    /// <summary>What a secondary click in either view opens, made the first time the panel opens.</summary>
    ContextMenu? menu;

    /// <summary>Builds the model over what the application holds.</summary>
    /// <param name="shell">The shell, for the context, the prompts and the menu.</param>
    /// <param name="context">The context a press in the panel claims.</param>
    /// <param name="session">The project session the browser reads the database through.</param>
    /// <param name="project">The project, whose selection a right-click on a tile writes.</param>
    /// <param name="library">The content library the browser and the two prompts act through.</param>
    /// <param name="extensions">This editor's registry.</param>
    /// <param name="status">Doc 20 § B7's column, asked per path.</param>
    /// <param name="thumbnails">The pictures the grid draws.</param>
    /// <param name="creatable">The Create ▸ kinds, asked when the menu is built.</param>
    /// <param name="open">What a double-click on an asset does — the application's tab opener.</param>
    /// <param name="dropped">What a drop outside the browser does.</param>
    /// <param name="dragging">What a drag outside the browser does while it is still moving.</param>
    /// <param name="grabbing">Told when a press in the browser starts or ends a possible drag.</param>
    public ContentBrowsing(
        EditorShell shell,
        string context,
        IProjectSession session,
        EditorProject project,
        IContentLibrary library,
        IEditorRegistry extensions,
        Func<string, SourceControlStatus> status,
        ThumbnailCache thumbnails,
        Func<IEnumerable<string>> creatable,
        Action<AssetId> open,
        Action<IReadOnlyList<AssetId>, float, float> dropped,
        Action<IReadOnlyList<AssetId>, float, float> dragging,
        Action<bool> grabbing
    ) {
        this.shell = shell;
        this.context = context;
        this.session = session;
        this.project = project;
        this.library = library;
        this.extensions = extensions;
        this.status = status;
        this.thumbnails = thumbnails;
        this.creatable = creatable;
        this.open = open;
        this.dropped = dropped;
        this.dragging = dragging;
        this.grabbing = grabbing;
    }

    /// <summary>The browser while the Project panel is open, or <see langword="null" />.</summary>
    public ProjectBrowser? Browser { get; private set; }

    /// <summary>Opens the browser's inline editor on an asset's row, if the panel is open and the row is there.</summary>
    /// <param name="asset">Which asset — a file or a folder.</param>
    /// <returns>Whether an editor opened.</returns>
    /// <remarks>
    ///     ⚠ <b>What Rename and New Folder are names for (#1292, #1516)</b>, and the reason this model is
    ///     published to <c>LibraryModule</c>: the rename itself is <see cref="IContentLibrary.Rename" />,
    ///     reached from the tree's commit, and only the in-place editor is this panel's.
    /// </remarks>
    public bool BeginRename(AssetId asset) => Browser?.BeginRename(asset) ?? false;

    /// <summary>Describes the panel.</summary>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor Describe() =>
        new(
            PanelId,
            EditorStrings.PanelProject,
            panel => {
                panel.WhenPressedIn(() => shell.Context = context);

                var browser = new ProjectBrowser(session, panel, library, extensions);

                browser.Activated += open;
                browser.DroppedOutside += dropped;
                browser.DraggedOutside += dragging;

                // ⚠ Doc 20 § B7's column, given to the panel as a value. The sweep that fills it
                // follows the session's rescans and the startup, not this panel (#1505).
                browser.Status = status;
                browser.Grabbing += grabbing;
                browser.Thumbnails = thumbnails;

                // ⚠ Doc 20 § B1's saved filters and collections are the library's, which the panel
                // reads and writes itself (#1304) — asked on every read, because `preferences` and
                // the project's settings object are both REPLACED by a Revert. What comes back
                // here is only what needs a name typed first.
                browser.FilterSaveRequested += KeepFilter;

                // ⚠ Both views, and the grid was the one that had nothing. The menu was attached to
                // the tree alone, so switching to tiles — which is the view somebody browses
                // *assets* in — left right-click doing nothing at all: no Create, no Import, no
                // Rename, no Show in Explorer. One menu over two elements, because the verbs act on
                // the project's selection and both views write it.
                menu ??= Menu();

                EditorApplication.Contextualise(browser.Tree, menu);
                Contextualise(browser.Grid, menu);

                browser.CollectionCreateRequested += MakeCollection;

                Browser = browser;
            }
        ) {
            // ⚠ The other half of holding it, and the crash it prevents is not hypothetical.
            // `Update` calls `SyncSelection` every frame, which marks the grid's tiles — and a
            // tile whose panel has been closed has been removed from the document, so asking it
            // for its state throws. It went unnoticed for as long as the browser opened as a
            // tree, because the tree's own highlight is written through `TreeView` and the grid
            // was the half nothing touched. See `PanelDescriptor.Closed`.
            //
            // It is also what the null checks behind `browser is not null` have always meant:
            // the enablement of Rename, Reveal and Select Dependencies is "the Project panel is
            // open", and while this was never cleared the answer was "it has been open once".
            Closed = () => Browser = null
        };

    /// <summary>Asks for a name and keeps the browser's current filter under it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The query is read at the moment the name comes back rather than when the prompt
    ///         opens, and either would have been defensible — but the browser is behind a modal, so
    ///         the two cannot differ.</b> Reading it here keeps the one rule that matters: what is
    ///         saved is what the bar says, and never a filter assembled from two different moments.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A name that is already taken replaces rather than doubling the menu</b>, which is
    ///         <c>CurvePresetLibrary.Save</c>'s decision restated: somebody who saves "Textures"
    ///         twice has said what they mean by it, and two identical lines on a menu is the answer
    ///         nobody wants.
    ///     </para>
    /// </remarks>
    void KeepFilter() {
        if (Browser is null) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var typed = await shell.Dialogs.PromptAsync(
                "Save Filter",
                "It is kept beside your layouts and keymap rather than in the project.",
                confirm: "Save"
            ).ConfigureAwait(true);

            if (typed is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name) || Browser is null) {
                return;
            }

            library.SaveFilter(name.Trim(), library.Shown.Search, library.Shown.Kind);
        }
    }

    /// <summary>Asks for a name and puts the assets into a collection under it.</summary>
    /// <param name="assets">What to seed it with, which may be nothing.</param>
    /// <remarks>
    ///     ⚠ <b>The assets are read before the prompt and the name after it</b>, which is
    ///     <see cref="KeepFilter" />'s bargain the other way round and for the same reason: the
    ///     browser is behind a modal, so the two moments cannot differ — and what a drag was carrying
    ///     is a fact about the gesture that ended, not about whatever is selected when the dialog
    ///     closes.
    /// </remarks>
    void MakeCollection(IReadOnlyList<AssetId> assets) {
        _ = Ask();

        async Task Ask() {
            var typed = await shell.Dialogs.PromptAsync(
                "New Collection",
                "It is kept with the project, under ProjectSettings, and holds asset ids rather than paths.",
                confirm: "Create"
            ).ConfigureAwait(true);

            if (typed is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name)) {
                return;
            }

            library.AddToCollection(name.Trim(), assets);
        }
    }

    /// <summary>The same menu, over the content browser's tiles.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection is written from the press for
    ///     <see cref="EditorApplication.Contextualise(TreeView, ContextMenu)" />'s reason</b> — every
    ///     line on the menu acts on it — and a press on the background clears it, so Create makes a
    ///     file in the folder being looked at rather than beside whatever was clicked last. The grid's
    ///     own <c>Selected</c> event cannot do this job: it fires for a primary press, and a
    ///     right-click that did not also select would open a menu about a different asset.
    /// </remarks>
    void Contextualise(AssetGrid grid, ContextMenu menu) {
        grid.AddHandler<PointerEvent>(
            (_, args) => {
                if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Secondary }) {
                    return;
                }

                if (grid.TileAt(args.X, args.Y)?.Node is { IsIndexed: true } asset) {
                    if (!project.Selection.Contains(asset.Guid)) {
                        project.Selection.Set([asset.Guid]);
                    }

                    return;
                }

                project.Selection.Set([]);
            },

            // Before the menu's own handler and regardless of it having marked the event handled,
            // which is the tree's arrangement and for the same reason: the commands the menu enables
            // are decided from the selection this writes.
            handledEventsToo: true
        );

        menu.Attach(grid);
    }

    /// <summary>What a secondary click in the content browser opens.</summary>
    /// <remarks>
    ///     ⚠ <b>Every line is a registered command, so this menu and the Assets menu on the bar
    ///     cannot disagree.</b> That is the point of the registry rather than a nicety: the two
    ///     menus, the palette and the keymap are four views over one list, and a browser that built
    ///     its own verbs would be the place where Delete means something different.
    /// </remarks>
    ContextMenu Menu() {
        var group = new MenuGroup(EditorStrings.MenuBrowser);

        group.Add("assets.open");
        group.AddSeparator();

        // ⚠ The same Create submenu the Assets menu on the bar carries, from the same ids — which is
        // what the registry is for and is why this is three lines rather than a second list. A
        // browser whose context menu could make a folder but not a material is one where the seven
        // asset kinds are reachable only from a menu at the top of the window, several inches from
        // the folder somebody has just navigated into and right-clicked in.
        group.AddSubmenu(EditorStrings.MenuCreate)
            .Add("assets.new-folder", "assets.create")
            .AddSeparator()
            .AddDynamic(creatable);

        group.Add("assets.import-files");
        group.AddSeparator();
        group.Add("assets.rename", "assets.delete", "assets.move-to");
        group.AddSeparator();
        group.Add("assets.reimport-all", "assets.bake-mesh-maps", "assets.show-in-explorer");

        return MenuPresenter.Context(shell.Document, group, shell.Commands, shell.Keys);
    }
}
