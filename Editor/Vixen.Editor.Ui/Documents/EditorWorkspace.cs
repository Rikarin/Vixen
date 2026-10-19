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
///         ⚠ <b>Views only, so far.</b> Doc 50 also gives this the panels, the layouts and the
///         panel state the application parks on itself (§ F1's 25 fields); those are still
///         <c>EditorApplication</c>'s, and #1294 says what is left.
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
    ///     <see cref="DocumentCommands.Install" /> — so <c>document.save</c>, <c>document.revert</c>
    ///     and <c>document.close</c> answer for the tab the focus is in, which is #656's port reaching
    ///     the editor. A panel is built fresh each time it opens, so installing on it once is right.
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

    /// <summary>The durable state of one view — what it is looking at, which must outlive the panel showing it.</summary>
    /// <typeparam name="T">The view's own model.</typeparam>
    /// <param name="view">Which view, by the id its panel is registered under.</param>
    /// <returns>The one instance for that view, made the first time anything asks.</returns>
    /// <remarks>
    ///     <b>Doc 50 § D2's <c>IWorkspace.Views[i]</c>.</b> A panel is built afresh every time it is
    ///     opened, so whatever it keeps on its own elements goes with the old ones; the panel factory
    ///     reads this on build and writes through it, and nothing has to be parked on the application
    ///     in between. The model is the view's own type, because this assembly cannot name a scene pane.
    /// </remarks>
    T ViewState<T>(string view) where T : class, new();
}

/// <summary>The editor's <see cref="IWorkspace" />: a view list keyed by document type.</summary>
/// <param name="open">
///     How the host puts an asset in a panel of its own, or <see langword="null" /> for one with no
///     panels — whose <see cref="Open" /> then answers <see langword="false" />.
/// </param>
/// <remarks>
///     ⚠ <b>The panel half is the host's, handed in.</b> Opening an asset in a tab is docking, layout
///     and the asset-panel id, which are still <c>EditorApplication</c>'s (#1294 records the move as
///     owed), so this holds the call rather than the logic.
/// </remarks>
public sealed class EditorWorkspace(Func<AssetId, bool>? open = null) : IWorkspace {
    readonly List<DocumentView> views = [];
    readonly Dictionary<string, object> states = new(StringComparer.Ordinal);

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
        DocumentCommands.Install(panel);

        var root = view.Create(document, panel);
        Shown?.Invoke(root, document);

        return root;
    }

    /// <inheritdoc />
    public bool Open(AssetId asset) => open?.Invoke(asset) ?? false;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The view's state was first asked for as another type.</exception>
    public T ViewState<T>(string view) where T : class, new() {
        ArgumentException.ThrowIfNullOrEmpty(view);

        if (!states.TryGetValue(view, out var state)) {
            states[view] = state = new T();
        }

        return state as T
            ?? throw new InvalidOperationException(
                $"The '{view}' view keeps a {state.GetType().Name}, not a {typeof(T).Name}: two owners for one view's state."
            );
    }

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
