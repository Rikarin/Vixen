// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Styling;

/// <summary>A handle on one element's inline declarations.</summary>
/// <param name="Index">Its slot in the store.</param>
/// <remarks>
///     A type of its own rather than a <see cref="DeclarationRange" />, and the reason is a bug this
///     had before it was one. Inline declarations live in a different arena from rules': a stylesheet
///     reload throws the rules away and inline styles belong to elements, which outlive it. Both
///     arenas held <see cref="Declaration" />s, so a range into one was assignment-compatible with a
///     range into the other, and the resolver read inline styles out of the rule store — finding
///     whatever declarations happened to sit at that offset. Every test passed but the one that
///     asked what an inline style did.
/// </remarks>
public readonly record struct InlineStyleId(int Index) {
    /// <inheritdoc />
    public override string ToString() => "inline style " + Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The declarations written on elements themselves.</summary>
/// <remarks>
///     Separate from the rule set because the two are thrown away on completely different occasions.
///     A <c>.vcss</c> saved in the editor reloads every rule; the elements, and whatever a component
///     wrote directly onto them, are still there.
/// </remarks>
public sealed class InlineStyleStore {
    readonly List<Declaration> declarations = [];
    readonly List<DeclarationRange> blocks = [];

    /// <summary>How many blocks have been recorded.</summary>
    public int Count => blocks.Count;

    /// <summary>Records a block of declarations.</summary>
    /// <param name="block">The declarations.</param>
    /// <returns>A handle on them.</returns>
    public InlineStyleId Add(ReadOnlySpan<Declaration> block) {
        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        blocks.Add(new DeclarationRange(start, block.Length));
        return new InlineStyleId(blocks.Count - 1);
    }

    /// <summary>The declarations behind a handle.</summary>
    /// <param name="id">The handle.</param>
    /// <returns>The declarations.</returns>
    public ReadOnlySpan<Declaration> DeclarationsOf(InlineStyleId id) {
        var range = blocks[id.Index];
        return CollectionsMarshal.AsSpan(declarations).Slice(range.Start, range.Count);
    }
}
