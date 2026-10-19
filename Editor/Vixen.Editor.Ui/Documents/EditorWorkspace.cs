// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Ui;

namespace Vixen.Editor.Ui.Documents;

/// <summary>Where a document is shown: the registered views, and the one call that builds one into a panel.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D4's Workspace half.</b> <c>IDocumentHost.TryOpen</c> resolves the kind and
///         hands back a document; <see cref="Show" /> resolves its view. A module reaches it with
///         <c>Require&lt;IWorkspace&gt;()</c> to register a view, or to hear one built with
///         <see cref="Shown" /> — which is how a module joins its own views to its own services
///         without the application naming either.
///     </para>
///     <para>
///         <b>And the arrangement's own verbs</b> (#1294): where a document's panel goes
///         (<see cref="Place" />), keeping the arrangement (<see cref="SaveLayout" />) and the theme
///         the user has written over the shipped one (<see cref="ThemeTokens" />,
///         <see cref="ReloadStyles" />). Those were <c>EditorApplication</c>'s, and they are the
///         Workspace context's words — a panel id, a layout, a stylesheet — so this is their home.
///     </para>
///     <para>
///         ⚠ <b>Not § F1's other parked fields, and not for want of moving them.</b> This assembly
///         refuses <c>Vixen.Editor.Core</c> on purpose, and the browser, the outliner, the inspectors
///         and the viewport's cameras are types of <c>Vixen.Editor.App</c>,
///         <c>.Inspector</c> and <c>.SceneView</c>, which a Workspace interface here cannot name. Doc
///         50 gives each its own context's model (<c>IContentLibrary.Shown</c>,
///         <c>IDiagnostics.Console</c>, the viewport's views) rather than this one.
///     </para>
/// </remarks>
public interface IWorkspace {
    /// <summary>Every registered view, in registration order.</summary>
    IReadOnlyList<DocumentView> Views { get; }

    /// <summary>Raised after <see cref="Show" /> built a view, with the view's root and its document.</summary>
    event Action<UiElement, IEditableDocument>? Shown;

    /// <summary>Registers a view, and hands back the removal.</summary>
    /// <param name="view">The view.</param>
    /// <returns>A scope that takes it out again — a plugin's registration scope keeps it.</returns>
    IDisposable Add(DocumentView view);

    /// <summary>Finds the view for a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="view">The view.</param>
    /// <returns>Whether anything shows it.</returns>
    /// <remarks>
    ///     The document's own type first and then each base in turn, and under one type the views in
    ///     the order they were registered; the first whose <see cref="DocumentView.Accepts" /> says yes.
    /// </remarks>
    bool TryGetView(IEditableDocument document, [NotNullWhen(true)] out DocumentView? view);

    /// <summary>Finds a view registered for a type or for one of its bases, without a document to ask.</summary>
    /// <param name="documentType">The document type.</param>
    /// <param name="view">The first view found.</param>
    /// <returns>Whether any view is registered for it.</returns>
    bool TryGetView(Type documentType, [NotNullWhen(true)] out DocumentView? view);

    /// <summary>Builds a document's view into a panel, which then hosts the document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="panel">Where it goes.</param>
    /// <returns>The view's root, or <see langword="null" /> when no view shows this document.</returns>
    /// <remarks>
    ///     ⚠ <b>The panel hosts the document</b> — <see cref="UiElement.HostedDocument" /> and
    ///     <see cref="DocumentCommands.Install(UiElement, Action)" /> — so <c>document.save</c>,
    ///     <c>document.revert</c> and <c>document.close</c> answer for the tab the focus is in, which is
    ///     #656's port reaching the editor; Ctrl+S, Ctrl+W and the File menu ask it (#1494). A panel is
    ///     built fresh each time it opens, so installing on it once is right.
    /// </remarks>
    UiElement? Show(IEditableDocument document, UiElement panel);

