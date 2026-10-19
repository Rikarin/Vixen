// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Ui.Documents;

/// <summary>What a document of one type is shown in: the Workspace half of what <c>IAssetEditorFactory</c> was.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D4, <a href="https://github.com/Rikarin/Vixen/issues/1294">#1294</a>.</b> The
///         kind — which files, how a document opens — is <c>Vixen.Editor.Core</c>'s
///         <c>DocumentKind</c>; this is the element. They are joined by the document's type and
///         nothing else, because this assembly deliberately cannot name <c>EditorDocument</c>: it
///         sees the document as <see cref="IEditableDocument" />, which is what #656's port made it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Create" /> may run more than once for one document.</b> A dock panel's
///         factory runs again when the panel is reopened, so nothing durable may live in a view. What
///         has to survive a reopen belongs on the document.
///     </para>
///     <para>
///         ⚠ <b>The type is not always enough, which is what <see cref="Accepts" /> is for.</b> The
///         scene and the prefab kinds both open a <c>SceneDocument</c> — the type is sealed, and what
///         tells them apart is the writer it was given — so two views are registered under one type
///         and each says which of the documents is its own. Doc 50 § D4's "keyed by document
///         <c>Type</c>" was measured against that and is right for the other twenty-six.
///     </para>
/// </remarks>
public abstract class DocumentView {
    /// <summary>The document type this view is for; a document of a type derived from it is found too.</summary>
    public abstract Type DocumentType { get; }

    /// <summary>Whether this view is the one for a document of its type.</summary>
    /// <param name="document">A document whose type is, or derives from, <see cref="DocumentType" />.</param>
    /// <returns><see langword="true" /> unless two views share a type and this one is not this document's.</returns>
    public virtual bool Accepts(IEditableDocument document) => true;

    /// <summary>Builds the controls into a panel.</summary>
    /// <param name="document">The document, of <see cref="DocumentType" />.</param>
    /// <param name="panel">Where the controls go.</param>
    /// <returns>The root of what was built, for a caller that wants to reach it.</returns>
    public abstract UiElement Create(IEditableDocument document, UiElement panel);
}

/// <summary>A <see cref="DocumentView" /> for one document type, so a view's body is typed.</summary>
/// <typeparam name="TDocument">The document type.</typeparam>
public abstract class DocumentView<TDocument> : DocumentView where TDocument : class, IEditableDocument {
    /// <inheritdoc />
    public sealed override Type DocumentType => typeof(TDocument);

    /// <inheritdoc />
    public sealed override bool Accepts(IEditableDocument document) =>
        document is TDocument typed && Owns(typed);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="document" /> is not a <typeparamref name="TDocument" />.</exception>
    public sealed override UiElement Create(IEditableDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);

        if (document is not TDocument typed) {
            // Not a defence against a caller, a defence against the registration: the workspace finds
            // a view by the document's type, so a mismatch here means the lookup and the view have
            // drifted apart.
            throw new ArgumentException(
                $"A view for {typeof(TDocument).Name} was handed a {document.GetType().Name}.",
                nameof(document)
            );
        }

        return Build(typed, panel);
    }

    /// <summary>Whether this view is the one for a document of its type.</summary>
    /// <param name="document">The document.</param>
    /// <returns><see langword="true" /> unless two views share a type.</returns>
    protected virtual bool Owns(TDocument document) => true;

    /// <summary>Builds the controls into a panel.</summary>
    /// <param name="document">The document.</param>
    /// <param name="panel">Where the controls go.</param>
    /// <returns>The root of what was built.</returns>
    protected abstract UiElement Build(TDocument document, UiElement panel);
}
