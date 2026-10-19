// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>What a document kind is handed when something asks for an asset to be opened.</summary>
/// <param name="Project">The project it belongs to.</param>
/// <param name="Asset">Its identity, which is what a reopened tab is found by.</param>
/// <param name="Path">Where the file is, absolute.</param>
/// <remarks>
///     The path is resolved by the host rather than by each kind, because <see cref="AssetDatabase" />
///     stores project-relative paths and a kind that did its own combining is a kind that would
///     eventually do it differently from the next one.
/// </remarks>
public sealed record DocumentRequest(EditorProject Project, AssetId Asset, string Path);

/// <summary>One kind of document: which files it claims and how it opens one. No view — that is <c>DocumentView</c>'s.</summary>
/// <remarks>
///     <para>
///         <b>The Documents half of what <c>IAssetEditorFactory</c> used to be</b> — doc 50 § D4,
///         <a href="https://github.com/Rikarin/Vixen/issues/1294">#1294</a>. That interface carried
///         <c>Open</c> (the project and the document: this assembly's words) and <c>CreateView</c>
///         (an element: <c>Vixen.Ui</c>'s) on one type, deliberately, because "a document with no
///         view is a model nobody can reach and a view with no document is a control with nothing
///         behind it". The pairing is right as a <i>product</i> invariant and wrong as a <i>type</i>:
///         it put two contexts' words on one interface, and no assembly could own it. So the kind is
///         here, the view is in <c>Vixen.Editor.Ui</c>, and the invariant is a test —
///         <c>DocumentPairingTests</c> — over the two registries a running editor actually holds.
///     </para>
///     <para>
///         ⚠ <b><see cref="DocumentTypes" /> is the join, and it is a list</b> because one kind can
///         open more than one type: the markup kind opens a <c>MarkupDocument</c> or a
///         <c>StyleSheetDocument</c> by extension. <see cref="DocumentHost.TryOpen" /> refuses a
///         document of a type the kind did not declare, which is the runtime half of the pairing — a
///         kind that drifted from what it declares would otherwise reach a view lookup that fails
///         for a reason nobody can see from the double-click.
///     </para>
/// </remarks>
public abstract class DocumentKind {
    /// <summary>What it is called, in a menu and in a message about two of them clashing.</summary>
    public abstract string Name { get; }

    /// <summary>The extensions it claims, each with its leading dot.</summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>Every type <see cref="Open" /> may return; a view is found for each by <c>DocumentView</c>.</summary>
    public abstract IReadOnlyList<Type> DocumentTypes { get; }

    /// <summary>Opens an asset as a document.</summary>
    /// <param name="request">What to open.</param>
    /// <returns>The document, already registered with the project by <see cref="EditorDocument" />'s constructor.</returns>
    public abstract EditorDocument Open(DocumentRequest request);

    /// <summary>Whether a document is one of <see cref="DocumentTypes" /> or derives from one.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether this kind declares it.</returns>
    public bool Declares(EditorDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var type in DocumentTypes) {
            if (type.IsInstanceOfType(document)) {
                return true;
            }
        }

        return false;
    }
}
