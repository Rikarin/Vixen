// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax;

/// <summary>
///     Raw kind values the shared tree reserves for itself.
/// </summary>
/// <remarks>
///     Kinds are plain integers here so one tree implementation can serve several
///     languages, each with its own enum. Almost nothing in this assembly reads the
///     value — list-ness is answered by <c>GreenNode.IsList</c>, not by comparing
///     kinds — but a list node still has to carry <em>some</em> kind, and a language
///     projecting <c>RawKind</c> back to its enum would otherwise land on whatever
///     member happened to occupy that slot.
/// </remarks>
public static class SyntaxKinds {
    /// <summary>
    ///     The anonymous list node. A language's kind enum must give this value to its
    ///     own list member (Raven: <c>SyntaxKind.ListKind = SyntaxKinds.List</c>) so
    ///     that casting a list node's <c>RawKind</c> yields the right name.
    /// </summary>
    public const int List = 1;
}
