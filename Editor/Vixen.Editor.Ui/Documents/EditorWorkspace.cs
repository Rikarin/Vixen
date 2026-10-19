// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
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
}

/// <summary>The editor's <see cref="IWorkspace" />: a view list keyed by document type.</summary>
public sealed class EditorWorkspace : IWorkspace {
    readonly List<DocumentView> views = [];

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