    /// <summary>Opens an asset in a panel of its own — the document a double-click opens, in the tab a double-click brings forward.</summary>
    /// <param name="asset">Which asset.</param>
    /// <returns>Whether anything opened it; a host that could not says why on screen.</returns>
    /// <remarks>
    ///     <b>What a view asks when it links to another asset</b> — a material's "Open shader graph",
    ///     a group picked from the Addressables list (#1297). Each used to be an event the application
    ///     subscribed to by naming the view's type; a module answers it now, and the panel the asset
    ///     lands in is the host's.
    /// </remarks>
    bool Open(AssetId asset);

    /// <summary>The documents recently opened in a tab or saved, by location, newest first (#656).</summary>
    /// <remarks>
    ///     <c>Vixen.Ui</c>'s list, keyed on <see cref="IEditableDocument.Location" />: <see cref="Show" />
    ///     notes every document it builds a view for, and a host notes its own documents — the editor
    ///     tracks its scene, whose location moves with Save As. Kept in the user's store between runs.
    /// </remarks>
    RecentDocuments Recent { get; }

    /// <summary>Puts a document's panel where documents go — beside the scene — before it is opened.</summary>
    /// <param name="panelId">The panel.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The cause of a graph vanishing into a 320 px column was the placement and not the
    ///         stylesheet.</b> A panel the arrangement does not name went wherever the host put new
    ///         panels; a document belongs where the viewport is, which is what every preset's
    ///         <em>centre</em> means. An arrangement already naming the panel, or with no scene panel
    ///         in it, keeps the host's own answer.
    ///     </para>
    ///     <para>
    ///         Before the panel is opened, because the placement happens the moment its id is
    ///         assigned — a group named in the layout is one the host leaves alone.
    ///     </para>
    /// </remarks>
    void Place(string panelId);

    /// <summary>Keeps the arrangement on screen in the user's store, named after the preset it came from.</summary>
    /// <returns>The name it was kept under.</returns>
    string SaveLayout();

    /// <summary>The theme tokens the user has written over the shipped palette, as the YAML they are kept in.</summary>
    /// <remarks>
    ///     Read once and kept, because the Appearance page that shows it is built again on every
    ///     visit — and its search box alone can bounce between two pages, which would make typing in
    ///     it a sequence of synchronous disk reads. <see cref="ReloadStyles" /> drops the copy.
    /// </remarks>
    string ThemeTokens { get; }

    /// <summary>Keeps new theme tokens and restyles the editor with them; an empty text is the shipped palette.</summary>
    /// <param name="text">The tokens.</param>
    /// <returns>Whether they could be written; a host that could not says why on screen.</returns>
    bool ApplyThemeTokens(string text);

    /// <summary>Reads the theme tokens again and restyles the editor, so a hand edit of the file shows without a restart.</summary>
    /// <remarks>
    ///     ⚠ <b>The kept copy is dropped with it.</b> The Appearance page exists to edit that file, so
    ///     a reload that left it showing the old text would be an editor disagreeing with itself about
    ///     what the theme is.
    /// </remarks>
    void ReloadStyles();
}

/// <summary>The editor's <see cref="IWorkspace" />: a view list keyed by document type, and the shell's arrangement.</summary>
/// <remarks>
///     ⚠ <b>Opening an asset in a tab is still the host's, handed in.</b> It resolves the document
///     through <c>IDocumentHost</c> and the editor's open scenes, which are <c>Vixen.Editor.Core</c>'s
///     words this assembly refuses; the panel id, its placement and the arrangement are here.
/// </remarks>
public sealed class EditorWorkspace : IWorkspace {
    /// <summary>What a panel showing an asset's editor is called in an arrangement.</summary>
    /// <remarks>
    ///     The GUID rather than the path, so that moving the file does not leave a panel nobody can
    ///     reopen — the identity is a GUID precisely so that it does not.
    /// </remarks>
    public const string AssetPrefix = "asset.";

    /// <summary>Where the user's theme overrides live, in the user's store.</summary>
    public const string ThemeFile = "theme.yaml";

