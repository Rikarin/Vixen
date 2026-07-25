// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Text;

namespace Vixen.Core.Syntax;

/// <summary>
///     What the shared tree needs to know about the file it came from.
/// </summary>
/// <remarks>
///     <para>
///         Parsing is the language's business — Raven drives ANTLR, VXML and VCSS will
///         hand-write descent parsers — so there is no shared <c>SyntaxTree</c> class to
///         inherit. <see cref="SyntaxNode.GetLocation" /> is the reason this exists at
///         all: a node knows its span but not its file or where the lines break, and it
///         reaches both through here.
///     </para>
///     <para>
///         An interface rather than a base class, deliberately. Each language names its
///         own type <c>SyntaxTree</c>; a shared base of the same name would make every
///         file that imports both namespaces ambiguous.
///     </para>
/// </remarks>
public interface ISyntaxTree {
    /// <summary>Path this tree was parsed from, or empty when it has no file.</summary>
    string FilePath { get; }

    /// <summary>The source text, or null when the tree was built from nodes rather than parsed.</summary>
    SourceText? Text { get; }
}
