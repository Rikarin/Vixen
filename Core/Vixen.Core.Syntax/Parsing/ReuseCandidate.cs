// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Parsing;

/// <summary>A node a front end offers to <see cref="Blender" />, and where it may be handed back.</summary>
/// <param name="Node">A node from the previous tree.</param>
/// <param name="Context">
///     Which of the front end's parse loops produced <paramref name="Node" />. The numbering is the
///     language's own — the blender only compares it for equality against the value a reuse site
///     names — and it exists because a node is bound to the grammar that read it, not merely to the
///     characters underneath it. Two loops reading the same tokens can build different trees, so a
///     candidate that matches on position and lexes identically is still the wrong answer when it
///     came out of a loop the parser is not currently in.
/// </param>
/// <param name="Reach">
///     How far into the text, as an absolute offset, the parse that built <paramref name="Node" />
///     looked — the end of the furthest token it consumed, peeked or scanned — or a negative value
///     when the front end does not know. A node's parse can decide its shape on tokens well past its
///     own end (Raven's array-rank scan runs to the end of the line), so an edit between the node
///     and that point changes what a fresh parse would build without touching a character the node
///     owns. The blender treats a candidate as reaching to whichever of its span and this is
///     further; see <c>SyntaxParser.Reach</c> for where the number comes from.
/// </param>
readonly record struct ReuseCandidate(SyntaxNode Node, int Context, int Reach = -1);
