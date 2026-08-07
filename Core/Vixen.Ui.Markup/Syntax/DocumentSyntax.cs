// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Syntax;

/// <summary>
///     The five questions everything asks a document's headers, answered over the one list they are
///     actually stored in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Computed rather than stored, and that is the whole of the fix.</b> A field each put
///         the headers in the tree in *field* order, so a file whose <c>@using</c> came above its
///         <c>@namespace</c> printed back with them the other way round — and byte-exact round trip
///         is the property the incremental reparse, the formatter and every span-to-source mapping
///         rest on. Storing them in source order and asking questions of the list costs a walk of
///         four nodes and cannot disagree with the file.
///     </para>
///     <para>
///         Each is a first-one-wins search, which is what the parser guarantees: a second
///         <c>@namespace</c> or <c>@tag</c> stops the header loop and is reported as a stray
///         directive, so it never reaches this list.
///     </para>
/// </remarks>
public sealed partial class DocumentSyntax {
    /// <summary>The <c>@component</c> header. Absent only in a file the binder rejects.</summary>
    public ComponentDirectiveSyntax? Component => First<ComponentDirectiveSyntax>();

    /// <summary>The <c>@namespace</c> header, if the file names one.</summary>
    /// <remarks>Absent means the project's root namespace is used instead.</remarks>
    public NamespaceDirectiveSyntax? Namespace => First<NamespaceDirectiveSyntax>();

    /// <summary>The <c>@tag</c> header, if the file names one.</summary>
    /// <remarks>
    ///     Absent means the component's host element takes the type's name in lower case.
    /// </remarks>
    public TagDirectiveSyntax? Tag => First<TagDirectiveSyntax>();

    /// <summary>The <c>@inherits</c> header, if the file names a base.</summary>
    /// <remarks>
    ///     Absent means <c>Component</c>. Present means the generated class is a
    ///     <c>UiElement</c> instead, and the emitter writes an element-flavoured scaffold — see
    ///     <see cref="Emit.ComponentEmitter" />.
    /// </remarks>
    public InheritsDirectiveSyntax? Inherits => First<InheritsDirectiveSyntax>();

    /// <summary>The <c>@using</c> headers, in source order.</summary>
    /// <remarks>
    ///     The one header a file may write more than once, so the only one of the four that is a
    ///     sequence rather than a search.
    /// </remarks>
    public IEnumerable<UsingDirectiveSyntax> Usings {
        get {
            foreach (var directive in Directives) {
                if (directive is UsingDirectiveSyntax @using) {
                    yield return @using;
                }
            }
        }
    }

    T? First<T>() where T : DirectiveSyntax {
        foreach (var directive in Directives) {
            if (directive is T found) {
                return found;
            }
        }

        return null;
    }
}