    readonly List<DocumentView> views = [];
    readonly Func<AssetId, bool>? open;
    readonly EditorShell? shell;
    readonly EditorUserStore? store;
    readonly RecentDocuments recent = new();
    string? tokens;

    /// <summary>A view list with no shell behind it — what a test or a sweep over the standard views builds.</summary>
    /// <param name="open">
    ///     How the host puts an asset in a panel of its own, or <see langword="null" /> for one with no
    ///     panels — whose <see cref="Open" /> then answers <see langword="false" />.
    /// </param>
    /// <remarks>
    ///     ⚠ The arrangement's verbs need a shell and a store, and throw
    ///     <see cref="InvalidOperationException" /> here rather than answer as if they had one.
    /// </remarks>
    public EditorWorkspace(Func<AssetId, bool>? open = null) {
        this.open = open;
    }

    /// <summary>The workspace of a running editor.</summary>
    /// <param name="shell">The shell whose arrangement this is.</param>
    /// <param name="store">The user's store, which keeps layouts and the theme.</param>
    /// <param name="open">How the host puts an asset in a panel of its own.</param>
    public EditorWorkspace(EditorShell shell, EditorUserStore store, Func<AssetId, bool>? open = null) : this(open) {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(store);

        this.shell = shell;
        this.store = store;

        // ⚠ Restored before it is listened to, so start-up does not write back the file it just read.
        if (store.Read(EditorUserStore.RecentDocumentsFile) is { } kept) {
            recent.Restore(kept.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        recent.Changed += Keep;
    }

    /// <inheritdoc />
    public RecentDocuments Recent => recent;

    /// <summary>How the host reverts a document from its tab, or <see langword="null" /> for the document's own revert.</summary>
    /// <remarks>
    ///     ⚠ <b>The editor's asks first</b> (#1494): <c>document.revert</c> in a tab is Revert to Saved,
    ///     which has always put a question before throwing away the only copy of somebody's edits, and
    ///     the framework's answer has no dialog to ask with.
    /// </remarks>
    public Action<IEditableDocument>? Reverting { get; set; }

    /// <summary>The id of the panel an asset's editor is shown in.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>Its panel id.</returns>
    public static string PanelOf(AssetId asset) => AssetPrefix + asset;

    /// <summary>Reads an asset panel's id back, which is what restoring an arrangement needs.</summary>
    /// <param name="id">A panel id.</param>
    /// <param name="asset">The asset it shows.</param>
    /// <returns>Whether it is an asset panel's id.</returns>
    /// <remarks>
    ///     ⚠ <b>Beside the formatter, because a format written in one place and parsed in another is
    ///     a format with two owners.</b> The parse is the half a saved layout depends on, and it was
    ///     the half that could drift silently: a mismatch does not fail, it just never restores a
    ///     document.
    /// </remarks>
    public static bool TryReadPanel(string id, out AssetId asset) {
        ArgumentNullException.ThrowIfNull(id);

        asset = default;

        return id.StartsWith(AssetPrefix, StringComparison.Ordinal)
            && AssetId.TryParse(id[AssetPrefix.Length..], out asset);
    }

    /// <inheritdoc />
    public IReadOnlyList<DocumentView> Views => views;

    /// <inheritdoc />
    public event Action<UiElement, IEditableDocument>? Shown;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">This view is already registered.</exception>
    public IDisposable Add(DocumentView view) {
        ArgumentNullException.ThrowIfNull(view);

        if (views.Contains(view)) {
            throw new InvalidOperationException($"{view.GetType().Name} is already registered.");
        }

        views.Add(view);

        return new Removal(this, view);
    }

    /// <inheritdoc />
    public bool TryGetView(IEditableDocument document, [NotNullWhen(true)] out DocumentView? view) {
        ArgumentNullException.ThrowIfNull(document);

        for (var type = document.GetType(); type is not null; type = type.BaseType) {
            foreach (var candidate in views) {
                if (candidate.DocumentType == type && candidate.Accepts(document)) {
                    view = candidate;
                    return true;
                }
            }
        }

        view = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetView(Type documentType, [NotNullWhen(true)] out DocumentView? view) {
        ArgumentNullException.ThrowIfNull(documentType);

        for (var type = documentType; type is not null; type = type.BaseType) {
            foreach (var candidate in views) {
                if (candidate.DocumentType == type) {
                    view = candidate;
                    return true;
                }
            }
        }

        view = null;
        return false;
    }

    /// <inheritdoc />
    public UiElement? Show(IEditableDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);

        if (!TryGetView(document, out var view)) {
            return null;
        }

        panel.HostedDocument = document;
        DocumentCommands.Install(panel, Reverting is { } revert ? () => revert(document) : null);

        if (document.Location.Peek() is { Length: > 0 } location) {
            recent.Note(location);
        }

        var root = view.Create(document, panel);
        Shown?.Invoke(root, document);

        return root;
    }

    /// <inheritdoc />
    public bool Open(AssetId asset) => open?.Invoke(asset) ?? false;

    /// <inheritdoc />
    public void Place(string panelId) {
        ArgumentException.ThrowIfNullOrEmpty(panelId);

        var layout = Shell.Workspace.Host.Layout;

        if (layout.Find(panelId) is not null || layout.Find("scene") is not { } scene) {
            return;
        }

        scene.Group.Add(panelId);
    }

    /// <inheritdoc />
    public string SaveLayout() {
        // Named after the preset it came from until there is a dialog to ask. A "Save Layout…" that
        // silently overwrote the arrangement the user is looking at would be worse than one that
        // has an obvious placeholder name.
        var name = Shell.Workspace.DefaultPreset + " (custom)";

        Store.SaveLayout(name, Shell.Workspace.Save());
        Shell.Notifications.Success(name);

        return name;
    }

    /// <inheritdoc />
    public string ThemeTokens => tokens ??= Store.Read(ThemeFile) ?? string.Empty;

    /// <inheritdoc />
    public bool ApplyThemeTokens(string text) {
        ArgumentNullException.ThrowIfNull(text);

        tokens = text;

        try {
            Store.Write(ThemeFile, text);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the theme", NotificationSeverity.Error, exception.Message);
            return false;
        }

        Shell.Theme.LoadTokens(text.Length == 0 ? null : text);
        return true;
    }

    /// <inheritdoc />
    public void ReloadStyles() {
        LoadStyles();
        Shell.Notifications.Success("Styles reloaded");
    }

    /// <summary>Reads the theme tokens and restyles the editor with them, without saying so — what start-up does.</summary>
    public void LoadStyles() {
        tokens = null;
        Shell.Theme.LoadTokens(Store.Read(ThemeFile));
    }

    /// <summary>Writes the recent list to the user's store.</summary>
    /// <remarks>
    ///     ⚠ <b>A failure is said and not thrown</b>, against the store's own rule, and deliberately:
    ///     this runs from inside a document being opened or saved, and a full disk that cost the user
    ///     their recent list must not also cost them the save they asked for. The list stays right in
    ///     memory for the rest of the run.
    /// </remarks>
    void Keep(RecentDocuments list) {
        try {
            Store.Write(EditorUserStore.RecentDocumentsFile, string.Join('\n', list.Locations));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not keep the recent documents", NotificationSeverity.Warning, exception.Message);
        }
    }

    EditorShell Shell => shell ?? throw new InvalidOperationException("This workspace has no shell: it was built as a view list only.");

    EditorUserStore Store => store ?? throw new InvalidOperationException("This workspace has no user store: it was built as a view list only.");

    sealed class Removal(EditorWorkspace workspace, DocumentView view) : IDisposable {
        bool done;

        public void Dispose() {
            if (done) {
                return;
            }

            done = true;
            workspace.views.Remove(view);
        }
    }
}
